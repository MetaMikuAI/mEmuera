using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using mEmuera.Contracts;
using SkiaSharp;

namespace mEmuera.Cells;

/// <summary>字符格前端的默认配色，与 TUI 相同。</summary>
public sealed record CellTheme
{
    public int Foreground { get; init; } = 0xDDDDDD;
    public int Button { get; init; } = 0x66DDFF;
    /// <summary>文档没有指定背景色时使用。</summary>
    public int Background { get; init; } = 0x101820;
}

/// <summary>一帧画面。Selected 是高亮的按钮（悬停或手指按住），Tooltip 为 null 表示不显示提示。</summary>
public sealed record CellFrame(PresentationDocument Document, LayoutSnapshot Layout, int Rows)
{
    public long? Selected { get; init; }
    public (string Text, int X, int Y)? Tooltip { get; init; }
}

/// <summary>
/// 把 <see cref="LayoutSnapshot"/> 画成像素，效果对齐 TUI 的 Kitty 模式：图片坐标沿用每格 8×16 的脚本单位，
/// 按实际格子大小缩放；层次是格子底色 → 背景图与内嵌图（Kitty z=-1）→ 文字 → 前景图（z=1）→ 提示框。
/// 格高固定为格宽两倍，图片不变形。
/// </summary>
public sealed class CellPainter : IDisposable
{
    private readonly CellTheme theme;
    private readonly SKTypeface primary;
    private readonly bool ownsPrimary;
    private readonly Dictionary<int, SKTypeface> fallbacks = [];
    private readonly Dictionary<string, SKTypeface> families = new(StringComparer.OrdinalIgnoreCase);
    private List<SKTypeface>? scanned;
    // Android 的 Skia 认得 monospace 别名并能按字符回退；桌面 Linux 的 NoDependencies 版本两样都不行，要按名字找。
    private static readonly string[] MonospaceNames = ["monospace", "Droid Sans Mono", "DejaVu Sans Mono", "Noto Sans Mono", "Cascadia Mono", "Consolas", "Menlo"];
    private static readonly string[] FallbackNames = ["Noto Sans CJK SC", "Noto Sans SC", "Source Han Sans SC", "Droid Sans Fallback", "WenQuanYi Micro Hei",
        "Microsoft YaHei", "PingFang SC", "Noto Sans CJK JP", "MS Gothic", "Noto Sans Symbols 2", "Noto Sans Symbols2", "Noto Color Emoji"];
    private readonly Dictionary<(SKTypeface, int), SKFont> fonts = [];
    private readonly SKPaint paint = new() { IsAntialias = true };
    private float ascent, descent;
    // 图片按像素数组缓存：同一张图在不同帧里换了外层记录也能命中。
    private static readonly ConditionalWeakTable<uint[], SKImage> Images = new();

    public CellPainter(SKTypeface? typeface = null, float fontSize = 16, CellTheme? theme = null)
    {
        this.theme = theme ?? new();
        ownsPrimary = typeface is null;
        primary = typeface ?? Monospace();
        FontSize = fontSize;
    }

    public CellTheme Theme => theme;
    public int CellWidth { get; private set; }
    public int CellHeight => CellWidth * 2;
    public float FontSize
    {
        get;
        set
        {
            field = Math.Clamp(value, 4, 200);
            foreach (var font in fonts.Values) font.Dispose();
            fonts.Clear();
            var font0 = Font(primary, 0);
            CellWidth = Math.Max(1, (int)MathF.Round(font0.MeasureText("M")));
            var metrics = font0.Metrics;
            ascent = metrics.Ascent; descent = metrics.Descent;
        }
    }

    /// <summary>给定像素尺寸能容纳的列数与行数。</summary>
    public (int Columns, int Rows) Grid(int width, int height) => (Math.Max(1, width / CellWidth), Math.Max(1, height / CellHeight));

