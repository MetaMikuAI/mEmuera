using System.Collections.Immutable;
using System.Text;
using mEmuera.Compiler.Lexing;
using mEmuera.Contracts;
using LogicalLine = mEmuera.Compiler.Lexing.LogicalLine;

namespace mEmuera.Compiler.Preprocessing;

public sealed class MacroEnvironment
{
    private readonly Dictionary<string, MacroDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
    public MacroEnvironment(IEnumerable<MacroDefinition>? initial = null)
    { foreach (var definition in initial ?? []) definitions.Add(definition.Name, definition); }
    public ImmutableDictionary<string, MacroDefinition> Snapshot() => definitions.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    public bool Contains(string name) => definitions.ContainsKey(name);
    public void Define(string text, SourceSpan span)
    {
        int end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        if (end == 0) throw new ScriptException("MACRO_NAME", "宏名无效", span);
        string name = text[..end]; var parameters = ImmutableArray<string>.Empty;
        if (end < text.Length && text[end] == '(')
        {
            int close = text.IndexOf(')', end);
            if (close < 0) throw new ScriptException("MACRO_PARAMETERS", "宏参数未结束", span);
            parameters = LogicalLines.Split(text[(end + 1)..close]).ToImmutableArray();
            if (parameters.Any(p => p.Length == 0) || parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Length)
                throw new ScriptException("MACRO_PARAMETERS", "宏参数为空或重复", span);
            end = close + 1;
        }
        if (!definitions.TryAdd(name, new(name, parameters, text[end..].Trim(), span)))
            throw new ScriptException("MACRO_DUPLICATE", "宏重复: " + name, span);
    }
    public string Expand(string text, SourceSpan span, List<SourceSpan>? origins = null) => Expand(text, span, [], origins);
    private string Expand(string text, SourceSpan span, HashSet<string> chain, List<SourceSpan>? origins)
    {
        if (chain.Count >= 64) throw new ScriptException("MACRO_LIMIT", "宏展开深度超限", span);
        var output = new StringBuilder();
        bool quoted = false, escaped = false;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (escaped) { output.Append(c); escaped = false; i++; continue; }
            if (quoted && c == '\\') { output.Append(c); escaped = true; i++; continue; }
            if (c == '"') { quoted = !quoted; output.Append(c); i++; continue; }
            if (quoted || !(char.IsLetter(c) || c == '_')) { output.Append(c); i++; continue; }
            int start = i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string name = text[start..i];
            if (!definitions.TryGetValue(name, out var macro)) { output.Append(name); continue; }
            if (!chain.Add(name.ToUpperInvariant())) throw new ScriptException("MACRO_CYCLE", "宏循环: " + name, span);
            origins?.Add(macro.Span);
            string replacement = macro.Replacement;
            if (macro.Parameters.Length > 0)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i == text.Length || text[i] != '(') throw new ScriptException("MACRO_ARGUMENTS", "宏缺少参数: " + name, span);
                int begin = ++i, depth = 1; bool str = false, escape = false;
                for (; i < text.Length && depth > 0; i++)
                {
                    char x = text[i];
                    if (escape) { escape = false; continue; }
                    if (str && x == '\\') { escape = true; continue; }
                    if (x == '"') str = !str;
                    if (!str) { if (x == '(') depth++; if (x == ')') depth--; }
                }
                if (depth != 0) throw new ScriptException("MACRO_ARGUMENTS", "宏参数括号未结束", span);
                var values = LogicalLines.Split(text[begin..(i - 1)]);
                if (values.Count != macro.Parameters.Length) throw new ScriptException("MACRO_ARGUMENTS", "宏参数数量不符", span);
                var arguments = macro.Parameters.Zip(values).ToDictionary(p => p.First, p => p.Second, StringComparer.OrdinalIgnoreCase);
                replacement = Substitute(replacement, arguments);
            }
            output.Append(Expand(replacement, span, chain, origins));
            chain.Remove(name.ToUpperInvariant());
            if (output.Length > 1_000_000) throw new ScriptException("MACRO_LIMIT", "宏展开长度超限", span);
        }
        return output.ToString();
    }
    private static string Substitute(string text, IReadOnlyDictionary<string, string> arguments)
    {
        var result = new StringBuilder(); bool quoted = false, escaped = false;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (escaped) { result.Append(c); escaped = false; i++; continue; }
            if (quoted && c == '\\') { result.Append(c); escaped = true; i++; continue; }
            if (c == '"') { result.Append(c); quoted = !quoted; i++; continue; }
            if (quoted || !(char.IsLetter(c) || c == '_')) { result.Append(c); i++; continue; }
            int start = i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string name = text[start..i]; result.Append(arguments.GetValueOrDefault(name, name));
        }
        return result.ToString();
    }
}

public sealed record ExpandedSourceMap(SourceSpan Original, ImmutableArray<SourceSpan> MacroOrigins);
public sealed record ExpandedLine(LogicalLine Line, ExpandedSourceMap Map);

