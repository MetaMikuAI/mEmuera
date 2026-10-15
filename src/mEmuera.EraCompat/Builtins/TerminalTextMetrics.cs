using System.Globalization;
using System.Text;
using mEmuera.Contracts;
using Wcwidth;

namespace mEmuera.EraCompat.Builtins;

// 脚本坐标：每列 cellWidth 个逻辑单位。TUI 固定为 8，不表示显示器物理像素；
// 像素前端传入 FontSize / 2，使脚本看到的字号和窗口宽度与 emuera.config 一致。
public sealed class TerminalTextMetrics(int cellWidth = 8) : ITextMetrics
{
    public int CellWidth { get; } = cellWidth > 0 ? cellWidth : throw new ArgumentOutOfRangeException(nameof(cellWidth));
    public int Columns(string text)
    {
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int cells = element.EnumerateRunes().Select(r => Math.Max(0, UnicodeCalculator.GetWidth(r))).Sum();
            if (element.Contains('\u200d') || element.Contains('\ufe0f')) cells = 2;
            width = checked(width + Math.Max(1, cells));
        }
        return width;
    }
}