    /// <summary>离屏画一帧，尺寸为列数×行数个格子。</summary>
    public SKBitmap Render(CellFrame frame)
    {
        var bitmap = new SKBitmap(frame.Layout.Width * CellWidth, frame.Rows * CellHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        Paint(canvas, frame);
        return bitmap;
    }

    public void Paint(SKCanvas canvas, CellFrame frame)
    {
        var document = frame.Document; var layout = frame.Layout;
        int columns = layout.Width, rows = frame.Rows, cw = CellWidth, ch = CellHeight;
        var visible = layout.Glyphs.Where(g => g.X >= 0 && g.X + g.Width <= columns && g.Y >= 0 && g.Y < rows).ToArray();
        canvas.Clear(Opaque(document.BackgroundColor ?? theme.Background));
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, columns * cw, rows * ch));
        foreach (var glyph in visible)
            if (glyph.Background is { } background) Fill(canvas, Cell(glyph), background);
        DrawLayers(canvas, document, layout, rows, frame.Selected, false);
        DrawInlineImages(canvas, visible, frame.Selected);
        foreach (var glyph in visible)
        {
            if (glyph.Raster is not null || string.IsNullOrWhiteSpace(glyph.Text)) continue;
            int rgb = glyph.Color ?? (glyph.ButtonId is null ? theme.Foreground : theme.Button);
            if (glyph.ButtonId is { } button && button == frame.Selected) rgb = document.FocusColor;
            DrawText(canvas, glyph.Text, Cell(glyph), Opaque(rgb), glyph.Style);
        }
        DrawLayers(canvas, document, layout, rows, frame.Selected, true);
        if (frame.Tooltip is { } tooltip) DrawTooltip(canvas, tooltip, document.Tooltip, columns, rows);
        canvas.Restore();
    }

    private SKRect Cell(CellGlyph glyph) => new(glyph.X * CellWidth, glyph.Y * CellHeight, (glyph.X + glyph.Width) * CellWidth, (glyph.Y + 1) * CellHeight);
    private static SKColor Opaque(int rgb) => new(0xff000000u | (uint)rgb);
    private void Fill(SKCanvas canvas, SKRect rect, uint argb)
    {
        paint.Style = SKPaintStyle.Fill; paint.Color = new SKColor(argb);
        canvas.DrawRect(rect, paint);
    }

    // 与 RasterComposition.Layers 的摆放规则相同，但背景图直接按原图缩放，不在每帧重新采样。
    private void DrawLayers(SKCanvas canvas, PresentationDocument document, LayoutSnapshot layout, int rows, long? selected, bool foreground)
    {
        float scale = CellWidth / 8f;
        float width = layout.Width * 8f, height = rows * 16f;
        var layers = new List<(long Depth, SKRect Target, RasterImage Image)>();
        if (!foreground)
            foreach (var layer in document.BackgroundLayers.Where(l => l.BackgroundName is not null).OrderByDescending(l => l.BackgroundDepth))
            {
                if (layer.Width <= 0 || layer.Height <= 0) continue;
                float fit = Math.Max(width / layer.Width, height / layer.Height);
                float w = layer.Width * fit, h = layer.Height * fit, left = (width - w) / 2;
                layers.Add((long.MaxValue, new(left, 0, left + w, h), new(layer.Width, layer.Height, layer.Pixels)));
            }
        foreach (var layer in layout.ImageLayers.Where(l => (l.Depth < 0) == foreground))
            layers.Add((layer.Depth, SKRect.Create(layer.X, layer.Y, layer.Image.Width, layer.Image.Height), layer.Image));
        foreach (var layer in document.BackgroundLayers.Where(l => !l.ButtonMap && l.BackgroundName is null && (l.Z < 0) == foreground))
        {
            var image = layer.ButtonValue is { } value && selected == -(long)value - 1 && layer.SelectedImage is { } chosen ? chosen : new RasterImage(layer.Width, layer.Height, layer.Pixels);
            layers.Add((layer.Z, SKRect.Create(layer.X, layer.Y + height - layer.Height, layer.Width, layer.Height), image));
        }
        foreach (var (_, target, image) in layers.OrderByDescending(l => l.Depth))
            DrawImage(canvas, image, new(target.Left * scale, target.Top * scale, target.Right * scale, target.Bottom * scale), false, false);
    }

    // 内嵌图在排版里拆成逐格字形；这里按原点合并成整张画，避免逐格缩放的接缝，再裁到实际可见的格子范围。
    private void DrawInlineImages(SKCanvas canvas, IEnumerable<CellGlyph> glyphs, long? selected)
    {
        foreach (var group in glyphs.Where(g => g.Raster is not null)
            .GroupBy(g => (g.Raster!, X: g.X - g.RasterColumn, Y: g.Y - g.RasterRow, g.RasterColumns, g.RasterRows)))
        {
            var raster = group.Key.Item1;
            var first = group.First();
            var image = first.ButtonId is { } button && button == selected ? raster.SelectedImage ?? raster.Image : raster.Image;
            var clip = new SKRect(group.Min(g => g.X) * CellWidth, group.Min(g => g.Y) * CellHeight, (group.Max(g => g.X) + 1) * CellWidth, (group.Max(g => g.Y) + 1) * CellHeight);
            var target = new SKRect(group.Key.X * CellWidth, group.Key.Y * CellHeight, (group.Key.X + group.Key.RasterColumns) * CellWidth, (group.Key.Y + group.Key.RasterRows) * CellHeight);
            canvas.Save(); canvas.ClipRect(clip);
            DrawImage(canvas, image, target, raster.FlipX, raster.FlipY);
            canvas.Restore();
        }
    }

    private void DrawImage(SKCanvas canvas, RasterImage image, SKRect target, bool flipX, bool flipY)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.IsDefaultOrEmpty || target.Width <= 0 || target.Height <= 0) return;
        var pixels = ImmutableCollectionsMarshal.AsArray(image.Pixels)!;
        if (pixels.Length < image.Width * image.Height) return;
        var skImage = Images.GetValue(pixels, array => SKImage.FromPixelCopy(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
            MemoryMarshal.AsBytes(array.AsSpan(0, image.Width * image.Height))));
        canvas.Save();
        if (flipX || flipY)
        {
            canvas.Translate(target.MidX, target.MidY); canvas.Scale(flipX ? -1 : 1, flipY ? -1 : 1);
            target.Offset(-target.MidX, -target.MidY);
        }
        bool shrink = target.Width < image.Width || target.Height < image.Height;
        canvas.DrawImage(skImage, target, new SKSamplingOptions(SKFilterMode.Linear, shrink ? SKMipmapMode.Linear : SKMipmapMode.None));
        canvas.Restore();
    }

    private void DrawText(SKCanvas canvas, string text, SKRect cell, SKColor color, int style)
    {
        Rune.DecodeFromUtf16(text, out var rune, out _);
        int codepoint = rune.Value;
        if (BoxDrawing.TryDraw(canvas, paint, codepoint, cell, color)) return;
        var font = Font(Typeface(codepoint), style & 3);
        float width = font.MeasureText(text);
        float baseline = cell.Top + (cell.Height - (descent - ascent)) / 2 - ascent;
        paint.Style = SKPaintStyle.Fill; paint.Color = color;
        canvas.Save();
        // 比格子宽的字（全角符号、回退字体）横向压进格子，比格子窄的居中，和终端一样不压到邻格。
        if (width > cell.Width + 0.5f)
        {
            canvas.Translate(cell.Left, 0); canvas.Scale(cell.Width / width, 1);
            canvas.DrawText(text, 0, baseline, SKTextAlign.Left, font, paint);
        }
        else canvas.DrawText(text, cell.Left + (cell.Width - width) / 2, baseline, SKTextAlign.Left, font, paint);
        canvas.Restore();
        float thickness = Math.Max(1, CellWidth / 10f);
        if ((style & 8) != 0) canvas.DrawRect(cell.Left, baseline + thickness, cell.Width, thickness, paint);
        if ((style & 4) != 0) canvas.DrawRect(cell.Left, cell.MidY, cell.Width, thickness, paint);
    }

    private void DrawTooltip(SKCanvas canvas, (string Text, int X, int Y) tooltip, TooltipSettings settings, int columns, int rows)
    {
        int width = Math.Min(60, columns);
        var help = CellLayout.Build(new(0, tooltip.Text.Split('\n').Select(line => new LogicalLine([new StyledRun(line)])).ToImmutableList()), width, 0);
        int height = Math.Min(help.Rows, rows), left = Math.Clamp(tooltip.X, 0, columns - width), top = Math.Clamp(tooltip.Y + 1, 0, rows - height);
        var box = new SKRect(left * CellWidth, top * CellHeight, (left + width) * CellWidth, (top + height) * CellHeight);
        Fill(canvas, box, 0xff000000u | (uint)settings.Background);
        foreach (var glyph in help.Glyphs.Where(g => g.Y < height && g.X + g.Width <= width && !string.IsNullOrWhiteSpace(g.Text)))
            DrawText(canvas, glyph.Text, Cell(glyph with { X = left + glyph.X, Y = top + glyph.Y }), Opaque(settings.Foreground), glyph.Style);
    }

    private static SKTypeface Monospace()
    {
        foreach (string name in MonospaceNames)
        {
            var typeface = SKTypeface.FromFamilyName(name);
            if (typeface is null) continue;
            using var font = new SKFont(typeface, 16);
            if (Math.Abs(font.MeasureText("M") - font.MeasureText("i")) < 0.01f) return typeface;
            typeface.Dispose();
        }
        return SKTypeface.Default;
    }

    private SKTypeface Typeface(int codepoint)
    {
        if (primary.ContainsGlyph(codepoint)) return primary;
        if (fallbacks.TryGetValue(codepoint, out var typeface)) return typeface;
        typeface = SKFontManager.Default.MatchCharacter(primary.FamilyName, SKFontStyle.Normal, ["zh-CN", "ja-JP"], codepoint);
        if (typeface is not null && !typeface.ContainsGlyph(codepoint)) typeface = null;
        if (typeface is not null)
        {
            if (families.TryGetValue(typeface.FamilyName, out var known)) typeface = known;
            else families[typeface.FamilyName] = typeface;
        }
        typeface ??= Scanned().FirstOrDefault(t => t.ContainsGlyph(codepoint)) ?? primary;
        return fallbacks[codepoint] = typeface;
    }

    // 字体管理器找不到时按名单、再按全部已装字体逐个试；只在第一次缺字时加载。
    private List<SKTypeface> Scanned()
    {
        if (scanned is not null) return scanned;
        scanned = [];
        foreach (string name in FallbackNames.Concat(SKFontManager.Default.FontFamilies))
        {
            if (families.ContainsKey(name)) { scanned.Add(families[name]); continue; }
            var typeface = SKTypeface.FromFamilyName(name);
            if (typeface is null) continue;
            if (!string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase) || families.ContainsKey(typeface.FamilyName))
            { if (typeface != primary && !families.ContainsValue(typeface)) typeface.Dispose(); continue; }
            families[name] = typeface; scanned.Add(typeface);
        }
        return scanned;
    }

    private SKFont Font(SKTypeface typeface, int style)
    {
        if (!fonts.TryGetValue((typeface, style), out var font))
            fonts[(typeface, style)] = font = new SKFont(typeface, FontSize)
            { Edging = SKFontEdging.Antialias, Subpixel = true, Embolden = (style & 1) != 0, SkewX = (style & 2) != 0 ? -0.25f : 0 };
        return font;
    }

    public void Dispose()
    {
        foreach (var font in fonts.Values) font.Dispose();
        foreach (var typeface in families.Values.Distinct().Where(t => t != primary)) typeface.Dispose();
        if (ownsPrimary) primary.Dispose();
        paint.Dispose();
    }
}

