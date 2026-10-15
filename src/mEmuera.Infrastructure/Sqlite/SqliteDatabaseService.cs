using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using mEmuera.Contracts;
using mEmuera.Infrastructure.FileSystem;

namespace mEmuera.Infrastructure.Sqlite;

public sealed partial class SqliteDatabaseService(GamePathResolver paths) : IDatabaseService, IScriptDatabaseService
{
    private sealed class Connection(SqliteConnection connection, Guid owner)
    {
        public SqliteConnection Db = connection;
        public Guid Owner = owner;
        public SqliteTransaction? Transaction;
    }
    private sealed record Reader(SqliteCommand Command, SqliteDataReader Data, DatabaseHandle Connection);
    private readonly Dictionary<long, Connection> connections = [];
    private readonly Dictionary<long, Reader> readers = [];
    private readonly SemaphoreSlim gate = new(1);
    private long next;
    private Connection Get(DatabaseHandle handle, Guid owner)
    {
        if (handle.Owner != owner || !connections.TryGetValue(handle.Id, out var connection) || connection.Owner != owner)
            throw new ScriptException("DB_HANDLE", "数据库句柄已失效或属于其他会话");
        return connection;
    }
    public async ValueTask<DatabaseData> ExecuteAsync(DatabasePayload request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        Connection? active = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (request.Operation == DbOperation.Open)
            {
                var spec = request.Spec ?? throw new ScriptException("DB_SPEC", "缺少数据库配置");
                var handle = await OpenConnectionAsync(request.Owner, new SqliteConnectionStringBuilder
                { DataSource = spec.Path == ":memory:" ? ":memory:" : paths.Database(spec.Path, spec.Writable), Mode = spec.Writable ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadOnly, Pooling = false }, ct);
                return new(Handle: handle);
            }
            if (request.Operation is DbOperation.Read or DbOperation.CloseReader)
            {
                if (request.Handle.Owner != request.Owner || !readers.TryGetValue(request.Handle.Id, out var reader)) throw new ScriptException("DB_READER", "reader 已失效");
                active = Get(reader.Connection, request.Owner);
                if (request.Operation == DbOperation.CloseReader) { await CloseReader(request.Handle.Id); return new(); }
                if (request.Limit is <= 0 or > 4096) throw new ScriptException("DB_BATCH", "读取批量须在 1 到 4096 之间");
                var rows = ImmutableArray.CreateBuilder<ImmutableArray<DbValue>>(); bool end = false;
                while (rows.Count < request.Limit)
                {
                    if (!await reader.Data.ReadAsync(ct)) { end = true; break; }
                    var row = ImmutableArray.CreateBuilder<DbValue>();
                    for (int i = 0; i < reader.Data.FieldCount; i++) row.Add(reader.Data.GetValue(i) switch
                    {
                        DBNull => new(DbKind.Null), long n => new(DbKind.Integer, Integer: n), double n => new(DbKind.Double, Double: n),
                        string s => new(DbKind.Text, Text: s), byte[] bytes => new(DbKind.Blob, Blob: bytes.ToImmutableArray()),
                        _ => throw new ScriptException("DB_TYPE", "不支持的 SQLite 数据类型")
                    });
                    rows.Add(row.ToImmutable());
                }
                return new(Batch: new(rows.ToImmutable(), end));
            }
            active = Get(request.Handle, request.Owner);
            switch (request.Operation)
            {
                case DbOperation.Begin:
                    if (active.Transaction is not null) throw new ScriptException("DB_TRANSACTION", "事务已经开始");
                    active.Transaction = active.Db.BeginTransaction(); return new();
                case DbOperation.Commit: case DbOperation.Rollback:
                    if (active.Transaction is null) throw new ScriptException("DB_TRANSACTION", "没有活动事务");
                    if (request.Operation == DbOperation.Commit) active.Transaction.Commit(); else active.Transaction.Rollback();
                    active.Transaction.Dispose(); active.Transaction = null; return new();
                case DbOperation.Close:
                    foreach (long id in readers.Where(p => p.Value.Connection == request.Handle).Select(p => p.Key).ToArray()) await CloseReader(id);
                    active.Transaction?.Dispose(); await active.Db.DisposeAsync(); connections.Remove(request.Handle.Id); return new();
                case DbOperation.Execute: case DbOperation.Query:
                    var command = active.Db.CreateCommand(); command.CommandText = request.Sql; command.Transaction = active.Transaction;
                    try
                    {
                        if (!request.Parameters.IsDefault)
                            foreach (var parameter in request.Parameters) command.Parameters.AddWithValue(parameter.Name, ConvertValue(parameter.Value));
                        if (request.Operation == DbOperation.Execute) { long affected = await command.ExecuteNonQueryAsync(ct); await command.DisposeAsync(); return new(Affected: affected); }
                        var data = await command.ExecuteReaderAsync(ct);
                        var handle = new DatabaseHandle(request.Owner, ++next); readers.Add(handle.Id, new(command, data, request.Handle));
                        return new(Query: new(handle, Enumerable.Range(0, data.FieldCount).Select(data.GetName).ToImmutableArray()));
                    }
                    catch { await command.DisposeAsync(); throw; }
                default: throw new ScriptException("DB_OPERATION", "未知数据库操作");
            }
        }
        catch (Exception e) when (e is SqliteException or OperationCanceledException)
        {
            if (request.Operation == DbOperation.Read && readers.ContainsKey(request.Handle.Id)) await CloseReader(request.Handle.Id);
            if (active?.Transaction is { } transaction)
            { transaction.Rollback(); transaction.Dispose(); active.Transaction = null; }
            throw;
        }
        finally { gate.Release(); }
    }
    private async ValueTask<DatabaseHandle> OpenConnectionAsync(Guid owner, SqliteConnectionStringBuilder specification, CancellationToken ct)
    {
        var connection = new SqliteConnection(specification.ToString());
        try { await connection.OpenAsync(ct); }
        catch { await connection.DisposeAsync(); throw; }
        var handle = new DatabaseHandle(owner, ++next);
        connections.Add(handle.Id, new(connection, owner)); return handle;
    }
    private static object ConvertValue(DbValue value) => value.Kind switch
    {
        DbKind.Null => DBNull.Value, DbKind.Integer => value.Integer, DbKind.Double => value.Double,
        DbKind.Text => value.Text ?? throw new ScriptException("DB_TYPE", "Text 不能为 null"),
        DbKind.Blob => value.Blob.IsDefault ? throw new ScriptException("DB_TYPE", "Blob 缺失") : value.Blob.ToArray(),
        _ => throw new ScriptException("DB_TYPE", "未知数据库值类型")
    };
    private async ValueTask CloseReader(long id)
    { var reader = readers[id]; readers.Remove(id); await reader.Data.DisposeAsync(); await reader.Command.DisposeAsync(); }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            foreach (long id in readers.Keys.ToArray()) await CloseReader(id);
            foreach (var connection in connections.Values) { connection.Transaction?.Dispose(); await connection.Db.DisposeAsync(); }
            connections.Clear();
        }
        finally { gate.Release(); }
    }
}
