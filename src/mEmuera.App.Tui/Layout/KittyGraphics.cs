using System.Text;
using mEmuera.Cells;
using mEmuera.Contracts;
using SkiaSharp;

namespace mEmuera.App.Tui.Layout;

public static class KittyGraphics
{
    // Terminal.Gui 的输入解析器尚不能完整吞掉 APC 回应；未知终端默认使用字符格。
    public static bool Detect(Func<string, string?> environment)
        => string.IsNullOrEmpty(environment("TMUX")) && string.IsNullOrEmpty(environment("STY"))
            && (environment("TERM") == "xterm-kitty" || environment("TERM_PROGRAM") == "ghostty");
    public static string Delete(int id) => $"\u001b_Ga=d,d=I,i={id},q=2\u001b\\";
    public static string Transmit(byte[] png, int id, int x, int y, int columns, int rows, int depth)
    {
        string data = Convert.ToBase64String(png);
        var output = new StringBuilder($"\u001b7\u001b[{y + 1};{x + 1}H");
        for (int offset = 0; offset < data.Length; offset += 4096)
        {
            int count = Math.Min(4096, data.Length - offset);
            output.Append("\u001b_G");
            if (offset == 0) output.Append($"a=T,f=100,t=d,i={id},p=1,c={columns},r={rows},z={depth},C=1,q=2,");
            output.Append($"m={(offset + count < data.Length ? 1 : 0)};").Append(data, offset, count).Append("\u001b\\");
        }
        return output.Append("\u001b8").ToString();
    }
    public static byte[] Compose(PresentationDocument document, LayoutSnapshot layout, int height, long? selected, bool foreground)
    {
        using var bitmap = new SKBitmap(layout.Width * 8, height * 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var cache = new Dictionary<RasterImage, SKBitmap>();
        try
        {
        void Draw(RasterImage image, SKRect source, SKRect target, bool flipX = false, bool flipY = false)
        {
            if (!cache.TryGetValue(image, out var pixels))
            {
                pixels = new SKBitmap(image.Width, image.Height);
                pixels.Pixels = image.Pixels.Select(p => new SKColor(p)).ToArray();
                cache.Add(image, pixels);
            }
            canvas.Save();
            if (flipX || flipY)
            {
                canvas.Translate(target.MidX, target.MidY); canvas.Scale(flipX ? -1 : 1, flipY ? -1 : 1);
                target.Offset(-target.MidX, -target.MidY);
            }
            canvas.DrawBitmap(pixels, source, target); canvas.Restore();
        }
        foreach (var layer in RasterComposition.Layers(document, layout, height, selected).Where(l => (l.Depth < 0) == foreground))
            Draw(layer.Image, new(0, 0, layer.Image.Width, layer.Image.Height), new(layer.X, layer.Y, layer.X + layer.Image.Width, layer.Y + layer.Image.Height));
        if (!foreground)
            foreach (var glyph in layout.Glyphs)
            {
                if (glyph.Raster is not { } raster) continue;
                var image = glyph.ButtonId is { } button && button == selected ? raster.SelectedImage ?? raster.Image : raster.Image;
                float left = (float)glyph.RasterColumn / glyph.RasterColumns, right = (float)(glyph.RasterColumn + 1) / glyph.RasterColumns;
                float top = (float)glyph.RasterRow / glyph.RasterRows, bottom = (float)(glyph.RasterRow + 1) / glyph.RasterRows;
                if (raster.FlipX) (left, right) = (1 - right, 1 - left);
                if (raster.FlipY) (top, bottom) = (1 - bottom, 1 - top);
                Draw(image, new(left * image.Width, top * image.Height, right * image.Width, bottom * image.Height),
                    new(glyph.X * 8, glyph.Y * 16, (glyph.X + 1) * 8, (glyph.Y + 1) * 16), raster.FlipX, raster.FlipY);
            }
        canvas.Flush();
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
        }
        finally { foreach (var pixels in cache.Values) pixels.Dispose(); }
    }
}
