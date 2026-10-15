using System.Collections.Immutable;
using System.Globalization;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.Saves.Legacy;

public sealed record LegacySaveFile(int Slot, string Path);
public sealed record LegacyImportResult(LegacySaveFile File, bool Imported, string Message)
{
    public LegacySave? Save { get; init; }
    public LegacyConversion? Conversion { get; init; }
}

/// <summary>把旧模拟器的普通存档转换进新存档；旧文件只读，已有存档的槽位不覆盖。</summary>
public static class LegacySaveImporter
{
    /// <summary>列出目录中的 saveNN.sav；global.sav 等其他文件不在导入范围内。</summary>
    public static ImmutableArray<LegacySaveFile> Discover(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<LegacySaveFile>();
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = System.IO.Path.GetFileName(path);
            if (name.Length <= 8 || !name.StartsWith("save", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)) continue;
            string digits = name[4..^4];
            if (digits.Length <= 9 && digits.All(char.IsAsciiDigit)) result.Add(new(int.Parse(digits, CultureInfo.InvariantCulture), path));
        }
        return [.. result.OrderBy(f => f.Slot)];
    }
    /// <param name="validate">用目标游戏校验转换结果，例如 GameSession.ValidateSave。</param>
    public static async Task<LegacyImportResult> ImportAsync(LegacySaveFile file, GameDefinition game, ISaveStore store,
        Action<SaveSnapshot> validate, CancellationToken ct = default)
    {
        var key = new SaveKey(game.GameId, file.Slot);
        var existing = await store.ReadAsync(key, ct);
        if (existing.Error?.Code != "SAVE_MISSING") return new(file, false, "目标槽位已有存档");
        LegacySave legacy;
        await using (var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read)) legacy = Era1808Reader.Read(input, ct);
        var converted = Era1808Converter.Convert(legacy, game);
        validate(converted.Snapshot);
        var written = await store.WriteAsync(key, converted.Snapshot, ct);
        if (!written.Committed) throw new ScriptException("LEGACY_WRITE", written.Error?.Message ?? "写入失败");
        return new(file, true, "已导入") { Save = legacy, Conversion = converted };
    }
    /// <summary>逐个导入；单个文件失败不影响其他文件，失败原因写在结果里。</summary>
    public static async Task<ImmutableArray<LegacyImportResult>> ImportAllAsync(IEnumerable<LegacySaveFile> files, GameDefinition game, ISaveStore store,
        Action<SaveSnapshot> validate, CancellationToken ct = default)
    {
        var results = ImmutableArray.CreateBuilder<LegacyImportResult>();
        foreach (var file in files)
        {
            try { results.Add(await ImportAsync(file, game, store, validate, ct)); }
            catch (Exception e) when (e is ScriptException or IOException or UnauthorizedAccessException)
            { results.Add(new(file, false, e.Message)); }
        }
        return results.ToImmutable();
    }
}
