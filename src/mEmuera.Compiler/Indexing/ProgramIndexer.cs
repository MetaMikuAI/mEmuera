using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using mEmuera.Compiler.Lexing;
using mEmuera.Compiler.Syntax;
using mEmuera.Compiler.Preprocessing;
using mEmuera.Contracts;

namespace mEmuera.Compiler.Indexing;

public static class LanguageCatalog
{
    public static readonly ImmutableHashSet<string> Events = "EVENTFIRST EVENTTRAIN EVENTSHOP EVENTBUY EVENTCOM EVENTTURNEND EVENTCOMEND EVENTEND EVENTLOAD BEFORE_THROW BEFORE_ERROR".Split(' ').ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    public static readonly ImmutableHashSet<string> Commands = "IF ELSEIF ELSE ENDIF SIF SELECTCASE CASE CASEELSE ENDSELECT FOR NEXT WHILE WEND DO LOOP REPEAT REND BREAK CONTINUE CALL RETURN JUMP GOTO CALLFORM TRYCALLFORM RETURNF PRINT PRINTL PRINTW PRINTFORM PRINTFORML PRINTFORMW PRINTD PRINTDL PRINTDW PRINTFORMD PRINTFORMDL PRINTFORMDW PRINTBUTTON DEBUGPRINT DEBUGPRINTL DEBUGPRINTFORM DEBUGPRINTFORML PRINTDATA DATALIST ENDLIST ENDDATA DATA DATAFORM STRDATA VARSET INPUT INPUTS WAIT TINPUT CLEARLINE SETCOLOR RESETCOLOR HTML_PRINT BEGIN SAVEDATA LOADDATA QUIT".Split(' ').ToImmutableHashSet();
    public static readonly ImmutableHashSet<string> KnownUnsupported = "CALLSHARP RANDOMIZE ENDSTRDATA".Split(' ').ToImmutableHashSet();
    static LanguageCatalog() => Commands = Commands.Union(["CALLF", "CALLFORMF", "RESTART", "TRYCCALLFORM", "JUMPFORM", "TRYJUMPFORM", "TRYCJUMPFORM", "CATCH", "ENDCATCH",
        "TRYCALL", "TRYCCALL", "TRYJUMP", "TRYCJUMP",
        "PRINTPLAINFORM", "DRAWLINEFORM", "STRLEN", "STRLENU", "STRLENFORM", "STRLENFORMU",
        "PRINTC", "PRINTLC", "PRINTCD", "PRINTLCD", "PRINTFORMC", "PRINTFORMLC", "PRINTFORMCD", "PRINTFORMLCD",
        "PRINTS", "PRINTSL", "PRINTSW", "PRINTSD", "PRINTSDL", "PRINTSDW", "NOSKIP", "ENDNOSKIP"]);
}

