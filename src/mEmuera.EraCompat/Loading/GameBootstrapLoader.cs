using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Loading;

public static class GameBootstrapLoader
{
    public sealed record CsvRow(string[] Fields, int Line, int Offset, int Length);
    // 旧 GameBase/VariableSize 按逗号分字段，引号不转义逗号。
    public static IEnumerable<string[]> Csv(string text)
        => ReadRows(text).Select(row => row.Fields);
    public static IEnumerable<CsvRow> ReadRows(string text)
    {
        int offset = 0, number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++; int startOffset = offset; offset += raw.Length + 1;
            string line = raw.Split(';', 2)[0];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split(',').Select(field => field.Trim()).ToArray();
            yield return new(fields, number, startOffset, raw.Length);
        }
    }
    public static async ValueTask<GameDefinition> LoadAsync(IGameSource source, ProgramIndex index, string? explicitId = null, CancellationToken ct = default)
    {
        string? id = null; string title = "mEmuera"; string version = "0";
        long gameVersion = 0, minimumVersion = -1;
        bool versionDefined = false, versionSpecified = false, minimumSpecified = false;
        var variables = index.Variables.ToArray();
        foreach (var file in index.Manifest.Files.Where(f => f.Category == "CSV"))
        {
            if (index.Options.Profile != "minimal-v1" && !file.Id.Path.StartsWith("CSV/", StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileName(file.Id.Path).ToUpperInvariant();
            if (name is not ("GAMEBASE.CSV" or "VARIABLESIZE.CSV")) continue;
            var text = await source.ReadAsync(file.Id, ct);
            foreach (var record in ReadRows(text.Text))
            {
                var row = record.Fields;
                var span = new SourceSpan(file.Id, record.Offset, record.Length, record.Line);
                if (row.Length < 2) continue;
                if (name == "GAMEBASE.CSV")
                {
                    switch (row[0])
                    {
                        case "コード":
                            if (!long.TryParse(row[1], out long number) || number == 0) throw new ScriptException("BOOT_ID", "游戏代码必须为非零整数", span);
                            if (id is not null && id != "era:" + number) throw new ScriptException("BOOT_ID", "游戏代码冲突", span);
                            id = "era:" + number; break;
                        case "タイトル": title = row[1]; break;
                        case "バージョン":
                            version = row[1]; versionSpecified = true;
                            versionDefined = TryMetadataNumber(version, out gameVersion); break;
                        case "バージョン違い認める":
                            minimumSpecified = true; TryMetadataNumber(row[1], out minimumVersion); break;
                    }
                }
                else if (row[0] is "RESULT" or "RESULTS")
                {
                    if (!int.TryParse(row[1], out int length) || length is <= 0 or > 1000000) throw new ScriptException("BOOT_SIZE", "变量长度非法", span);
                    int position = Array.FindIndex(variables, v => v.Name == row[0]); variables[position] = variables[position] with { Length = length };
                }
            }
        }
        if (explicitId is not null && id is not null && explicitId != id) throw new ScriptException("BOOT_ID", "显式 game-id 与 CSV 身份冲突");
        id = explicitId ?? id ?? throw new ScriptException("BOOT_ID", "缺少游戏代码，请提供 --game-id");
        object ExtensionProfile(ExtensionSaveNames names) => names.XmlDocuments.IsEmpty ? new { names.Maps, names.Tables } : names;
        string specifications = index.Options.Profile == "minimal-v1"
            ? string.Join("|", variables.Select(v => $"{v.Name}:{v.Kind}:{v.Length}:{v.Saved}"))
            : System.Text.Json.JsonSerializer.Serialize(new { index.Options.AllowCallEvent, index.Options.DefaultComAble, index.Options.DefaultStain,
                GameExtensions = ExtensionProfile(index.Options.GameExtensions), GlobalExtensions = ExtensionProfile(index.Options.GlobalExtensions),
                // 非持久的 LOCALF 和整数边界常量不参与存档配置指纹。
                Variables = variables.Where(v => v.Saved || v.GlobalSaved || v.Name is not ("LOCALF" or "__INT_MAX__" or "__INT_MIN__"))
                    .Select(v => new { v.Name, v.Kind, v.Shape, v.Scope, v.Saved, v.GlobalSaved, v.ReadOnly, v.CharacterScalar }) }, IdentityJson.Options);
        string profile = index.Options.Profile + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.Options.Encoding + version + specifications + (index.Options.LegacyTimes ? ":times-legacy" : ""))));
        var declaredVersion = variables.FirstOrDefault(v => v.Name == "GAMEBASE_VERSION");
        if (!versionSpecified && declaredVersion is { Initial.IsEmpty: false })
        { gameVersion = declaredVersion.Initial[0].AsInt(); versionDefined = true; }
        var declaredMinimum = variables.FirstOrDefault(v => v.Name == "GAMEBASE_ALLOWVERSION");
        if (!minimumSpecified && declaredMinimum is { Initial.IsEmpty: false }) minimumVersion = declaredMinimum.Initial[0].AsInt();
        // 版本迁移只复用相同语言配置、变量规格和注册扩展布局。
        string migration = System.Text.Json.JsonSerializer.Serialize(new
        {
            index.Options.Profile, index.Options.Encoding, index.Options.AllowCallEvent, index.Options.AllowSpCharacters,
            index.Options.AutoStringArguments, index.Options.LegacyTimes, index.Options.SortFiles, index.Options.SearchSubdirectories,
            index.Options.UseRename, index.Options.UseReplace, index.Options.UseErd, index.Options.LanguageCodePage,
            Specifications = specifications
        }, IdentityJson.Options);
        string fingerprint = "save-layout-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migration)));
        return new(id, title, profile, index with { Variables = variables.ToImmutableArray() }, Flow: SystemFlow.LoadContinuationPolicy.Create(index))
        {
            SaveCompatibility = new(profile, fingerprint, gameVersion, minimumVersion, versionDefined),
            CharacterTemplates = index.Options.CharacterTemplates,
            SystemFlow = index.Options.Profile == "minimal-v1" ? null : new SystemFlow.EraSystemFlow(index)
        };
    }
    private static bool TryMetadataNumber(string text, out long value)
    {
        if (long.TryParse(text, out value)) return true;
        int length = 0;
        while (length < text.Length && char.IsNumber(text[length])) length++;
        return long.TryParse(text.AsSpan(0, length), out value);
    }
}
