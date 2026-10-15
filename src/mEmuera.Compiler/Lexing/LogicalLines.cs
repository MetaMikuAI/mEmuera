using mEmuera.Contracts;

namespace mEmuera.Compiler.Lexing;

public sealed record LogicalLine(string Text, SourceSpan Span, int End);
public static class LogicalLines
{
    public static IEnumerable<string> Calls(string text)
    {
        bool quoted = false, escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (escape) { escape = false; continue; }
            if (quoted && c == '\\') { escape = true; continue; }
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted || !(char.IsLetter(c) || c == '_')) continue;
            int start = i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            string name = text[start..i].ToUpperInvariant();
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i < text.Length && text[i] == '(') yield return name;
            i--;
        }
    }
    public static IEnumerable<LogicalLine> Read(SourceText source)
    {
        using var lines = Physical(source).GetEnumerator();
        while (lines.MoveNext())
        {
            var line = lines.Current;
            if (line.Text == "}") throw new ScriptException("CONTINUATION", "多余的续行结束符", line.Span);
            if (line.Text != "{") { yield return line; continue; }
            var text = new System.Text.StringBuilder(); bool closed = false; int end = line.End;
            while (lines.MoveNext())
            {
                var part = lines.Current; end = part.End;
                if (part.Text == "}") { closed = true; break; }
                if (part.Text == "{") throw new ScriptException("CONTINUATION", "续行块不能嵌套", part.Span);
                text.Append(source.Text.AsSpan(part.Span.Start - part.Span.Column + 1, part.End - (part.Span.Start - part.Span.Column + 1)).TrimEnd('\r'));
                text.Append(' ');
            }
            if (!closed) throw new ScriptException("CONTINUATION", "续行块未结束", line.Span);
            yield return new(text.ToString().Trim(), line.Span with { Length = end - line.Span.Start }, end);
        }
    }
    public static IEnumerable<LogicalLine> Physical(SourceText source)
    {
        int start = 0, line = 1;
        while (start < source.Text.Length)
        {
            int end = source.Text.IndexOf('\n', start);
            if (end < 0) end = source.Text.Length;
            int length = end - start;
            while (length > 0 && source.Text[start + length - 1] == '\r') length--;
            int lead = 0;
            while (lead < length && char.IsWhiteSpace(source.Text[start + lead])) lead++;
            yield return new(source.Text.Substring(start + lead, length - lead), new(source.Id, start + lead, length - lead, line, lead + 1), end);
            start = end + 1;
            line++;
        }
    }
    public static string StripComment(string text)
    {
        bool quoted = false, escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (escape) { escape = false; continue; }
            if (quoted && c == '\\') { escape = true; continue; }
            if (c == '"') quoted = !quoted;
            if (!quoted && c == ';') return text[..i].TrimEnd();
        }
        return text;
    }
    private static int HeadLength(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ',') i++;
        return i;
    }
    public static string HeadName(string text) => text[..HeadLength(text)].ToUpperInvariant();
    public static (string Name, string Tail) Head(string text)
    {
        int i = HeadLength(text);
        return (text[..i].ToUpperInvariant(), text[i..].Trim());
    }
    private static bool RawArgument(string name) => name is
        "PRINT" or "PRINTL" or "PRINTW" or "PRINTD" or "PRINTDL" or "PRINTDW" or "PRINTK" or "PRINTKL" or "PRINTKW"
        or "PRINTC" or "PRINTLC" or "PRINTCD" or "PRINTLCD" or "PRINTPLAIN" or "PRINTPLAINFORM" or "PRINTSINGLE" or "PRINTSINGLEFORM"
        or "PRINTFORM" or "PRINTFORML" or "PRINTFORMW" or "PRINTFORMD" or "PRINTFORMDL" or "PRINTFORMDW"
        or "PRINTFORMK" or "PRINTFORMKL" or "PRINTFORMKW" or "PRINTFORMC" or "PRINTFORMLC" or "PRINTFORMCD" or "PRINTFORMLCD"
        or "PUTFORM" or "REUSELASTLINE" or "STRLEN" or "STRLENU" or "STRLENFORM" or "STRLENFORMU" or "THROW"
        or "DRAWLINEFORM" or "CUSTOMDRAWLINE" or "DEBUGPRINT" or "DEBUGPRINTL" or "DEBUGPRINTFORM" or "DEBUGPRINTFORML"
        or "DATA" or "DATAFORM";
    public static (string Name, string Tail) StatementHead(string text)
    {
        var head = Head(text);
        if (!RawArgument(head.Name) && !head.Name.StartsWith("PRINTV", StringComparison.Ordinal)) return head;
        int start = head.Name.Length;
        if (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        return (head.Name, text[start..]);
    }
    public static string StripStatementComment(string text)
    {
        string name = HeadName(text);
        if (RawArgument(name)) return text;
        if (!name.StartsWith("PRINTV", StringComparison.Ordinal)) return StripComment(text);
        bool quoted = false, escaped = false, raw = false;
        for (int i = name.Length; i < text.Length; i++)
        {
            char c = text[i];
            if (raw) { if (c == '\\' && i + 1 < text.Length) i++; else if (c == ',') raw = false; continue; }
            if (escaped) { escaped = false; continue; }
            if (quoted && c == '\\') { escaped = true; continue; }
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c == '\'') raw = true;
            if (c == ';') return text[..i].TrimEnd();
        }
        return text;
    }
    public static List<string> Split(string text, bool printValues = false)
    {
        var result = new List<string>();
        bool quoted = false, escape = false, raw = false;
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (printValues && raw)
            {
                if (c == '\\' && i + 1 < text.Length) i++;
                else if (c == ',') { result.Add(text[start..i].TrimStart()); start = i + 1; raw = false; }
                continue;
            }
            if (printValues && !quoted && c == '\'' && text[start..i].Trim().Length == 0) { raw = true; continue; }
            if (escape) { escape = false; continue; }
            if (!quoted && c == '@' && i + 1 < text.Length && text[i + 1] == '"')
            { i = Syntax.FormattedText.QuotedEnd(text, i + 2, null); continue; }
            if (!quoted && c == '\\' && text.AsSpan(i).StartsWith("\\@"))
            { i = Syntax.FormattedText.ConditionalEnd(text, i, null) - 1; continue; }
            if (quoted && c == '\\') { escape = true; continue; }
            if (c == '"') quoted = !quoted;
            if (quoted) continue;
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ',' && depth == 0) { result.Add(text[start..i].Trim()); start = i + 1; }
        }
        result.Add(raw ? text[start..].TrimStart() : text[start..].Trim());
        return result;
    }
}