public static class ProgramIndexer
{
    public static VariableDescriptor Declaration(string text, bool local, SourceSpan span)
    {
        var (kind, rest) = LogicalLines.Head(text);
        bool saved = false, reference = false;
        while (true)
        {
            var (attribute, tail) = LogicalLines.Head(rest);
            if (attribute == "SAVEDATA") saved = true;
            else if (attribute == "REF") reference = true;
            else break;
            rest = tail;
        }
        var parts = LogicalLines.Split(rest);
        string name = parts[0].ToUpperInvariant();
        if (name.Length == 0 || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
            throw new ScriptException("DECLARATION", "不支持的变量声明", span);
        int length = 1;
        if (parts.Count > 2 || parts.Count == 2 && (!int.TryParse(parts[1], out length) || length <= 0 || length > 1000000))
            throw new ScriptException("DECLARATION_SIZE", "仅支持正数长度的一维数组", span);
        return new(name, name, kind == "#DIMS" ? ValueKind.String : ValueKind.Integer, length, saved, local, reference);
    }
    /// <param name="progress">每个脚本开始前、以及全部读完后回调：已完成数、总数。</param>
    public static async ValueTask<IndexResult> BuildAsync(IGameSource source, CompileOptions options, CancellationToken ct, Action<Requirement>? requirementSink = null, IBuiltinRegistry? builtins = null, Action<int, int>? progress = null)
    {
        var manifest = options.PreparedManifest ?? await source.EnumerateAsync(ct);
        var variables = new List<VariableDescriptor> { new("RESULT", "RESULT", ValueKind.Integer, 100), new("RESULTS", "RESULTS", ValueKind.String, 100) };
        if (options.Profile != "minimal-v1") variables.Add(new("COUNT", "COUNT", ValueKind.Integer));
        foreach (var variable in options.SystemVariables)
        {
            variables.RemoveAll(v => v.Name == variable.Name);
            variables.Add(variable);
        }
        var functions = new List<FunctionDescriptor>();
        var requirements = new Dictionary<(string, string), Requirement>();
        var declarationSources = new HashSet<SourceId>();
        void Requirement(string name, string category, SourceSpan span, bool supported)
        {
            requirementSink?.Invoke(new(name, category, span, supported));
            var key = (name, category);
            if (requirements.TryGetValue(key, out var existing)) requirements[key] = existing with { Count = existing.Count + 1 };
            else requirements.Add(key, new(name, category, span, supported));
        }
        var diagnostics = options.PreparationDiagnostics.ToList();
        VariableDescriptor WithNames(VariableDescriptor variable)
        {
            var dimensions = ImmutableDictionary.CreateBuilder<int, ImmutableDictionary<string, int>>();
            for (int i = 0; i < variable.Shape.Length; i++)
            {
                string key = variable.Shape.Length == 1 ? variable.Name : variable.Name + "@" + (i + 1);
                if (options.NameTables.TryGetValue(key, out var names)) dimensions[i] = names.Where(p => p.Value < variable.Shape[i]).ToImmutableDictionary(StringComparer.Ordinal);
            }
            return variable with { DimensionNames = dimensions.ToImmutable(), NameIndices = dimensions.GetValueOrDefault(0) ?? variable.NameIndices };
        }
        foreach (var conflict in manifest.Files.GroupBy(f => f.Id.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            diagnostics.Add(new("SOURCE_CASE_COLLISION", "文件路径大小写冲突", new(conflict.First().Id, 0, 0, 1)));
        int structured = 0, unknown = 0, dynamic = 0;
        var scripts = ScriptLoadOrder.Select(manifest, options);
        var macros = new MacroEnvironment();
        var pending = new List<(string Text, SourceSpan Span)>();
        void ResolveGlobals()
        {
            while (pending.Count > 0)
            {
                int count = pending.Count;
                foreach (var declaration in pending.ToArray())
                {
                    try
                    {
                        var variable = WithNames(DeclarationIndexer.Parse(macros.Expand(declaration.Text, declaration.Span), false, declaration.Span, variables, builtins: builtins, options: options, macros: macros));
                        if (variables.Any(v => v.Name == variable.Name)) throw new ScriptException("DUPLICATE_VARIABLE", "变量重复: " + variable.Name, declaration.Span);
                        variables.Add(variable); pending.Remove(declaration);
                    }
                    catch (ScriptException e) when (e.Diagnostic.Code == "CONSTANT_UNDEFINED") { }
                    catch (ScriptException e) { diagnostics.Add(e.Diagnostic); pending.Remove(declaration); }
                }
                if (pending.Count == count)
                {
                    foreach (var declaration in pending) diagnostics.Add(new("CONSTANT_DEPENDENCY", "声明常量依赖缺失或循环", declaration.Span));
                    pending.Clear();
                }
            }
        }
        bool headersResolved = false;
        int done = 0;
        foreach (var file in scripts)
        {
            progress?.Invoke(done++, scripts.Length);
            if (file.Category == "ERB" && !headersResolved) { ResolveGlobals(); headersResolved = true; }
            ct.ThrowIfCancellationRequested();
            int before = diagnostics.Count;
            SourceText text;
            try { text = await source.ReadAsync(file.Id, ct); }
            catch (Exception e) when (e is IOException or DecoderFallbackException or ScriptException)
            { diagnostics.Add(new("SOURCE", e.Message, new(file.Id, 0, 0, 1))); continue; }
            FunctionDescriptor? current = null;
            var attributes = new List<string>();
            var locals = new List<VariableDescriptor>();
            var labels = new List<LabelDescriptor>();
            var parameterDefaults = new List<string?>();
            void Finish(int end)
            {
                if (current is null) return;
                foreach (string implicitName in new[] { "LOCAL", "LOCALS", "LOCALF", "ARG", "ARGS", "ARGF" })
                    if (!locals.Any(v => v.Name == implicitName))
                        locals.Add(new(implicitName, implicitName, implicitName.EndsWith('S') ? ValueKind.String : implicitName is "LOCALF" or "ARGF" ? ValueKind.Float : ValueKind.Integer,
                            options.SystemVariables.FirstOrDefault(v => v.Name == implicitName)?.Length ?? 1000, Local: true, Scope: implicitName.StartsWith("LOCAL", StringComparison.Ordinal) && options.Profile != "minimal-v1" ? VariableScope.FunctionStatic : VariableScope.CallLocal));
                var defaultScope = locals.Concat(variables).ToArray();
                var parameters = current.Parameters.Select((p, position) =>
                {
                    if (position < parameterDefaults.Count && parameterDefaults[position] is { } expression)
                    {
                        try { p = p with { Default = DeclarationIndexer.Constant(expression, defaultScope, current.Span, builtins, options, macros) }; }
                        catch (ScriptException e) { diagnostics.Add(new("PARAMETER_DEFAULT", "参数默认值无效: " + e.Diagnostic.Message, current.Span)); }
                    }
                    var local = locals.Find(v => v.Name == p.Name);
                    var global = variables.Find(v => v.Name == p.Name);
                    var target = local ?? global;
                    bool globalTarget = local is null && global is not null && global.Scope is not (VariableScope.CallLocal or VariableScope.Character);
                    if (target?.ReadOnly == true && (local is not null || globalTarget)) diagnostics.Add(new("PARAMETER_LAYOUT", "形参目标不可写", current.Span));
                    if (target is null) return p;
                    bool reference = local?.Reference == true;
                    return p with { Kind = target.Kind, Reference = reference, GlobalTarget = globalTarget,
                        Default = p.Default ?? (!reference && local is not null ? EraValue.Zero(target.Kind) : null) };
                }).ToImmutableArray();
                functions.Add(current with { BodyEnd = end, Attributes = attributes.ToImmutableArray(), Locals = locals.ToImmutableArray(), Parameters = parameters, Labels = labels.ToImmutableArray(), IsEvent = LanguageCatalog.Events.Contains(current.Name) });
            }
            string? block = null;
            try
            {
            foreach (var expanded in Preprocessor.Read(text, macros, options, file.Category == "ERH", ct))
            {
                var line = expanded.Line;
                string value = LogicalLines.StripComment(line.Text);
                if (value.Length == 0) continue;
                var (name, rest) = LogicalLines.Head(value);
                if (block is not null)
                {
                    if (name == block) block = null;
                    else if (name is not ("DATA" or "DATAFORM" or "DATALIST" or "ENDLIST"))
                        diagnostics.Add(new("DATA_BLOCK", "数据块内存在非法语句", line.Span));
                    continue;
                }
                if (name.StartsWith("PRINTDATA", StringComparison.Ordinal) || name == "STRDATA")
                {
                    block = "ENDDATA";
                    Requirement(name, "block", line.Span, true);
                    continue;
                }
                if (value[0] == '@')
                {
                    Finish(line.Span.Start);
                    parameterDefaults.Clear();
                    var (functionName, args) = LogicalLines.Head(value[1..]);
                    args = args.TrimStart(',').Trim();
                    if (args.StartsWith('(') && args.EndsWith(')')) args = args[1..^1];
                    var parameters = new List<Parameter>();
                    if (args.Length > 0)
                    {
                        foreach (var argument in LogicalLines.Split(args))
                        {
                            string[] pair = argument.Split('=', 2);
                            string parameterName = pair[0].Trim().ToUpperInvariant();
                            parameterDefaults.Add(pair.Length == 2 ? pair[1].Trim() : null);
                            int targetIndex = 0;
                            if (parameterName.Contains(':'))
                            {
                                var target = parameterName.Split(':');
                                if (target.Length != 2 || !int.TryParse(target[1], out targetIndex) || targetIndex < 0 || targetIndex >= 1000)
                                    diagnostics.Add(new("PARAMETER_LAYOUT", "参数目标下标无效", line.Span));
                                parameterName = target[0];
                            }
                            parameters.Add(new(parameterName, parameterName == "ARGS" ? ValueKind.String : parameterName == "ARGF" ? ValueKind.Float : ValueKind.Integer, false, null, targetIndex)
                            { ExplicitTarget = pair[0].Contains(':') });
                        }
                    }
                    current = new(file.Id.Path + "@" + line.Span.Start, functionName, line.Span, line.End + 1, text.Text.Length, parameters.ToImmutableArray(), [], []);
                    attributes.Clear(); locals.Clear(); labels.Clear();
                    continue;
                }
                if (name is "#DIM" or "#DIMS" or "#DIMF")
                {
                    if (current is null)
                    { pending.Add((value, line.Span)); declarationSources.Add(file.Id); continue; }
                    try
                    {
                        var variable = WithNames(DeclarationIndexer.Parse(value, true, line.Span, locals.Concat(variables).ToArray(), options.Profile == "minimal-v1", builtins, options, macros));
                        var destination = current is null ? variables : locals;
                        if (destination.Any(v => v.Name == variable.Name)) throw new ScriptException("DUPLICATE_VARIABLE", "变量重复: " + variable.Name, line.Span);
                        destination.Add(variable);
                        if (current is null) declarationSources.Add(file.Id);
                    }
                    catch (ScriptException e) { diagnostics.Add(e.Diagnostic); }
                    continue;
                }
                if (name is "#LOCALSIZE" or "#LOCALSSIZE" or "#LOCALFSIZE")
                {
                    try
                    {
                        string localName = name == "#LOCALSIZE" ? "LOCAL" : name == "#LOCALSSIZE" ? "LOCALS" : "LOCALF";
                        int size = checked((int)DeclarationIndexer.Constant(rest, variables, line.Span, builtins, options, macros).AsInt());
                        if (current is null || size <= 0 || size > 16_000_000) throw new ScriptException("DECLARATION_SIZE", "局部数组尺寸无效", line.Span);
                        int at = locals.FindIndex(v => v.Name == localName);
                        var variable = new VariableDescriptor(localName, localName, name == "#LOCALSIZE" ? ValueKind.Integer : name == "#LOCALSSIZE" ? ValueKind.String : ValueKind.Float, size, Local: true,
                            Scope: options.Profile == "minimal-v1" ? VariableScope.CallLocal : VariableScope.FunctionStatic);
                        if (at < 0) locals.Add(variable); else locals[at] = variable;
                    }
                    catch (ScriptException e) { diagnostics.Add(e.Diagnostic); }
                    continue;
                }
                if (name is "#FUNCTION" or "#FUNCTIONS" or "#PRI" or "#LATER" or "#SINGLE" or "#ONLY") { attributes.Add(name); continue; }
                if (value[0] is '#' or '[' or '{' or '}')
                {
                    unknown++; diagnostics.Add(new("INDEX_UNKNOWN_STRUCTURE", "未支持的预处理或续行结构", line.Span));
                    Requirement(name, "unknown", line.Span, false); continue;
                }
                if (value[0] == '$')
                { labels.Add(new(value[1..].Trim().ToUpperInvariant(), line.Span)); Requirement("LABEL", "label", line.Span, true); continue; }
                bool assignment = value.Contains('=') && !LanguageCatalog.Commands.Contains(name) && !KnownIdentifiers.Commands.Contains(name) && !KnownIdentifiers.Functions.Contains(name);
                bool supported = assignment || LanguageCatalog.Commands.Contains(name) || builtins?.Find(name) is not null;
                Requirement(assignment ? "ASSIGN" : name, "command", line.Span, supported);
                if (name is not ("PRINT" or "PRINTL" or "PRINTW"))
                    foreach (string call in LogicalLines.Calls(rest)) Requirement(call, "expression-function", line.Span, builtins?.Find(call)?.ReturnType is not null);
                if (name is "CALLFORM" or "TRYCALLFORM" or "GETMETH" or "GETMETHS") dynamic++;
            }
            }
            catch (ScriptException e) { unknown++; diagnostics.Add(e.Diagnostic); }
            Finish(text.Text.Length);
            if (block is not null) diagnostics.Add(new("INDEX_UNCLOSED_BLOCK", "数据块未结束", new(file.Id, 0, 0, 1)));
            if (before == diagnostics.Count) structured++;
        }
        progress?.Invoke(scripts.Length, scripts.Length);
        ResolveGlobals();
        foreach (var group in functions.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1 && !group.First().IsEvent)
                diagnostics.Add(new("DUPLICATE_FUNCTION", "普通函数重复: " + group.Key, group.Last().Span));
        using var identityHash = SHA256.Create();
        await using var identityStream = new CryptoStream(Stream.Null, identityHash, CryptoStreamMode.Write);
        await System.Text.Json.JsonSerializer.SerializeAsync(identityStream, new { options, sources = manifest.Files.Select(f => f.Id), loaded = scripts.Select(f => f.Id) }, IdentityJson.Options, cancellationToken: ct);
        await identityStream.FlushFinalBlockAsync(ct);
        string identity = Convert.ToHexString(identityHash.Hash!);
        return new(new(identity, manifest, variables.ToImmutableArray(), functions.ToImmutableArray(), requirements.Values.ToImmutableArray(),
            new(scripts.Length, structured, unknown, dynamic), options, declarationSources.ToImmutableArray())
            { Macros = macros.Snapshot(), LoadedSources = scripts.Select(f => f.Id).ToImmutableArray() }, diagnostics.ToImmutableArray());
    }
}