public static class Preprocessor
{
    public static string CharacterTokens(string text, CompileOptions options, SourceSpan span)
    {
        var output = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '[' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int end = text.IndexOf("]]", i + 2, StringComparison.Ordinal);
                if (end < 0) { output.Append(c); continue; }
                string name = text[(i + 2)..end];
                output.Append(options.Renames.TryGetValue(name, out string? replacement) ? replacement : text[i..(end + 2)]); i = end + 1;
            }
            else output.Append(c);
        }
        return output.ToString();
    }
    private sealed record Branch(bool Parent, bool Taken, bool Active, bool Skip, bool Else = false);
    public static IEnumerable<ExpandedLine> Read(SourceText source, MacroEnvironment macros, CompileOptions options, bool define = false, CancellationToken ct = default)
    {
        var branches = new Stack<Branch>();
        var continuation = new StringBuilder(); LogicalLine? first = null;
        foreach (var physical in LogicalLines.Physical(source))
        {
            ct.ThrowIfCancellationRequested();
            var line = physical with { Text = LogicalLines.StripStatementComment(physical.Text) };
            string value = line.Text;
            if (value.Length == 0) continue;
            bool active = branches.Count == 0 || branches.Peek().Active;
            if (value[0] == '[' && !value.StartsWith("[[", StringComparison.Ordinal))
            {
                int close = value.IndexOf(']');
                if (close < 0) throw new ScriptException("PREPROCESSOR", "预处理指令未结束", line.Span);
                var (key, argument) = LogicalLines.Head(value[1..close]);
                if (key is "IF" or "IF_DEBUG" or "IF_NDEBUG" or "SKIPSTART")
                {
                    bool enabled = key switch { "IF" => macros.Contains(argument), "IF_DEBUG" => options.Debug, "IF_NDEBUG" => !options.Debug, _ => false };
                    branches.Push(new(active, enabled, active && enabled, key == "SKIPSTART"));
                }
                else if (key is "ELSEIF" or "ELSE")
                {
                    if (!branches.TryPop(out var branch) || branch.Skip || branch.Else) throw new ScriptException("PREPROCESSOR", "条件分支不匹配", line.Span);
                    bool enabled = !branch.Taken && (key == "ELSE" || macros.Contains(argument));
                    branches.Push(branch with { Active = branch.Parent && enabled, Taken = branch.Taken || enabled, Else = key == "ELSE" });
                }
                else if (key is "ENDIF" or "SKIPEND")
                {
                    if (!branches.TryPop(out var branch) || branch.Skip != (key == "SKIPEND")) throw new ScriptException("PREPROCESSOR", "条件结束不匹配", line.Span);
                }
                else throw new ScriptException("INDEX_UNKNOWN_STRUCTURE", "未知预处理指令: " + key, line.Span);
                continue;
            }
            if (!active) continue;
            string physicalCommand = LogicalLines.Head(value).Name;
            if (!options.Debug && physicalCommand is "DEBUGPRINT" or "DEBUGPRINTL" or "DEBUGPRINTFORM" or "DEBUGPRINTFORML" or "DEBUGCLEAR")
            {
                yield return new(line, new(line.Span, [])); continue;
            }
            value = CharacterTokens(value, options, line.Span);
            if (value.TrimEnd() == "{")
            {
                if (first is not null) throw new ScriptException("CONTINUATION", "续行块不能嵌套", line.Span);
                first = line; continue;
            }
            if (value.TrimEnd() == "}")
            {
                if (first is null) throw new ScriptException("CONTINUATION", "多余的续行结束符", line.Span);
                line = new(continuation.ToString().Trim(), first.Span with { Length = line.End - first.Span.Start }, line.End);
                first = null; continuation.Clear(); value = line.Text;
            }
            else if (first is not null) { continuation.Append(value).Append(' '); continue; }
            var (command, tail) = LogicalLines.Head(value);
            if (command == "#DEFINE") { if (define) macros.Define(tail, line.Span); continue; }
            var origins = new List<SourceSpan>();
            // 原始输出文本不按标识符展开；格式串表达式由编译器处理。
            if (!command.StartsWith("PRINT", StringComparison.Ordinal) && !command.StartsWith("DEBUGPRINT", StringComparison.Ordinal)
                && command is not ("DATA" or "DATAFORM" or "REUSELASTLINE" or "THROW" or "PUTFORM" or "DRAWLINEFORM" or "STRLENFORM" or "STRLENFORMU"))
                value = macros.Expand(value, line.Span, origins);
            yield return new(line with { Text = value }, new(line.Span, origins.ToImmutableArray()));
        }
        if (first is not null) throw new ScriptException("CONTINUATION", "续行块未结束", first.Span);
        if (branches.Count != 0) throw new ScriptException("PREPROCESSOR", "预处理区域未结束", new(source.Id, source.Text.Length, 0, source.Text.Count(c => c == '\n') + 1));
    }
}
