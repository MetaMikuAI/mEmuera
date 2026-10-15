using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.Saves.Legacy;

public sealed record LegacyConversion(SaveSnapshot Snapshot, ImmutableArray<string> DefaultedFields, ImmutableArray<string> UnmappedZeroFields);

public static class Era1808Converter
{
    private static ScriptException Invalid(string message) => new("LEGACY_COMPATIBILITY", message);
    public static LegacyConversion Convert(LegacySave legacy, GameDefinition game)
    {
        long version = game.Index.VariablesByName.GetValueOrDefault("GAMEBASE_VERSION")?.Initial.FirstOrDefault().AsInt() ?? 0;
        if (game.GameId != "era:" + legacy.GameCode.ToString(CultureInfo.InvariantCulture)) throw Invalid("旧档游戏代码不匹配");
        if (legacy.GameVersion != version) throw Invalid("限定旧档导入要求游戏版本完全一致");
        var missing = ImmutableArray.CreateBuilder<string>();
        var empty = ImmutableArray.CreateBuilder<string>();
        ImmutableArray<SavedVariable> Bind(ImmutableArray<SavedVariable> source, bool character, string prefix)
        {
            var definitions = game.Index.Variables.Where(v => v.Saved && !v.GlobalSaved && (v.Scope == VariableScope.Character) == character).ToArray();
            var old = source.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
            foreach (string name in old.Keys)
                if (!definitions.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (old[name].Values.Any(value => value != EraValue.Zero(old[name].Kind))) throw Invalid("旧档包含不支持且非零的保存字段: " + prefix + name);
                    empty.Add(prefix + name);
                }
            return definitions.Select(v =>
            {
                if (old.TryGetValue(v.Name, out var saved))
                {
                    if (saved.Kind != v.Kind || !saved.Shape.SequenceEqual(v.Shape)) throw Invalid("旧档字段类型或数组形状不匹配: " + prefix + v.Name);
                    return saved with { Name = v.Name };
                }
                missing.Add(prefix + v.Name);
                var values = Enumerable.Repeat(EraValue.Zero(v.Kind), v.Length).ToArray();
                if (!character) v.Initial.CopyTo(values);
                return new SavedVariable(v.Name, v.Kind, v.Length, values.ToImmutableArray()) { Shape = v.Shape };
            }).ToImmutableArray();
        }
        var variables = Bind(legacy.Variables, false, "");
        var characters = legacy.Characters.Select((c, i) =>
        {
            var values = Bind(c, true, $"角色{i}:");
            long no = values.FirstOrDefault(v => v.Name == "NO")?.Values[0].AsInt() ?? 0;
            return new CharacterSnapshot(i + 1, no, values);
        }).ToImmutableArray();
        var names = game.Index.Options.GameExtensions;
        if (legacy.Maps.Keys.Any(k => !names.Maps.Contains(k)) || legacy.Tables.Keys.Any(k => !names.Tables.Contains(k))
            || legacy.XmlDocuments.Keys.Any(k => !names.XmlDocuments.Contains(k))) throw Invalid("旧档扩展未在当前游戏注册保存");
        // 旧随机数发生器状态不能直接解释为新引擎状态；按游戏代码和版本设定稳定种子。
        ulong seed = unchecked((ulong)legacy.GameCode ^ (ulong)legacy.GameVersion ^ 0x9e3779b97f4a7c15UL);
        var snapshot = new SaveSnapshot(2, game.GameId, game.Profile, DateTimeOffset.UtcNow, variables, seed)
        {
            GameVersion = legacy.GameVersion, Description = legacy.Description, Characters = characters,
            Maps = legacy.Maps, XmlDocuments = legacy.XmlDocuments,
            Tables = legacy.Tables.Select(p => Table(p.Key, p.Value)).ToImmutableArray()
        };
        return new(snapshot, missing.ToImmutable(), empty.ToImmutable());
    }
    private static TableSnapshot Table(string name, LegacyTable saved)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_000_000 };
        using var schemaReader = XmlReader.Create(new StringReader(saved.Schema), settings);
        var schema = XDocument.Load(schemaReader);
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        XNamespace ms = "urn:schemas-microsoft-com:xml-msdata";
        if (schema.Descendants().Any(e => e.Attribute(ms + "DataType") is not null || e.Name == xs + "include" || e.Name == xs + "import" || e.Name == xs + "redefine"))
            throw Invalid("旧档表结构包含自定义类型或外部结构");
        try
        {
            using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            using (var reader = schema.CreateReader()) table.ReadXmlSchema(reader);
            Type[] types = [typeof(sbyte), typeof(short), typeof(int), typeof(long), typeof(string)];
            if (table.Columns.Count is 0 or > 1024 || table.Columns.Cast<DataColumn>().Any(c => !types.Contains(c.DataType) || c.Expression.Length != 0))
                throw Invalid($"旧档表 {name} 的字段类型或表达式不受支持（{table.Columns.Count}列：{string.Join(",", table.Columns.Cast<DataColumn>().Select(c => c.ColumnName + ":" + c.DataType.Name))}）");
            using (var reader = XmlReader.Create(new StringReader(saved.Data), settings)) table.ReadXml(reader);
            EraValue? Value(object? value) => value is null or DBNull ? null : value is string text ? new EraValue(text) : new EraValue(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
            return new(name, table.CaseSensitive,
                table.Columns.Cast<DataColumn>().Select(c => new TableColumnSnapshot(c.ColumnName, Array.IndexOf(types, c.DataType) + 1, c.AllowDBNull) { DefaultValue = Value(c.DefaultValue) }).ToImmutableArray(),
                table.Rows.Cast<DataRow>().Select(r => r.ItemArray.Select(Value).ToImmutableArray()).ToImmutableArray());
        }
        catch (Exception e) when (e is DataException or XmlException or ArgumentException)
        { throw Invalid("旧档数据表无效: " + e.Message); }
    }
}
