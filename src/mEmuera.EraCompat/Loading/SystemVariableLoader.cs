using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Loading;

public static class SystemVariableLoader
{
    // 系统变量的默认规格及 CSV 配置。
    public static async ValueTask<CompileOptions> PrepareAsync(IGameSource source, CompileOptions options, CancellationToken ct = default)
    {
        if (options.Profile == "minimal-v1") return options;
        options = ConfigQueryMetadata.Initialize(options);
        var variables = new Dictionary<string, VariableDescriptor>(StringComparer.OrdinalIgnoreCase);
        void Add(string names, int length, ValueKind kind = ValueKind.Integer, VariableScope scope = VariableScope.System,
            bool saved = true, bool readOnly = false, bool global = false)
        {
            foreach (string name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                variables.Add(name, new(name, name, kind, length, saved, Scope: scope, ReadOnly: readOnly, GlobalSaved: global));
        }
        Add("DAY MONEY ITEM TFLAG UP PALAMLV EXPLV EJAC DOWN RESULT COUNT TARGET ASSI MASTER NOITEM LOSEBASE SELECTCOM ASSIPLAY PREVCOM TIME ITEMSALES PLAYER NEXTCOM PBAND BOUGHT", 1000);
        variables["EXPLV"] = variables["EXPLV"] with { Initial = [.. new long[] { 0, 1, 4, 20, 50, 200 }.Select(v => new EraValue(v))] };
        variables["PALAMLV"] = variables["PALAMLV"] with { Initial = [.. new long[] { 0, 100, 500, 3000, 10000, 30000, 60000, 100000, 150000, 250000 }.Select(v => new EraValue(v))] };
        Add("FLAG", 10000);
        Add(string.Join(' ', Enumerable.Range('A', 26).Select(c => ((char)c).ToString())), 1000);
        Add("RANDDATA", 625);
        Add("SAVESTR TSTR", 100, ValueKind.String);
        Add("STR", 20000, ValueKind.String, saved: false);
        Add("RESULTS", 100, ValueKind.String, saved: false);
        Add("GLOBAL", 1000, saved: false, global: true);
        Add("GLOBALS", 100, ValueKind.String, saved: false, global: true);
        Add("SAVEDATA_TEXT", 1, ValueKind.String, saved: false);
        Add("WINDOW_TITLE", 1, ValueKind.String, saved: false);
        Add("RESULTF", 1, ValueKind.Float, saved: false);
        Add("GAMEBASE_CODE GAMEBASE_VERSION GAMEBASE_ALLOWVERSION GAMEBASE_DEFAULTCHARA GAMEBASE_NOITEM", 1, saved: false, readOnly: true);
        Add("GAMEBASE_TITLE GAMEBASE_AUTHOR GAMEBASE_YEAR GAMEBASE_INFO", 1, ValueKind.String, saved: false, readOnly: true);
        Add("LOCAL ARG", 1000, scope: VariableScope.CallLocal, saved: false);
        Add("LOCALS ARGS", 100, ValueKind.String, VariableScope.CallLocal, saved: false);
        Add("LOCALF", 10, ValueKind.Float, VariableScope.CallLocal, saved: false);
        Add("__INT_MAX__ __INT_MIN__", 1, saved: false, readOnly: true);
        variables["__INT_MAX__"] = variables["__INT_MAX__"] with { Initial = [new(long.MaxValue)] };
        variables["__INT_MIN__"] = variables["__INT_MIN__"] with { Initial = [new(long.MinValue)] };
        Add("ISASSI NO", 1, scope: VariableScope.Character);
        Add("BASE MAXBASE ABL EXP MARK PALAM SOURCE EX RELATION EQUIP TEQUIP STAIN NOWEX DOWNBASE CUP CDOWN TCVAR", 100, scope: VariableScope.Character);
        Add("TALENT CFLAG", 1000, scope: VariableScope.Character);
        Add("JUEL GOTJUEL", 200, scope: VariableScope.Character);
        Add("NAME CALLNAME NICKNAME MASTERNAME", 1, ValueKind.String, VariableScope.Character);
        Add("CSTR", 100, ValueKind.String, VariableScope.Character);
        foreach (string name in "ISASSI NO NAME CALLNAME NICKNAME MASTERNAME".Split(' ')) variables[name] = variables[name] with { CharacterScalar = true };
        foreach (var (name, size) in new (string, int)[] { ("ABL",100), ("EXP",100), ("TALENT",1000), ("MARK",100), ("TRAIN",1000), ("PALAM",200), ("ITEM",1000), ("BASE",100), ("SOURCE",1000), ("EX",100), ("EQUIP",100), ("TEQUIP",100), ("FLAG",10000), ("TFLAG",1000), ("CFLAG",1000), ("TCVAR",100), ("CSTR",100), ("STAIN",1000), ("STR",20000), ("TSTR",100), ("SAVESTR",100), ("GLOBAL",1000), ("GLOBALS",100), ("DAY",100), ("TIME",100), ("MONEY",100) })
            Add(name + "NAME", size, ValueKind.String, saved: false, readOnly: true);
        Add("ITEMPRICE", 1000, saved: false, readOnly: true);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifest = await source.EnumerateAsync(ct);
        var sourcePaths = manifest.Files.ToDictionary(f => f.Id.Path, StringComparer.OrdinalIgnoreCase);
        var aliasDiagnostics = new List<Diagnostic>();
        async ValueTask AddAliases(SourceFile file, ImmutableDictionary<string, int>.Builder names)
        {
            if (!sourcePaths.TryGetValue(Path.ChangeExtension(file.Id.Path, ".als"), out var alias)) return;
            var text = await source.ReadAsync(alias.Id, ct);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in GameBootstrapLoader.ReadRows(text.Text))
            {
                var span = new SourceSpan(alias.Id, row.Offset, row.Length, row.Line);
                if (row.Fields.Length < 2 || !int.TryParse(row.Fields[0], out int index))
                { aliasDiagnostics.Add(new("ALIAS_ROW", "别名行格式无效，已跳过", span, Severity.Warning)); continue; }
                string name = row.Fields[1].Trim();
                if (name.Length == 0) continue;
                if (!seen.Add(name)) aliasDiagnostics.Add(new("ALIAS_DUPLICATE", "重复别名，保留首项", span, Severity.Warning));
                names.TryAdd(name, index);
            }
        }
        var configurationSources = new List<SourceId>();
        if (sourcePaths.TryGetValue("emuera.config", out var config))
        {
            configurationSources.Add(config.Id);
            options = EngineConfig.Apply(await source.ReadAsync(config.Id, ct), options);
        }
        if (sourcePaths.TryGetValue("CSV/VarExtGameData.csv", out var extensionConfig))
        {
            var text = await source.ReadAsync(extensionConfig.Id, ct);
            foreach (var row in GameBootstrapLoader.ReadRows(text.Text))
            {
                if (row.Fields.Length < 2) continue;
                string key = row.Fields[0].ToUpperInvariant();
                bool global = key.StartsWith("GLOBAL_", StringComparison.Ordinal);
                var names = global ? options.GlobalExtensions : options.GameExtensions;
                var values = row.Fields.Skip(1).Select(s => s.Trim()).Where(s => s.Length > 0);
                if (key is "SAVE_DTS" or "GLOBAL_DTS") names = names with { Tables = names.Tables.Union(values) };
                else if (key is "SAVE_MAPS" or "GLOBAL_MAPS") names = names with { Maps = names.Maps.Union(values) };
                else if (key is "SAVE_XMLS" or "GLOBAL_XMLS") names = names with { XmlDocuments = names.XmlDocuments.Union(values) };
                else { aliasDiagnostics.Add(new("EXTENSION_CONFIG_UNSUPPORTED", "尚未支持扩展保存类别: " + key, new(extensionConfig.Id, row.Offset, row.Length, row.Line))); continue; }
                options = global ? options with { GlobalExtensions = names } : options with { GameExtensions = names };
            }
        }
        foreach (var file in manifest.Files.Where(f => options.UseReplace && f.Id.Path.Equals("CSV/_Replace.csv", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await source.ReadAsync(file.Id, ct);
            foreach (var row in GameBootstrapLoader.ReadRows(text.Text))
            {
                if (row.Fields.Length < 2) continue;
                options = ConfigQueryMetadata.Apply(options, row.Fields[0], row.Fields[1]);
                if (row.Fields[0] is "DRAWLINE文字" or "DRAWLINE character")
                    options = options with { DrawLinePattern = row.Fields[1].Length == 0 ? "-" : row.Fields[1] };
                if (row.Fields[0] is "時間切れ表示" or "Time up message") options = options with { TimeupLabel = row.Fields[1] };
                if (row.Fields[0] is "お金の単位" or "Currency symbol") options = options with { MoneyLabel = row.Fields[1] };
                if (row.Fields[0] is "単位の位置" or "Currency symbol position") options = options with { MoneyFirst = row.Fields[1].ToUpperInvariant() switch
                    { "前" or "YES" or "TRUE" => true, "後" or "NO" or "FALSE" => false, _ => throw new ScriptException("CONFIG_MONEY", "货币单位位置无效") } };
                if (row.Fields[0] is "汚れの初期値" or "Default Stain")
                    options = options with { DefaultStain = row.Fields[1].Split('/').Select(s => long.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToImmutableArray() };
                if (row.Fields[0] is "COM_ABLE初期値" or "Default COM_ABLE")
                    options = options with { DefaultComAble = long.Parse(row.Fields[1], System.Globalization.CultureInfo.InvariantCulture) };
                string? level = row.Fields[0] switch { "EXPLVの初期値" or "Default EXPLV" => "EXPLV", "PALAMLVの初期値" or "Default PALAMLV" => "PALAMLV", _ => null };
                if (level is not null)
                    variables[level] = variables[level] with { Initial = [.. row.Fields[1].Split('/').Select(s => new EraValue(long.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)))] };
            }
        }
        foreach (var file in manifest.Files.Where(f => f.Id.Path.Equals("CSV/GameBase.csv", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await source.ReadAsync(file.Id, ct);
            foreach (var row in GameBootstrapLoader.ReadRows(text.Text))
            {
                if (row.Fields.Length < 2) continue;
                string? name = row.Fields[0] switch { "コード" => "GAMEBASE_CODE", "バージョン" => "GAMEBASE_VERSION", "バージョン違い認める" => "GAMEBASE_ALLOWVERSION", "最初からいるキャラ" => "GAMEBASE_DEFAULTCHARA", "アイテムなし" => "GAMEBASE_NOITEM", "ウィンドウタイトル" => "WINDOW_TITLE", "タイトル" => "GAMEBASE_TITLE", "作者" => "GAMEBASE_AUTHOR", "製作年" => "GAMEBASE_YEAR", "追加情報" => "GAMEBASE_INFO", _ => null };
                if (name is null) continue;
                var variable = variables[name];
                EraValue value = variable.Kind == ValueKind.String ? new(row.Fields[1]) : new(ReadMetadataNumber(row.Fields[1]));
                variables[name] = variable with { Initial = [value] };
            }
        }
        foreach (var file in manifest.Files.Where(f => f.Id.Path.Equals("CSV/VariableSize.csv", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await source.ReadAsync(file.Id, ct);
            foreach (var row in GameBootstrapLoader.ReadRows(text.Text))
            {
                if (row.Fields.Length < 2 || !variables.TryGetValue(row.Fields[0], out var variable)) continue;
                if (!int.TryParse(row.Fields[1], out int length) || length is < 0 or > 1_000_000)
                    throw new ScriptException("BOOT_SIZE", "变量长度非法", new(file.Id, row.Offset, row.Length, row.Line));
                variables[variable.Name] = variable with { Length = length };
                changed.Add(variable.Name);
            }
        }
        foreach (string name in "ABL TALENT EXP MARK BASE SOURCE EX EQUIP TEQUIP FLAG TFLAG CFLAG TCVAR CSTR STAIN STR TSTR SAVESTR GLOBAL GLOBALS DAY TIME MONEY".Split(' '))
        {
            var main = variables[name]; var names = variables[name + "NAME"];
            int length = changed.Contains(name) && changed.Contains(names.Name) ? Math.Max(main.Length, names.Length)
                : changed.Contains(name) ? main.Length : changed.Contains(names.Name) ? names.Length : -1;
            if (length >= 0) { variables[name] = main with { Length = length }; variables[names.Name] = names with { Length = length }; }
        }
        variables["ITEMPRICE"] = variables["ITEMPRICE"] with { Length = variables["ITEMNAME"].Length };
        var definedPrices = new HashSet<int>();
        foreach (var file in manifest.Files.Where(f => f.Category == "CSV" && f.Id.Path.StartsWith("CSV/", StringComparison.OrdinalIgnoreCase)))
        {
            string stem = Path.GetFileNameWithoutExtension(file.Id.Path).ToUpperInvariant();
            bool textBody = stem == "STR";
            string target = textBody ? "STR" : stem == "STRNAME" ? "STRNAME" : stem + "NAME";
            if (!variables.TryGetValue(target, out var variable)) continue;
            var values = Enumerable.Repeat(EraValue.Zero(ValueKind.String), variable.Length).ToArray();
            var indices = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            var prices = stem == "ITEM" ? new EraValue[variables["ITEMPRICE"].Length] : null;
            if (prices is not null) Array.Fill(prices, new EraValue(0L));
            var text = await source.ReadAsync(file.Id, ct);
            foreach (var row in ReadNameRows(text.Text))
            {
                if (row.Fields.Length < 2 || !int.TryParse(row.Fields[0], out int at)) continue;
                if (at < 0 || at >= values.Length) throw new ScriptException("CSV_BOUNDS", "名称表下标越界", new(file.Id, row.Offset, row.Length, row.Line));
                values[at] = new(row.Fields[1]);
                if (prices is not null && at < prices.Length && row.Fields.Length > 2 && long.TryParse(row.Fields[2], out long price))
                { prices[at] = new(price); definedPrices.Add(at); }
            }
            if (!textBody)
            {
                for (int at = 0; at < values.Length; at++)
                    if (values[at].AsString().Length != 0) indices.TryAdd(values[at].AsString(), at);
                await AddAliases(file, indices);
            }
            variables[target] = variable with { Initial = values.ToImmutableArray(), NameIndices = textBody ? variable.NameIndices : indices.ToImmutable() };
            string mainName = stem == "STRNAME" ? "STR" : stem;
            if (!textBody && variables.TryGetValue(mainName, out var main)) variables[mainName] = main with { NameIndices = indices.ToImmutable() };
            if (prices is not null) variables["ITEMPRICE"] = variables["ITEMPRICE"] with { Initial = prices.ToImmutableArray() };
        }
        foreach (var file in manifest.Files.Where(f => options.UseErd && f.Category == "ERD" && f.Id.Path.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Id.Path, StringComparer.OrdinalIgnoreCase))
        {
            string stem = Path.GetFileNameWithoutExtension(file.Id.Path).ToUpperInvariant();
            string target = stem switch
            {
                "MAXBASE" or "DOWNBASE" or "LOSEBASE" => "BASENAME",
                "UP" or "DOWN" or "JUEL" or "GOTJUEL" or "CUP" or "CDOWN" => "PALAMNAME",
                "ITEMSALES" or "ITEMPRICE" => "ITEMNAME",
                "NOWEX" => "EXNAME",
                "TRAINNAME" => "TRAINNAME",
                "STR" => "STR",
                "STRNAME" => "STRNAME",
                _ => stem + "NAME"
            };
            if (!variables.TryGetValue(target, out var variable)) continue;
            var values = Enumerable.Repeat(EraValue.Zero(ValueKind.String), variable.Length).ToArray();
            if (!variable.Initial.IsDefaultOrEmpty) variable.Initial.CopyTo(values);
            var names = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
            if (target != "STR")
                for (int at = 0; at < values.Length; at++)
                    if (values[at].AsString().Length != 0) names.TryAdd(values[at].AsString(), at);
            var prices = target == "ITEMNAME" ? Enumerable.Repeat(new EraValue(0L), variables["ITEMPRICE"].Length).ToArray() : null;
            if (prices is not null && !variables["ITEMPRICE"].Initial.IsDefaultOrEmpty) variables["ITEMPRICE"].Initial.CopyTo(prices);
            var text = await source.ReadAsync(file.Id, ct);
            foreach (var row in ReadNameRows(text.Text))
            {
                if (row.Fields.Length < 2 || !int.TryParse(row.Fields[0], out int at) || at < 0 || at >= values.Length) continue;
                string name = row.Fields[1];
                if (values[at].AsString().Length == 0 && name.Length != 0 && target != "STR" && names.TryGetValue(name, out int duplicate) && duplicate != at) continue;
                if (values[at].AsString().Length == 0 && (name.Length == 0 || target == "STR" || !names.TryGetValue(name, out int other) || other == at))
                {
                    values[at] = new(name);
                    if (target != "STR" && name.Length != 0) names.TryAdd(name, at);
                }
                if (prices is not null && at < prices.Length && !definedPrices.Contains(at) && row.Fields.Length > 2 && long.TryParse(row.Fields[2], out long price))
                { prices[at] = new(price); definedPrices.Add(at); }
            }
            foreach (var pair in variable.NameIndices) names.TryAdd(pair.Key, pair.Value);
            variables[target] = variable with { Initial = values.ToImmutableArray(), NameIndices = names.ToImmutable() };
            string mainName = target == "STRNAME" ? "STR" : target.EndsWith("NAME", StringComparison.Ordinal) ? target[..^4] : target;
            if (target != "STR" && variables.TryGetValue(mainName, out var main)) variables[mainName] = main with { NameIndices = names.ToImmutable() };
            if (prices is not null) variables["ITEMPRICE"] = variables["ITEMPRICE"] with { Initial = prices.ToImmutableArray() };
        }
        foreach (var (name, table) in new[] { ("MAXBASE", "BASE"), ("DOWNBASE", "BASE"), ("LOSEBASE", "BASE"), ("JUEL", "PALAM"), ("GOTJUEL", "PALAM"), ("UP", "PALAM"), ("DOWN", "PALAM"), ("CUP", "PALAM"), ("CDOWN", "PALAM"), ("ITEMPRICE", "ITEM"), ("ITEMSALES", "ITEM"), ("NOWEX", "EX") })
            variables[name] = variables[name] with { NameIndices = variables[table].NameIndices };
        var diagnostics = aliasDiagnostics;
        var nameTables = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var userNameTables = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files.Where(f => options.UseErd && (f.Category == "ERD" && f.Id.Path.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase)
            || f.Category == "CSV" && f.Id.Path.StartsWith("CSV/", StringComparison.OrdinalIgnoreCase) && f.Id.Path.Count(c => c == '/') == 1)))
        {
            string key = Path.GetFileNameWithoutExtension(file.Id.Path).ToUpperInvariant();
            if (file.Category == "CSV")
            {
                if (key == "STR") continue;
                if (key == "STRNAME") key = "STR";
                if (variables.TryGetValue(key + "NAME", out var presetNames))
                {
                    nameTables[key] = presetNames.NameIndices;
                    continue;
                }
            }
            var names = nameTables.GetValueOrDefault(key, ImmutableDictionary<string, int>.Empty).ToBuilder();
            var text = await source.ReadAsync(file.Id, ct);
            var slots = new Dictionary<int, string>();
            foreach (var row in ReadNameRows(text.Text))
                if (row.Fields.Length >= 2 && int.TryParse(row.Fields[0], out int number) && number >= 0) slots[number] = row.Fields[1];
            foreach (var pair in slots.OrderBy(p => p.Key))
                if (pair.Value.Length != 0) names.TryAdd(pair.Value, pair.Key);
            await AddAliases(file, names);
            nameTables[key] = names.ToImmutable();
            if (file.Category == "ERD" || file.Category == "CSV" && !variables.ContainsKey(key))
            {
                var userNames = userNameTables.GetValueOrDefault(key, ImmutableDictionary<string, int>.Empty).ToBuilder();
                foreach (var pair in slots.OrderBy(p => p.Key))
                    if (pair.Value.Length != 0) userNames.TryAdd(pair.Value, pair.Key);
                await AddAliases(file, userNames);
                userNameTables[key] = userNames.ToImmutable();
            }
        }
        var templates = await CharacterTemplateLoader.LoadAsync(source, manifest, variables, diagnostics, ct);
        if (options.ConfigValues.GetValueOrDefault("CompatiCALLNAME").AsInt() != 0)
            templates = templates.ToImmutableDictionary(p => p.Key, p =>
            {
                var values = p.Value.Values;
                if (values.TryGetValue("NAME", out var name) && !name.IsEmpty
                    && (!values.TryGetValue("CALLNAME", out var callname) || callname.IsEmpty || callname[0].AsString().Length == 0))
                    values = values.SetItem("CALLNAME", [name[0]]);
                return p.Value with { Values = values };
            });
        var characterNames = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        foreach (var template in templates.Values.OrderBy(t => t.Number))
            foreach (string name in new[] { "NAME", "CALLNAME", "NICKNAME" })
                if (template.Values.TryGetValue(name, out var names) && !names.IsEmpty && names[0].AsString().Length != 0)
                    characterNames.TryAdd(names[0].AsString(), template.Number);
        var renames = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var file in manifest.Files.Where(f => options.UseRename && f.Id.Path.Equals("CSV/_Rename.csv", StringComparison.OrdinalIgnoreCase)))
        {
            var text = await source.ReadAsync(file.Id, ct);
            foreach (string line in text.Text.Split('\n'))
            {
                if (line.StartsWith(';')) continue;
                var fields = System.Text.RegularExpressions.Regex.Split(line, @"(?<!\\),");
                if (fields.Length == 2) renames[fields[1].Trim()] = fields[0].Trim();
            }
        }
        return ConfigQueryMetadata.Sync(options) with { SystemVariables = variables.Values.ToImmutableArray(), NameTables = nameTables.ToImmutable(), UserNameTables = userNameTables.ToImmutable(), CharacterNameIndices = characterNames.ToImmutable(), CharacterTemplates = templates, Renames = renames.ToImmutable(), PreparationDiagnostics = diagnostics.ToImmutableArray(), PreparedManifest = manifest,
            PreparationSources = manifest.Files.Where(f => (f.Category is "CSV" or "ALS") && f.Id.Path.StartsWith("CSV/", StringComparison.OrdinalIgnoreCase) || (f.Category is "ERD" or "ALS") && f.Id.Path.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase)).Select(f => f.Id).Concat(configurationSources).ToImmutableArray() };
    }

    private static IEnumerable<GameBootstrapLoader.CsvRow> ReadNameRows(string text)
    {
        int offset = 0, number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++; int start = offset; offset += raw.Length + 1;
            string line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return new(line.Split(','), number, start, raw.Length);
        }
    }

    private static long ReadMetadataNumber(string text)
    {
        if (long.TryParse(text, out long value)) return value;
        int length = 0;
        while (length < text.Length && char.IsNumber(text[length])) length++;
        return long.TryParse(text.AsSpan(0, length), out value) ? value : 0;
    }
}
