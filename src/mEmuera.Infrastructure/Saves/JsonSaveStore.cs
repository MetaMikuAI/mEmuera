using System.Text.Json;
using System.Collections.Immutable;
using mEmuera.Contracts;
using mEmuera.Infrastructure.FileSystem;

namespace mEmuera.Infrastructure.Saves;

public sealed class JsonSaveStore(string dataRoot, Action<string>? checkpoint = null) : ISaveStore
{
    private readonly string root = Path.GetFullPath(dataRoot);
    private readonly SemaphoreSlim gate = new(1);
    private string PathFor(SaveKey key)
    {
        if (key.Slot < 0 || string.IsNullOrWhiteSpace(key.GameId)) throw new IOException("存档标识错误");
        string game = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key.GameId)));
        string filename = key.Scope switch
        {
            SaveScope.Global => "global",
            SaveScope.Character => "chara_" + CharacterName(key.Name),
            _ => key.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return GamePathResolver.Resolve(root, $"saves/{game}/{filename}.memsave.json");
    }
    private static string CharacterName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ScriptException("SAVE_NAME", "角色存档名称无效");
        return name;
    }
    public ValueTask<ImmutableArray<string>> FindCharactersAsync(string gameId, string pattern, CancellationToken ct = default)
    {
        if (pattern.Any(c => c < 32 || "<>:\"/\\|".Contains(c))) throw new ScriptException("SAVE_NAME", "角色存档查询模式无效");
        string directory = Path.GetDirectoryName(PathFor(new(gameId, 0)))!;
        if (!Directory.Exists(directory)) return ValueTask.FromResult(ImmutableArray<string>.Empty);
        var names = ImmutableArray.CreateBuilder<string>();
        foreach (string path in Directory.EnumerateFiles(directory, "chara_*.memsave.json"))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path)[6..^13];
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, OperatingSystem.IsWindows())) names.Add(name);
        }
        return ValueTask.FromResult(names.ToImmutable());
    }
    public async ValueTask<SaveResult> WriteAsync(SaveKey key, SaveSnapshot snapshot, CancellationToken ct = default)
    {
        string? temporary = null; bool committed = false;
        await gate.WaitAsync(ct);
        try
        {
            Validate(snapshot, key);
            string path = PathFor(key); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (checkpoint is null) await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: ct);
                else
                {
                    byte[] fixture = JsonSerializer.SerializeToUtf8Bytes(snapshot);
                    await stream.WriteAsync(fixture.AsMemory(0, fixture.Length / 2), ct);
                    checkpoint("write");
                    await stream.WriteAsync(fixture.AsMemory(fixture.Length / 2), ct);
                }
                if (stream.Length > 256L * 1024 * 1024) throw new ScriptException("SAVE_LIMIT", "存档文件超过 256 MiB");
                checkpoint?.Invoke("flush"); await stream.FlushAsync(ct); stream.Flush(true);
            }
            await using (var stream = File.OpenRead(temporary)) Validate((await JsonSerializer.DeserializeAsync<SaveSnapshot>(stream, cancellationToken: ct))!, key);
            ct.ThrowIfCancellationRequested(); checkpoint?.Invoke("replace"); ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
            committed = true; checkpoint?.Invoke("committed");
            return new(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ScriptException or OperationCanceledException)
        { return new(committed, committed ? null : new("SAVE_WRITE", e.Message)); }
        finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
    public async ValueTask<LoadResult> ReadAsync(SaveKey key, CancellationToken ct = default)
    {
        try
        {
            await using var stream = File.OpenRead(PathFor(key));
            if (stream.Length > 256L * 1024 * 1024) throw new ScriptException("SAVE_LIMIT", "存档文件超过 256 MiB");
            var snapshot = await JsonSerializer.DeserializeAsync<SaveSnapshot>(stream, cancellationToken: ct);
            Validate(snapshot!, key); return new(snapshot);
        }
        catch (FileNotFoundException) { return new(null, new("SAVE_MISSING", "存档不存在")); }
        catch (DirectoryNotFoundException) { return new(null, new("SAVE_MISSING", "存档不存在")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ScriptException)
        { return new(null, new("SAVE_READ", e.Message)); }
    }
    public async ValueTask DeleteAsync(SaveKey key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            string path = PathFor(key);
            if (File.Exists(path)) { ct.ThrowIfCancellationRequested(); File.Delete(path); }
        }
        finally { gate.Release(); }
    }
    private static void Validate(SaveSnapshot snapshot, SaveKey key)
    {
        if (snapshot is null || snapshot.SchemaVersion is not (1 or 2) || snapshot.FlowVersion != 1 || snapshot.GameId != key.GameId
            || string.IsNullOrEmpty(snapshot.CompatibilityProfile) || snapshot.Variables.IsDefault)
            throw new ScriptException("SAVE_SCHEMA", "存档格式或游戏标识不匹配");
        var names = new HashSet<string>();
        foreach (var variable in snapshot.Variables)
            if (variable is null || string.IsNullOrWhiteSpace(variable.Name) || !names.Add(variable.Name) || variable.Length <= 0 || variable.Values.IsDefault || variable.Values.Length != variable.Length
                || !Enum.IsDefined(variable.Kind) || variable.Values.Any(v => v.Kind != variable.Kind || v.Kind == ValueKind.String && v.Text is null))
                throw new ScriptException("SAVE_VARIABLES", "存档变量数据不完整");
        if (snapshot.Characters.IsDefault || snapshot.Characters.Length > 10000 || snapshot.Characters.Any(c => c is null || c.Id <= 0 || c.Variables.IsDefault)
            || snapshot.Characters.Select(c => c.Id).Distinct().Count() != snapshot.Characters.Length)
            throw new ScriptException("SAVE_CHARACTERS", "存档角色数据不完整");
    }
}
