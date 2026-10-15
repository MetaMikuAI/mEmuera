using mEmuera.Contracts;

namespace mEmuera.Rendering;

/// <summary>emuera.config 里决定像素布局的参数。脚本查到的字号、窗口宽度都由它换算，宿主用同一份创建文本测量和渲染器。
/// WindowWidth/WindowHeight 是画布（原版的 MainPicBox）的像素尺寸，不含滚动条、菜单栏和底部输入行。</summary>
public sealed record DisplayMetrics(int WindowWidth, int WindowHeight, int FontSize, int LineHeight, string FontName,
    uint Foreground, uint Background, uint Focus)
{
    /// <summary>半角字符占一格；全角占两格。等宽字体下与原版逐像素一致。</summary>
    public int CellWidth => Math.Max(1, FontSize / 2);
    public int Columns => Math.Max(1, WindowWidth / CellWidth);
    public int Rows => Math.Max(1, WindowHeight / Math.Max(1, LineHeight));

    /// <param name="menuHeight">原版窗口顶部菜单栏的高度（SHOW MENU:YES 时存在）；移动端没有菜单栏，传 0。</param>
    /// <remarks>原版：画布高 = WINDOW HEIGHT − 菜单栏 − 一行行高（底部输入框）；画布宽 = WINDOW WIDTH，滚动条在画布之外。</remarks>
    public static DisplayMetrics From(CompileOptions options, int menuHeight = 0)
    {
        long Number(string key, long fallback) => options.ConfigValues.TryGetValue(key, out var value) && value.Kind == ValueKind.Integer ? value.Integer : fallback;
        uint Color(string key, long fallback) => 0xff000000u | (uint)(Number(key, fallback) & 0xffffff);
        int lineHeight = (int)Math.Max(1, Number("LineHeight", 19));
        int canvasHeight = (int)Math.Max(lineHeight, Number("WindowY", 480) - lineHeight - Math.Max(0, menuHeight));
        return new((int)Math.Max(1, Number("WindowX", 760)), canvasHeight,
            (int)Math.Max(2, Number("FontSize", 18)), lineHeight, options.DefaultFont,
            Color("ForeColor", options.DefaultForeground), Color("BackColor", options.DefaultBackground), Color("FocusColor", options.FocusColor));
    }
}
