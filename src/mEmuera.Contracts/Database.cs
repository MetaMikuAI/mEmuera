using System.Collections.Immutable;

namespace mEmuera.Contracts;

public enum DbKind { Null, Integer, Double, Text, Blob }
public sealed record DbValue(DbKind Kind, long Integer = 0, double Double = 0, string? Text = null, ImmutableArray<byte> Blob = default);
public readonly record struct DatabaseHandle(Guid Owner, long Id);
public sealed record DatabaseSpec(string Path, bool Writable);
public sealed record DbParameter(string Name, DbValue Value);
public sealed record DbQuery(DatabaseHandle Reader, ImmutableArray<string> Columns);
public sealed record DbBatch(ImmutableArray<ImmutableArray<DbValue>> Rows, bool End);
public enum DbOperation { Open, Execute, Query, Read, CloseReader, Begin, Commit, Rollback, Close }
public sealed record DatabasePayload(Guid Owner, DbOperation Operation, DatabaseHandle Handle = default,
    DatabaseSpec? Spec = null, string Sql = "", ImmutableArray<DbParameter> Parameters = default, int Limit = 128) : ServicePayload;
public sealed record DatabaseData(DatabaseHandle? Handle = null, long? Affected = null, DbQuery? Query = null, DbBatch? Batch = null) : ServiceData;
public interface IDatabaseService : IAsyncDisposable
{
    ValueTask<DatabaseData> ExecuteAsync(DatabasePayload request, CancellationToken ct = default);
}
public sealed record ScriptDatabasePayload(string Operation, ImmutableArray<EraValue> Arguments, ImmutableArray<bool> NullArguments = default) : ServicePayload;
public sealed record BuiltinValueData(EraValue Value) : ServiceData;
public interface IScriptDatabaseService
{
    ValueTask<EraValue> ExecuteScriptAsync(ScriptDatabasePayload request, CancellationToken ct = default);
}
