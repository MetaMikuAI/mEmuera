using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Xml;
using mEmuera.Contracts;
using Microsoft.Data.Sqlite;

namespace mEmuera.Infrastructure.Sqlite;

public sealed partial class SqliteDatabaseService
{
    private readonly Guid scriptOwner = Guid.NewGuid();
    private readonly SemaphoreSlim scriptGate = new(1);
    private readonly Dictionary<string, DatabaseHandle> scriptConnections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, DatabaseHandle> scriptReaders = [];
    private readonly Dictionary<long, DatabaseHandle> scriptReaderConnections = [];
    private readonly Dictionary<long, ImmutableArray<DbValue>> scriptRows = [];
    public async ValueTask<EraValue> ExecuteScriptAsync(ScriptDatabasePayload request, CancellationToken ct = default)
    {
        await scriptGate.WaitAsync(ct);
        try
        {
            var a = request.Arguments; string operation = request.Operation;
            var parameters = ImmutableArray<DbParameter>.Empty;
            if (operation.StartsWith("SQL_P_", StringComparison.Ordinal))
            {
                parameters = a.Skip(2).Select((value, i) => new DbParameter("@" + i,
                    !request.NullArguments.IsDefault && request.NullArguments[i + 2] ? new(DbKind.Null) : new(DbKind.Text, Text: value.AsString()))).ToImmutableArray();
                operation = "SQL_" + operation[6..];
            }
            if (operation == "SQL_CONNECT")
            {
                string name = a[0].AsString();
                if (scriptConnections.ContainsKey(name)) return new(1L);
                var connection = new SqliteConnectionStringBuilder(a[1].AsString());
                if (connection.DataSource.Length == 0) throw new ScriptException("DB_SPEC", "缺少数据库路径");
                if (connection.DataSource != ":memory:" && connection.Mode != SqliteOpenMode.Memory)
                    connection.DataSource = paths.Database(connection.DataSource, connection.Mode != SqliteOpenMode.ReadOnly);
                await gate.WaitAsync(ct);
                try { scriptConnections.Add(name, await OpenConnectionAsync(scriptOwner, connection, ct)); }
                finally { gate.Release(); }
                return new(1L);
            }
            if (operation.StartsWith("SQL_READER_", StringComparison.Ordinal))
            {
                long id = a[0].AsInt();
                if (!scriptReaders.TryGetValue(id, out var reader))
                {
                    if (operation is "SQL_READER_READ" or "SQL_READER_CLOSE") return new(operation == "SQL_READER_READ" ? 0L : 1L);
                    throw new ScriptException("DB_READER", "SQL reader 已失效");
                }
                if (operation == "SQL_READER_CLOSE")
                { await ExecuteAsync(new(scriptOwner, DbOperation.CloseReader, reader), ct); ForgetScriptReader(id); return new(1L); }
                if (operation == "SQL_READER_READ")
                {
                    var batch = (await ExecuteAsync(new(scriptOwner, DbOperation.Read, reader, Limit: 1), ct)).Batch!;
                    if (batch.Rows.Length == 0) { scriptRows.Remove(id); return new(0L); }
                    scriptRows[id] = batch.Rows[0]; return new(1L);
                }
                long column = a[1].AsInt();
                if (!scriptRows.TryGetValue(id, out var row) || column < 0 || column >= row.Length) throw new ScriptException("DB_COLUMN", "SQL 当前行或列无效");
                var value = row[(int)column];
                if (operation is "SQL_READER_IS_NULL" or "SQL_READER_ISNULL") return new(value.Kind == DbKind.Null ? 1L : 0L);
                if (operation is "SQL_READER_GET_LONG" or "SQL_READER_GET_STRING")
                    return await ReadScriptColumnAsync(reader, (int)column, operation == "SQL_READER_GET_STRING" ? ValueKind.String : ValueKind.Integer, ct);
                return ConvertScriptValue(value, operation.EndsWith("STRING", StringComparison.Ordinal) ? ValueKind.String : operation.EndsWith("FLOAT", StringComparison.Ordinal) ? ValueKind.Float : ValueKind.Integer);
            }
            if (!scriptConnections.TryGetValue(a[0].AsString(), out var handle))
            {
                if (operation == "SQL_IMPORT_MAP_XML") return new(0L);
                if (operation == "SQL_DISCONNECT") return new(1L);
                throw new ScriptException("DB_CONNECTION", "数据库尚未连接");
            }
            if (operation == "SQL_DISCONNECT")
            {
                await ExecuteAsync(new(scriptOwner, DbOperation.Close, handle), ct);
                foreach (long id in scriptReaderConnections.Where(p => p.Value == handle).Select(p => p.Key).ToArray()) ForgetScriptReader(id);
                scriptConnections.Remove(a[0].AsString()); return new(1L);
            }
            if (operation == "SQL_IMPORT_MAP_XML") return await ImportMapAsync(handle, a[1].AsString(), a[2].AsString(), ct);
            if (operation == "SQL_EXECUTE_NONQUERY")
                return new((await ExecuteAsync(new(scriptOwner, DbOperation.Execute, handle, Sql: a[1].AsString(), Parameters: parameters), ct)).Affected!.Value);
            if (operation is not ("SQL_EXECUTE_READER" or "SQL_EXECUTE_SCALAR_LONG" or "SQL_EXECUTE_SCALAR_STRING" or "SQL_EXECUTE_SCALAR_FLOAT"))
                throw new ScriptException("DB_OPERATION", "未实现 SQL 操作: " + operation);
            var query = (await ExecuteAsync(new(scriptOwner, DbOperation.Query, handle, Sql: a[1].AsString(), Parameters: parameters), ct)).Query!;
            if (operation == "SQL_EXECUTE_READER") { scriptReaders[query.Reader.Id] = query.Reader; scriptReaderConnections[query.Reader.Id] = handle; return new(query.Reader.Id); }
            try
            {
                var rows = (await ExecuteAsync(new(scriptOwner, DbOperation.Read, query.Reader, Limit: 1), ct)).Batch!.Rows;
                return ConvertScriptValue(rows.IsEmpty || rows[0].IsEmpty ? new(DbKind.Null) : rows[0][0], operation.EndsWith("STRING", StringComparison.Ordinal) ? ValueKind.String : operation.EndsWith("FLOAT", StringComparison.Ordinal) ? ValueKind.Float : ValueKind.Integer);
            }
            finally { await ExecuteAsync(new(scriptOwner, DbOperation.CloseReader, query.Reader), CancellationToken.None); }
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException or XmlException)
        { throw new ScriptException("DB_VALUE", "数据库数据转换失败: " + error.Message); }
        finally { scriptGate.Release(); }
    }
    private void ForgetScriptReader(long id)
    { scriptReaders.Remove(id); scriptReaderConnections.Remove(id); scriptRows.Remove(id); }
    private async ValueTask<EraValue> ReadScriptColumnAsync(DatabaseHandle handle, int column, ValueKind kind, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!readers.TryGetValue(handle.Id, out var reader)) throw new ScriptException("DB_READER", "SQL reader 已失效");
            Get(reader.Connection, scriptOwner);
            if (reader.Data.IsDBNull(column)) return EraValue.Zero(kind);
            return kind == ValueKind.String ? new(reader.Data.GetString(column)) : new(reader.Data.GetInt64(column));
        }
        finally { gate.Release(); }
    }
    private static EraValue ConvertScriptValue(DbValue value, ValueKind kind)
    {
        if (value.Kind == DbKind.Null) return EraValue.Zero(kind);
        object scalar = value.Kind switch { DbKind.Integer => value.Integer, DbKind.Double => value.Double, DbKind.Text => value.Text!, _ => throw new ScriptException("DB_TYPE", "此操作不接受二进制列") };
        return kind switch { ValueKind.String => new(Convert.ToString(scalar, CultureInfo.InvariantCulture)!), ValueKind.Float => new(Convert.ToDouble(scalar, CultureInfo.InvariantCulture)), _ => new(Convert.ToInt64(scalar, CultureInfo.InvariantCulture)) };
    }
    private async ValueTask<EraValue> ImportMapAsync(DatabaseHandle handle, string table, string file, CancellationToken ct)
    {
        string identifier = QuoteTable(table);
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(paths.Source(file), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000_000 })) doc.Load(reader);
        await ExecuteAsync(new(scriptOwner, DbOperation.Begin, handle), ct);
        try
        {
            await ExecuteAsync(new(scriptOwner, DbOperation.Execute, handle, Sql: $"CREATE TABLE IF NOT EXISTS {identifier} (k TEXT PRIMARY KEY, v TEXT)"), ct);
            foreach (XmlNode node in doc.SelectNodes("/map/p")!)
            {
                ct.ThrowIfCancellationRequested();
                if (node.SelectSingleNode("k") is not { } key || node.SelectSingleNode("v") is not { } value) continue;
                await ExecuteAsync(new(scriptOwner, DbOperation.Execute, handle, Sql: $"INSERT OR REPLACE INTO {identifier} (k,v) VALUES (@k,@v)",
                    Parameters: [new("@k", new(DbKind.Text, Text: key.InnerText)), new("@v", new(DbKind.Text, Text: value.InnerXml))]), ct);
            }
            await ExecuteAsync(new(scriptOwner, DbOperation.Commit, handle), ct); return new(1L);
        }
        catch
        {
            if (Get(handle, scriptOwner).Transaction is not null) await ExecuteAsync(new(scriptOwner, DbOperation.Rollback, handle), CancellationToken.None);
            throw;
        }
    }
    private static string QuoteTable(string table)
    {
        var parts = new List<string>();
        var part = new StringBuilder(); char quote = '\0';
        for (int i = 0; i < table.Length; i++)
        {
            char c = table[i];
            if (quote != '\0')
            {
                part.Append(c);
                if (c != quote) continue;
                if (i + 1 < table.Length && table[i + 1] == quote) { part.Append(table[++i]); continue; }
                quote = '\0'; continue;
            }
            if (c == '.') { parts.Add(part.ToString().Trim()); part.Clear(); continue; }
            if (c is '\'' or '"' or '`' or '[') quote = c == '[' ? ']' : c;
            part.Append(c);
        }
        if (quote != '\0') throw new ScriptException("DB_TABLE", "表名引号未闭合");
        parts.Add(part.ToString().Trim());
        if (parts.Count is < 1 or > 2) throw new ScriptException("DB_TABLE", "表名须为名称或数据库名.名称");
        return string.Join('.', parts.Select(QuotePart));
    }
    private static string QuotePart(string part)
    {
        if (part.Length == 0) throw new ScriptException("DB_TABLE", "表名不能为空");
        char open = part[0], close = open == '[' ? ']' : open;
        if (open is '\'' or '"' or '`' or '[')
        {
            if (part.Length < 2 || part[^1] != close) throw new ScriptException("DB_TABLE", "表名引号无效");
            string contents = part[1..^1];
            var value = new StringBuilder();
            for (int i = 0; i < contents.Length; i++)
            {
                char c = contents[i];
                if (c == close && (i + 1 >= contents.Length || contents[++i] != close)) throw new ScriptException("DB_TABLE", "表名引号无效");
                value.Append(c);
            }
            part = value.ToString();
        }
        else if ((!char.IsLetter(part[0]) && part[0] != '_' && part[0] < 128)
            || part.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '$') && c < 128))
            throw new ScriptException("DB_TABLE", "表名包含无效字符");
        return "\"" + part.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
