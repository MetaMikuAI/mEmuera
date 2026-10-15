using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace mEmuera.Contracts;

public static class HtmlFragment
{
    public static string EscapeAttribute(string text) => System.Security.SecurityElement.Escape(text)!
        .Replace("\\", "&#92;", StringComparison.Ordinal).Replace("\n", "&#10;", StringComparison.Ordinal)
        .Replace("\r", "&#13;", StringComparison.Ordinal).Replace("\t", "&#9;", StringComparison.Ordinal);
    public static int TagEnd(string text, int start)
    {
        char quote = '\0';
        for (int end = start + 1; end < text.Length; end++)
        {
            char c = text[end];
            if (quote != '\0')
            {
                if (c == '\\' && end + 1 < text.Length) end++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == '>') return end;
        }
        throw new ScriptException("HTML_SYNTAX", "HTML 标签未结束");
    }
    public static string UnescapeText(string text)
    {
        var output = new StringBuilder();
        for (int at = 0; at < text.Length;)
        {
            if (text[at] == '<')
            {
                int end;
                if (text.AsSpan(at).StartsWith("<!--", StringComparison.Ordinal))
                {
                    end = text.IndexOf("-->", at + 4, StringComparison.Ordinal);
                    if (end < 0) throw new ScriptException("HTML_SYNTAX", "HTML 注释未结束");
                    end += 2;
                }
                else { end = TagEnd(text, at); _ = NormalizeTag(text[(at + 1)..end]); }
                output.Append(text.AsSpan(at, end + 1 - at)); at = end + 1;
            }
            else
            {
                int end = text.IndexOf('<', at); if (end < 0) end = text.Length;
                output.Append(Unescape(text[at..end])); at = end;
            }
        }
        return output.ToString();
    }
    private static string AttributeEscapes(string text)
    {
        var output = new StringBuilder();
        for (int at = 0; at < text.Length; at++)
        {
            char c = text[at];
            if (c != '\\') { output.Append(c); continue; }
            if (++at == text.Length) throw new ScriptException("HTML_SYNTAX", "HTML 属性转义未结束");
            c = text[at];
            switch (c)
            {
                case 's': output.Append(' '); break;
                case 'S': output.Append('　'); break;
                case 't': output.Append('\t'); break;
                case 'n': output.Append('\n'); break;
                case '\\': case '\'': case '"': output.Append(c); break;
                case '\n': break;
                default: output.Append('\\').Append(c); break;
            }
        }
        return output.ToString();
    }
    private static string NormalizeTag(string tag)
    {
        var output = new StringBuilder();
        for (int at = 0; at < tag.Length; at++)
        {
            char c = tag[at];
            if (c is '\'' or '"')
            {
                int close = at + 1;
                while (close < tag.Length && tag[close] != c)
                {
                    if (tag[close] == '\\' && close + 1 < tag.Length) close++;
                    close++;
                }
                if (close == tag.Length) throw new ScriptException("HTML_SYNTAX", "HTML 属性引号未结束");
                string value = Unescape(AttributeEscapes(tag[(at + 1)..close]));
                output.Append(c).Append(EscapeAttribute(value)).Append(c); at = close;
                if (at + 1 < tag.Length && char.IsLetter(tag[at + 1])) output.Append(' ');
            }
            else output.Append(char.ToLowerInvariant(c));
        }
        return output.ToString();
    }
    public static string Unescape(string text)
    {
        var output = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '&') { output.Append(text[i]); continue; }
            int end = text.IndexOf(';', i + 1);
            if (end <= i + 1) throw new ScriptException("HTML_ENTITY", "HTML 实体缺少名称或分号");
            string entity = text[(i + 1)..end].ToLowerInvariant();
            char? known = entity switch { "nbsp" => ' ', "amp" => '&', "lt" => '<', "gt" => '>', "quot" => '"', "apos" => '\'', _ => null };
            if (known is { } value) output.Append(value);
            else
            {
                if (!entity.StartsWith('#')) throw new ScriptException("HTML_ENTITY", "未知 HTML 实体");
                try
                {
                    int code = entity.StartsWith("#x", StringComparison.Ordinal) ? Convert.ToInt32(entity[2..], 16) : Convert.ToInt32(entity[1..], 10);
                    if (code is < 0 or > 65535) throw new FormatException();
                    output.Append((char)code);
                }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                { throw new ScriptException("HTML_ENTITY", "HTML 数字实体无效"); }
            }
            i = end;
        }
        return output.ToString();
    }
    public static XElement Parse(string text)
    {
        var xml = new StringBuilder("<root>");
        var open = new Stack<(string Name, string Tag)>();
        static bool Inline(string name) => name is "font" or "b" or "i" or "u" or "s" or "button" or "nonbutton";
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '<')
            {
                int next = text.IndexOf('<', i); if (next < 0) next = text.Length;
                xml.Append(System.Security.SecurityElement.Escape(Unescape(text[i..next]))); i = next - 1; continue;
            }
            if (text.AsSpan(i).StartsWith("<!--", StringComparison.Ordinal))
            {
                int commentEnd = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (commentEnd < 0) throw new ScriptException("HTML_SYNTAX", "HTML 注释未结束");
                i = commentEnd + 2; continue;
            }
            int end = TagEnd(text, i);
            string tag = text[(i + 1)..end].Trim();
            string name = tag.Split([' ', '\t', '\r', '\n', '/'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
            if (!tag.StartsWith('!') && !tag.StartsWith('?'))
            {
                if (tag.StartsWith('/'))
                {
                    if (name == "p" && open.TryPeek(out var child) && child.Name == "nobr")
                    { open.Pop(); xml.Append("</nobr>"); }
                    if (open.TryPeek(out var current) && current.Name != name && Inline(name)
                        && open.TakeWhile(t => t.Name != name).All(t => Inline(t.Name)) && open.Any(t => t.Name == name))
                    {
                        // 旧版行内字体与按钮状态独立；转成 XML 时拆开交叉区间并保留仍有效的状态。
                        var reopen = new Stack<(string Name, string Tag)>();
                        while (open.Peek().Name != name)
                        {
                            var item = open.Pop(); xml.Append("</").Append(item.Name).Append('>'); reopen.Push(item);
                        }
                        open.Pop(); xml.Append("</").Append(name).Append('>');
                        while (reopen.TryPop(out var item)) { xml.Append('<').Append(item.Tag).Append('>'); open.Push(item); }
                        i = end; continue;
                    }
                    if (!open.TryPeek(out var expected) || expected.Name != name)
                        throw new ScriptException("HTML_SYNTAX", $"HTML 结束标签不匹配: </{name}>，当前标签 <{expected.Name ?? "无"}>，位置 {i}");
                    open.Pop();
                }
                else if (!tag.EndsWith('/') && name is not ("br" or "img" or "shape")) open.Push((name, NormalizeTag(tag)));
            }
            xml.Append('<').Append(NormalizeTag(tag));
            if (!tag.StartsWith('/') && !tag.EndsWith('/') && name is "br" or "img" or "shape") xml.Append('/');
            xml.Append('>'); i = end;
        }
        while (open.TryPop(out var item))
        {
            string name = item.Name;
            if (name is not ("p" or "nobr")) throw new ScriptException("HTML_SYNTAX", "HTML 标签未关闭: " + name);
            xml.Append("</").Append(name).Append('>');
        }
        xml.Append("</root>");
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.ToString()), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            return XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException error) { throw new ScriptException("HTML_SYNTAX", "HTML 片段格式错误: " + error.Message); }
    }
}
