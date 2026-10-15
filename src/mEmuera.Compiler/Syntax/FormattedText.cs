using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Compiler.Syntax;

internal abstract record FormatPart;
internal sealed record FormatText(string Text) : FormatPart;
internal sealed record FormatField(string Expression, bool String) : FormatPart;
internal sealed record FormatChoice(string Condition, IReadOnlyList<FormatPart> Yes, IReadOnlyList<FormatPart> No) : FormatPart;
internal sealed record FormatFieldArguments(string Expression, string? Width, bool Left);

internal sealed class FormattedText(string text, SourceSpan? span, bool ignoreTripleSymbols = false)
{
    private int position;
    private int depth;
    private ScriptException Error(string message) => new("FORMAT", message, span);
    public static IReadOnlyList<FormatPart> Parse(string text, SourceSpan? span, bool ignoreTripleSymbols = false) => new FormattedText(text, span, ignoreTripleSymbols).Read('\0');
    public static FormatFieldArguments FieldArguments(FormatField field, SourceSpan? span, Func<string, string>? expand = null)
    {
        var fields = Lexing.LogicalLines.Split(expand?.Invoke(field.Expression) ?? field.Expression);
        if (fields.Count > 1 && fields[^1].Length == 0) fields.RemoveAt(fields.Count - 1);
        if (fields.Count is < 1 or > 3) throw new ScriptException("FORMAT", "插值参数无效", span);
        string alignment = fields.Count == 3 ? fields[2].ToUpperInvariant() : "RIGHT";
        if (alignment is not ("LEFT" or "RIGHT")) throw new ScriptException("FORMAT", "插值对齐方式无效", span);
        return new(fields[0], fields.Count > 1 ? fields[1] : null, alignment == "LEFT");
    }
    public static List<string> InputArguments(string text, SourceSpan? span, bool ignoreTripleSymbols = false)
    {
        var parser = new FormattedText(text, span, ignoreTripleSymbols); parser.Read(',');
        var result = new List<string> { text[..parser.position] };
        if (parser.position < text.Length) result.AddRange(Lexing.LogicalLines.Split(text[(parser.position + 1)..]));
        return result;
    }
    public static List<string> CallArguments(string text, SourceSpan? span, bool ignoreTripleSymbols = false)
    {
        var parser = new FormattedText(text, span, ignoreTripleSymbols); parser.Read('(');
        var result = new List<string> { text[..parser.position].Trim() };
        string tail = text[parser.position..].Trim();
        if (tail.StartsWith('[')) throw parser.Error("尚未支持调用子名称");
        if (tail.StartsWith('('))
        {
            if (!tail.EndsWith(')')) throw parser.Error("调用参数缺少右括号");
            tail = tail[1..^1].Trim();
        }
        else if (tail.StartsWith(',')) tail = tail[1..].Trim();
        if (tail.Length > 0) result.AddRange(Lexing.LogicalLines.Split(tail));
        return result;
    }
    public static int QuotedEnd(string text, int start, SourceSpan? span)
    {
        var parser = new FormattedText(text, span) { position = start };
        parser.Read('"');
        if (parser.position == text.Length) throw parser.Error("格式字符串未结束");
        return parser.position;
    }
    public static int ConditionalEnd(string text, int start, SourceSpan? span, int nesting = 0)
    {
        if (nesting > 64) throw new ScriptException("FORMAT", "格式字符串嵌套过深", span);
        var parser = new FormattedText(text, span) { position = start + 2, depth = nesting };
        parser.ExpressionUntil('?'); parser.position++;
        parser.Read('#');
        if (parser.position < text.Length && text[parser.position] == '#') { parser.position++; parser.Read('@'); }
        if (!text.AsSpan(parser.position).StartsWith("\\@")) throw parser.Error("条件格式缺少结束符 \\@");
        return parser.position + 2;
    }
    private IReadOnlyList<FormatPart> Read(char end)
    {
        if (++depth > 64) throw Error("格式字符串嵌套过深");
        var parts = new List<FormatPart>(); var buffer = new StringBuilder();
        void Flush() { parts.Add(new FormatText(buffer.ToString())); buffer.Clear(); }
        while (position < text.Length)
        {
            char c = text[position];
            if (end == '(' && c is '(' or ',' or '[') break;
            if (end == ',' && c == ',') break;
            if (end == '"' && c == '"' || end == '#' && c == '#') break;
            if ((end is '#' or '@') && text.AsSpan(position).StartsWith("\\@")) break;
            position++;
            if (c is '{' or '%')
            {
                Flush(); string expression = ExpressionUntil(c == '{' ? '}' : '%'); position++;
                parts.Add(new FormatField(expression, c == '%')); continue;
            }
            if (!ignoreTripleSymbols && c is '*' or '+' or '=' or '/' or '$' && position + 1 < text.Length && text[position] == c && text[position + 1] == c)
            {
                Flush(); position += 2;
                string expression = c switch
                { '*' => "NAME:TARGET", '+' => "CALLNAME:MASTER", '=' => "CALLNAME:PLAYER", '/' => "NAME:ASSI", _ => "CALLNAME:TARGET" };
                parts.Add(new FormatField(expression, true)); continue;
            }
            if (c != '\\') { buffer.Append(c); continue; }
            if (position == text.Length) throw Error("转义符后缺少字符");
            c = text[position++];
            if (c != '@')
            { buffer.Append(c switch { 's' => ' ', 'S' => '　', 't' => '\t', 'n' => '\n', _ => c }); continue; }
            Flush();
            string condition = ExpressionUntil('?'); position++;
            var yes = Trim(Read('#')); IReadOnlyList<FormatPart> no = [];
            if (position < text.Length && text[position] == '#') { position++; no = Trim(Read('@')); }
            if (!text.AsSpan(position).StartsWith("\\@")) throw Error("条件格式缺少结束符 \\@");
            position += 2; parts.Add(new FormatChoice(condition, yes, no));
        }
        Flush(); depth--; return parts;
    }
    private string ExpressionUntil(char end)
    {
        int start = position, parentheses = 0;
        while (position < text.Length)
        {
            char c = text[position];
            if (parentheses == 0 && c == end) return text[start..position];
            if (c == '\\' && text.AsSpan(position).StartsWith("\\@"))
            { position = ConditionalEnd(text, position, span, depth + 1); continue; }
            if (c == '@' && position + 1 < text.Length && text[position + 1] == '"')
            { position += 2; Read('"'); if (position == text.Length) throw Error("格式字符串未结束"); position++; continue; }
            if (c == '"')
            {
                position++; bool closed = false;
                while (position < text.Length)
                {
                    c = text[position++];
                    if (c == '\\' && position < text.Length) { position++; continue; }
                    if (c == '"') { closed = true; break; }
                }
                if (!closed) throw Error("字符串未结束");
                continue;
            }
            if (c == '(') parentheses++;
            if (c == ')') parentheses--;
            position++;
        }
        throw Error("格式表达式缺少结束符 " + end);
    }
    private static IReadOnlyList<FormatPart> Trim(IReadOnlyList<FormatPart> parts)
    {
        var result = parts.ToArray();
        if (result[0] is FormatText first) result[0] = first with { Text = first.Text.TrimStart(' ', '\t') };
        if (result[^1] is FormatText last) result[^1] = last with { Text = last.Text.TrimEnd(' ', '\t') };
        return result;
    }
}
