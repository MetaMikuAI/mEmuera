using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Loading;

internal static class CharacterTemplateLoader
{
    public static async ValueTask<ImmutableDictionary<long, CharacterTemplate>> LoadAsync(IGameSource source, SourceManifest manifest, IReadOnlyDictionary<string, VariableDescriptor> variables, List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var result = ImmutableDictionary.CreateBuilder<long, CharacterTemplate>();
        foreach (var file in manifest.Files.Where(f => f.Category == "CSV" && f.Id.Path.StartsWith("CSV/", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(f.Id.Path).StartsWith("CHARA", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var text = await source.ReadAsync(file.Id, ct); long? number = null;
            var values = new Dictionary<string, EraValue[]>();
            EraValue[] Get(string name)
            {
                if (!values.TryGetValue(name, out var array)) values[name] = array = Enumerable.Repeat(EraValue.Zero(variables[name].Kind), variables[name].Length).ToArray();
                return array;
            }
            foreach (var row in GameBootstrapLoader.ReadRows(text.Text))
            {
                var fields = row.Fields; if (fields.Length < 2) continue;
                var span = new SourceSpan(file.Id, row.Offset, row.Length, row.Line);
                string name = fields[0].ToUpperInvariant() switch
                {
                    "番号" => "NO", "名前" => "NAME", "呼び名" => "CALLNAME", "あだ名" => "NICKNAME", "主人の呼び方" => "MASTERNAME",
                    "刻印" => "MARK", "経験" => "EXP", "能力" => "ABL", "基礎" => "BASE", "素質" => "TALENT", "相性" => "RELATION", "フラグ" => "CFLAG", "装着物" => "EQUIP", "珠" => "JUEL", var n => n
                };
                if (name == "NO")
                {
                    if (number is not null || !long.TryParse(fields[1], out long parsed)) throw new ScriptException("CSV_CHARACTER", "角色编号无效或重复", span);
                    number = parsed; Get("NO")[0] = new(parsed); continue;
                }
                if (number is null) continue;
                if (!variables.TryGetValue(name, out var spec) || spec.Scope != VariableScope.Character) continue;
                if (spec.CharacterScalar && spec.Kind == ValueKind.String) { Get(name)[0] = new(fields[1]); continue; }
                if (fields.Length < 3 && spec.Kind == ValueKind.String) { diagnostics.Add(new("CSV_CHARACTER_VALUE", "角色字符串值缺失", span, Severity.Warning)); continue; }
                int at;
                if (!int.TryParse(fields[1], out at))
                {
                    string table = name == "JUEL" ? "PALAMNAME" : name + "NAME";
                    at = variables.TryGetValue(table, out var names) ? names.NameIndices.GetValueOrDefault(fields[1], -1) : -1;
                }
                if (at < 0 || at >= spec.Length) { diagnostics.Add(new("CSV_CHARACTER_INDEX", "角色模板下标无效，按旧规则跳过该行", span, Severity.Warning)); continue; }
                EraValue value;
                if (spec.Kind == ValueKind.String) value = new(fields[2]);
                else value = new(fields.Length > 2 && long.TryParse(fields[2], out long integer) ? integer : 1L);
                Get(name)[at] = value;
                if (name == "BASE") Get("MAXBASE")[at] = value;
            }
            if (number is null) continue;
            if (!result.TryAdd(number.Value, new(number.Value, values.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray()), file.Id)))
                throw new ScriptException("CSV_CHARACTER", "角色模板编号冲突", new(file.Id, 0, 0, 1));
        }
        return result.ToImmutable();
    }
}
