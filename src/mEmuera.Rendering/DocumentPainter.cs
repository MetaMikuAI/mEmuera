using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using mEmuera.Contracts;
using SkiaSharp;

namespace mEmuera.Rendering;

/// <param name="ScrollFromBottom">向上滚动的像素；0 表示贴底。</param>
/// <param name="HoverButton">指针或焦点所在按钮，文字改用高亮色。</param>
public sealed record RenderState(int ScrollFromBottom = 0, long? HoverButton = null);

/// <summary>把布局画到 SKCanvas。同一份代码给 Linux 快照测试和 Android 画布使用。</summary>
public sealed class DocumentPainter(DisplayMetrics metrics, FontCatalog fonts) : IDisposable
{
    private const int Bold = 1, Italic = 2, Strike = 4, Underline = 8;
    private readonly Dictionary<(SKTypeface, int), SKFont> fontCache = [];
    private static readonly ConditionalWeakTable<RasterImage, SKImage> Images = new();

    /// <summary>视口顶部对应的文档坐标。原版始终贴底：最后一行画在画布最下面一行，文档比画布短时上方留空（返回负值）。</summary>
    public int ViewTop(DocumentLayout layout, RenderState state)
    {
        int overflow = Math.Max(0, layout.Height - metrics.WindowHeight);
        return layout.Height - metrics.WindowHeight - Math.Clamp(state.ScrollFromBottom, 0, overflow);
    }

    public SKBitmap Render(PresentationDocument document, DocumentLayout layout, RenderState? state = null)
    {
        var bitmap = new SKBitmap(metrics.WindowWidth, metrics.WindowHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, document, layout, state ?? new());
        return bitmap;
    }

    public void Draw(SKCanvas canvas, PresentationDocument document, DocumentLayout layout, RenderState state)
    {
        uint background = document.BackgroundColor is { } color ? 0xff000000u | (uint)color : metrics.Background;
        canvas.Clear(new SKColor(background));
        int top = ViewTop(layout, state), bottom = top + metrics.WindowHeight;
        uint focus = 0xff000000u | (uint)document.FocusColor;
        canvas.Save();
        canvas.Translate(0, -top);
        var layers = Layers(document, layout, top);
        foreach (var layer in layers.Where(l => l.Depth >= 0)) DrawImage(canvas, layer.Image, layer.Target, false, false);
        foreach (var row in layout.Rows)
        {
            if (row.Top >= bottom) break;
            if (row.Top + metrics.LineHeight <= top && !row.Items.Any(i => i.Bounds.Intersects(top, bottom))) continue;
            foreach (var item in row.Items) DrawItem(canvas, item, state, focus);
        }
        foreach (var layer in layers.Where(l => l.Depth < 0)) DrawImage(canvas, layer.Image, layer.Target, false, false);
        canvas.Restore();
    }

    private readonly record struct Layer(long Depth, SKRect Target, RasterImage Image);
    private static readonly ConditionalWeakTable<RasterLayer, RasterImage> LayerImages = new();

    private List<Layer> Layers(PresentationDocument document, DocumentLayout layout, int top)
    {
        var result = new List<Layer>();
        // 背景图：等比铺满窗口并居中。
        foreach (var layer in document.BackgroundLayers.Where(l => l.BackgroundName is not null))
        {
            double scale = Math.Max((double)metrics.WindowWidth / layer.Width, (double)metrics.WindowHeight / layer.Height);
            float width = (float)(layer.Width * scale), height = (float)(layer.Height * scale);
            float x = (metrics.WindowWidth - width) / 2, y = top + (metrics.WindowHeight - height) / 2;
            result.Add(new(long.MaxValue - layer.BackgroundDepth, new(x, y, x + width, y + height), LayerImages.GetValue(layer, ToImage)));
        }
        foreach (var layer in document.ImageLayers)
        {
            int y = layer.BottomAnchored ? top + metrics.WindowHeight - layer.Image.Height + layer.Y
                : layer.Y + (layer.AnchorLine >= 0 && layer.AnchorLine < layout.LineTops.Length ? layout.LineTops[layer.AnchorLine] : 0);
            result.Add(new(layer.Depth, new(layer.X, y, layer.X + layer.Image.Width, y + layer.Image.Height), layer.Image));
        }
        foreach (var layer in document.BackgroundLayers.Where(l => !l.ButtonMap && l.BackgroundName is null))
        {
            float y = top + layer.Y + metrics.WindowHeight - layer.Height;
            result.Add(new(layer.Z, new(layer.X, y, layer.X + layer.Width, y + layer.Height), LayerImages.GetValue(layer, ToImage)));
        }
        return [.. result.OrderByDescending(l => l.Depth)];
    }
    private static RasterImage ToImage(RasterLayer layer) => new(layer.Width, layer.Height, layer.Pixels);

    private void DrawItem(SKCanvas canvas, LayoutItem item, RenderState state, uint focus)
    {
        switch (item)
        {
            case TextItem text: DrawText(canvas, text, state, focus); break;
            case ImageItem image:
                var raster = image.Raster;
                bool selected = state.HoverButton is { } hover && image.Run.ButtonId == hover && raster.SelectedImage is not null;
                DrawImage(canvas, selected ? raster.SelectedImage! : raster.Image, image.Bounds, raster.FlipX, raster.FlipY);
                break;
            case PanelItem panel: DrawPanel(canvas, panel, state, focus); break;
        }
    }