/// <summary>框线与方块字符按格子画成几何图形，相邻格子无缝衔接（终端的通行做法，字体字形常常接不上）。</summary>
internal static class BoxDrawing
{
    // 每个字符四个方向的线：0 无，1 细，2 粗，3 双线。顺序为上、右、下、左。
    private static readonly Dictionary<int, (int Up, int Right, int Down, int Left)> Lines = Build();
    private static Dictionary<int, (int, int, int, int)> Build()
    {
        var table = new Dictionary<int, (int, int, int, int)>
        {
            [0x2500] = (0, 1, 0, 1), [0x2501] = (0, 2, 0, 2), [0x2502] = (1, 0, 1, 0), [0x2503] = (2, 0, 2, 0),
            [0x250C] = (0, 1, 1, 0), [0x250F] = (0, 2, 2, 0), [0x2510] = (0, 0, 1, 1), [0x2513] = (0, 0, 2, 2),
            [0x2514] = (1, 1, 0, 0), [0x2517] = (2, 2, 0, 0), [0x2518] = (1, 0, 0, 1), [0x251B] = (2, 0, 0, 2),
            [0x251C] = (1, 1, 1, 0), [0x2523] = (2, 2, 2, 0), [0x2524] = (1, 0, 1, 1), [0x252B] = (2, 0, 2, 2),
            [0x252C] = (0, 1, 1, 1), [0x2533] = (0, 2, 2, 2), [0x2534] = (1, 1, 0, 1), [0x253B] = (2, 2, 0, 2),
            [0x253C] = (1, 1, 1, 1), [0x254B] = (2, 2, 2, 2),
            [0x2550] = (0, 3, 0, 3), [0x2551] = (3, 0, 3, 0), [0x2554] = (0, 3, 3, 0), [0x2557] = (0, 0, 3, 3),
            [0x255A] = (3, 3, 0, 0), [0x255D] = (3, 0, 0, 3), [0x2560] = (3, 3, 3, 0), [0x2563] = (3, 0, 3, 3),
            [0x2566] = (0, 3, 3, 3), [0x2569] = (3, 3, 0, 3), [0x256C] = (3, 3, 3, 3),
            [0x2574] = (0, 0, 0, 1), [0x2575] = (1, 0, 0, 0), [0x2576] = (0, 1, 0, 0), [0x2577] = (0, 0, 1, 0),
        };
        return table;
    }

