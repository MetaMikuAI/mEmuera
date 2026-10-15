using System.Globalization;
using mEmuera.Contracts;
using SkiaSharp;

namespace mEmuera.Rendering;

/// <summary>按字体实测宽度折算列数：原版用 GDI 量整段文字，等宽 CJK 字体里 ★、━ 这类东亚宽度"模糊"字符是整格宽，
/// 与 Unicode 宽度表不同。引擎和渲染器要用同一个实例，布局才与绘制一致。线程安全。</summary>
public sealed class FontTextMetrics(FontCatalog fonts, DisplayMetrics display) : ITextMetrics
{
    private readonly Dictionary<string, int> cache = new(StringComparer.Ordinal);

    public int CellWidth => display.CellWidth;

    public int Columns(string text)
    {
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width = checked(width + Cluster(elements.GetTextElement()));
        return width;
    }

    private int Cluster(string element)
    {
        lock (cache)
        {
            if (cache.TryGetValue(element, out int cached)) return cached;
            int columns;
            // 表情序列和变体选择符：字体量不出可靠的宽度，沿用终端的两格。
            if (element.Contains('‍') || element.Contains('️')) columns = 2;
            else
            {
                var face = fonts.ForRune(fonts.Resolve(display.FontName), char.ConvertToUtf32(element, 0));
                using var font = new SKFont(face, display.FontSize);
                columns = Math.Max(1, (int)Math.Round(font.MeasureText(element) / display.CellWidth));
            }
            return cache[element] = columns;
        }
    }
}
