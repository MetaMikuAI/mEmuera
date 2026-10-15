using mEmuera.Contracts;

namespace mEmuera.Runtime.Presentation;

internal sealed partial class DocumentBuilder
{
    public static (string First, string Remainder) SplitHtml(string text, int columns, ITextMetrics metrics, IReadOnlyDictionary<string, RasterImage>? images)
    {
        if (columns <= 0) throw new ScriptException("HTML_WIDTH", "HTML 截取宽度必须大于零");
        long remaining = (long)columns * metrics.CellWidth;
        text = HtmlFragment.UnescapeText(text);
        var open = new Stack<(string Tag, bool Style)>(); var close = new Stack<(string Tag, bool Style)>();
        int at = 0, consumeBreak = 0; bool content = false;
        long Width(string html)
        {
            var measured = new DocumentBuilder(); measured.Html(html, true, images, metrics.CellWidth);
            return measured.Snapshot().Lines[0].Runs.Sum(run => (long)(run.Image?.Width ?? run.Panel?.Width ?? metrics.Columns(run.Text) * metrics.CellWidth));
        }
        while (at < text.Length)
        {
            int tag = text.IndexOf('<', at);
            if (tag != at)
            {
                string prefix = string.Concat(open.Where(v => v.Style).Select(v => v.Tag));
                string suffix = string.Concat(close.Reverse().Where(v => v.Style).Select(v => v.Tag));
                int end = tag < 0 ? text.Length : tag, consumed = 0;
                while (at + consumed < end)
                {
                    long width = Width(prefix + text[at + consumed] + suffix);
                    if (width > remaining) break;
                    remaining -= width; consumed++;
                }
                at += consumed; content = true;
                if (tag < 0 || at < tag) break;
            }
            int finish = HtmlFragment.TagEnd(text, at);
            string body = text[(at + 1)..finish];
            if (body.StartsWith('/'))
            {
                if (open.Count == 0) throw new ScriptException("HTML_SYNTAX", "HTML 结束标签没有对应开始标签");
                open.Pop(); close.Pop();
            }
            else
            {
                string name = body.Split(' ')[0];
                if (name == "br") { consumeBreak = finish + 1 - at; break; }
                if (name is "img" or "shape")
                {
                    remaining -= Width(text[at..(finish + 1)]);
                    if (remaining < 0 && content) break;
                }
                else
                {
                    bool style = name is "b" or "i" or "s";
                    open.Push((text[at..(finish + 1)], style)); close.Push(("</" + name + ">", style));
                }
            }
            at = finish + 1;
        }
        return (text[..at] + string.Concat(close.Select(v => v.Tag)), string.Concat(open.Reverse().Select(v => v.Tag)) + text[(at + consumeBreak)..]);
    }
}