    public static bool TryDraw(SKCanvas canvas, SKPaint paint, int codepoint, SKRect cell, SKColor color)
    {
        paint.Color = color; paint.Style = SKPaintStyle.Fill;
        if (codepoint is >= 0x2580 and <= 0x2593) { Block(canvas, paint, codepoint, cell, color); return true; }
        bool rounded = codepoint is >= 0x256D and <= 0x2570;
        if (!rounded && !Lines.ContainsKey(codepoint)) return false;
        float light = Math.Max(1, MathF.Round(cell.Width / 8)), heavy = light * 2, gap = light;
        float cx = cell.Left + MathF.Floor(cell.Width / 2), cy = cell.Top + MathF.Floor(cell.Height / 2);
        if (rounded)
        {
            // ╭╮╯╰：竖向半线与横向半线之间用二次曲线连接；线心与直线段的矩形对齐。
            bool up = codepoint is 0x256F or 0x2570, right = codepoint is 0x256D or 0x2570;
            float x = cx - MathF.Floor(light / 2) + light / 2, y = cy - MathF.Floor(light / 2) + light / 2, r = cell.Width / 2;
            using var path = new SKPath();
            path.MoveTo(x, up ? cell.Top : cell.Bottom);
            path.LineTo(x, y + (up ? -r : r));
            path.QuadTo(x, y, x + (right ? r : -r), y);
            path.LineTo(right ? cell.Right : cell.Left, y);
            paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = light; paint.StrokeCap = SKStrokeCap.Butt;
            canvas.DrawPath(path, paint);
            paint.Style = SKPaintStyle.Fill;
            return true;
        }
        var (u, r2, d, l) = Lines[codepoint];
        void Segment(int weight, bool vertical, float from, float to)
        {
            if (weight == 0) return;
            float t = weight == 2 ? heavy : light;
            float a = Math.Min(from, to), b = Math.Max(from, to);
            if (weight == 3)
            {
                if (vertical) { canvas.DrawRect(cx - gap - light, a, light, b - a, paint); canvas.DrawRect(cx + gap, a, light, b - a, paint); }
                else { canvas.DrawRect(a, cy - gap - light, b - a, light, paint); canvas.DrawRect(a, cy + gap, b - a, light, paint); }
                return;
            }
            if (vertical) canvas.DrawRect(cx - MathF.Floor(t / 2), a, t, b - a, paint);
            else canvas.DrawRect(a, cy - MathF.Floor(t / 2), b - a, t, paint);
        }
        // 线段伸到中心再多出半个线宽，拐角处不留缺口。
        float overlap = heavy;
        Segment(u, true, cell.Top, cy + overlap / 2);
        Segment(d, true, cy - overlap / 2, cell.Bottom);
        Segment(l, false, cell.Left, cx + overlap / 2);
        Segment(r2, false, cx - overlap / 2, cell.Right);
        return true;
    }

    private static void Block(SKCanvas canvas, SKPaint paint, int codepoint, SKRect cell, SKColor color)
    {
        float w = cell.Width, h = cell.Height;
        switch (codepoint)
        {
            case 0x2580: canvas.DrawRect(cell.Left, cell.Top, w, h / 2, paint); break;
            case >= 0x2581 and <= 0x2588:
                float lower = h * (codepoint - 0x2580) / 8;
                canvas.DrawRect(cell.Left, cell.Bottom - lower, w, lower, paint); break;
            case >= 0x2589 and <= 0x258F:
                float left = w * (0x2590 - codepoint) / 8;
                canvas.DrawRect(cell.Left, cell.Top, left, h, paint); break;
            case 0x2590: canvas.DrawRect(cell.MidX, cell.Top, w / 2, h, paint); break;
            case >= 0x2591 and <= 0x2593:
                paint.Color = color.WithAlpha((byte)(64 * (codepoint - 0x2590)));
                canvas.DrawRect(cell, paint); paint.Color = color; break;
        }
    }
}