    private void DrawPanel(SKCanvas canvas, PanelItem item, RenderState state, uint focus)
    {
        var panel = item.Panel; var b = item.Bounds;
        var rect = new SKRect(b.X, b.Y, b.Right, b.Bottom);
        float radius = panel.Radius;
        if (panel.Color is { } fill)
        {
            using var paint = new SKPaint { Color = new SKColor(0xff000000u | (uint)fill), IsAntialias = true, Style = SKPaintStyle.Fill };
            canvas.DrawRoundRect(rect, radius, radius, paint);
        }
        if (panel.Border > 0)
        {
            using var paint = new SKPaint { Color = new SKColor(0xff000000u | (uint)(panel.BorderColor ?? (int)(metrics.Foreground & 0xffffff))), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = panel.Border };
            float half = panel.Border / 2f;
            canvas.DrawRoundRect(new SKRect(rect.Left + half, rect.Top + half, rect.Right - half, rect.Bottom - half), radius, radius, paint);
        }
        canvas.Save();
        canvas.ClipRect(rect);
        foreach (var row in item.Content.Rows)
            foreach (var inner in row.Items) DrawItem(canvas, inner, state, focus);
        canvas.Restore();
    }

    private void DrawText(SKCanvas canvas, TextItem item, RenderState state, uint focus)
    {
        var run = item.Run; var b = item.Bounds;
        if (run.Background is { } back)
        {
            using var paint = new SKPaint { Color = new SKColor(back), Style = SKPaintStyle.Fill };
            canvas.DrawRect(b.X, b.Y, b.Width, b.Height, paint);
        }
        uint color = run.ButtonId is { } id && state.HoverButton == id ? focus
            : run.Color is { } rgb ? 0xff000000u | (uint)rgb : metrics.Foreground;
        using var ink = new SKPaint { Color = new SKColor(color), IsAntialias = true, Style = SKPaintStyle.Fill };
        var primary = fonts.Resolve(run.Font ?? metrics.FontName);
        float baseline = Baseline(primary, b.Y);
        foreach (var cluster in item.Clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Text)) continue;
            int rune = char.ConvertToUtf32(cluster.Text, 0);
            var face = fonts.ForRune(primary, rune);
            canvas.DrawText(cluster.Text, b.X + cluster.X, baseline, SKTextAlign.Left, FontFor(face, run.Style), ink);
        }
        if ((run.Style & (Underline | Strike)) != 0)
        {
            using var line = new SKPaint { Color = new SKColor(color), StrokeWidth = Math.Max(1, metrics.FontSize / 16f), Style = SKPaintStyle.Stroke };
            if ((run.Style & Underline) != 0) canvas.DrawLine(b.X, baseline + metrics.FontSize / 10f + 1, b.Right, baseline + metrics.FontSize / 10f + 1, line);
            if ((run.Style & Strike) != 0) canvas.DrawLine(b.X, baseline - metrics.FontSize * 0.3f, b.Right, baseline - metrics.FontSize * 0.3f, line);
        }
    }

    /// <summary>字体的上下行距在行高内居中。</summary>
    private float Baseline(SKTypeface face, int top)
    {
        using var font = new SKFont(face, metrics.FontSize);
        font.GetFontMetrics(out var m);
        float height = m.Descent - m.Ascent;
        return top + (metrics.LineHeight - height) / 2 - m.Ascent;
    }

    private SKFont FontFor(SKTypeface face, int style)
    {
        int key = style & (Bold | Italic);
        if (fontCache.TryGetValue((face, key), out var cached)) return cached;
        var font = new SKFont(face, metrics.FontSize)
        {
            Edging = SKFontEdging.SubpixelAntialias,
            Embolden = (key & Bold) != 0 && !face.IsBold,
            SkewX = (key & Italic) != 0 && !face.IsItalic ? -0.25f : 0,
        };
        fontCache[(face, key)] = font; return font;
    }

    private static void DrawImage(SKCanvas canvas, RasterImage source, PixelRect target, bool flipX, bool flipY)
        => DrawImage(canvas, source, new SKRect(target.X, target.Y, target.Right, target.Bottom), flipX, flipY);
    private static void DrawImage(SKCanvas canvas, RasterImage source, SKRect target, bool flipX, bool flipY)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Pixels.IsDefaultOrEmpty) return;
        var image = Images.GetValue(source, Create);
        canvas.Save();
        if (flipX || flipY)
        {
            canvas.Translate(flipX ? target.Left + target.Right : 0, flipY ? target.Top + target.Bottom : 0);
            canvas.Scale(flipX ? -1 : 1, flipY ? -1 : 1);
        }
        canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
    }
    // 像素为 0xAARRGGBB，小端内存序即 BGRA。
    private static SKImage Create(RasterImage source)
    {
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var array = ImmutableCollectionsMarshal.AsArray(source.Pixels)!;
        return SKImage.FromPixelCopy(info, MemoryMarshal.AsBytes(array.AsSpan()));
    }

    public void Dispose()
    {
        foreach (var font in fontCache.Values) font.Dispose();
        fontCache.Clear();
    }
}
