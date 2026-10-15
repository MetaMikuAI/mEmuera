using mEmuera.Contracts;

namespace mEmuera.Cells;

public static class RasterComposition
{
    public static IEnumerable<(long Depth, int X, int Y, RasterImage Image)> Layers(PresentationDocument document, LayoutSnapshot layout, int height, long? selected = null)
    {
        foreach (var layer in document.BackgroundLayers.Where(l => l.BackgroundName is not null).OrderByDescending(l => l.BackgroundDepth))
        {
            double scale = Math.Max(layout.Width * 8d / layer.Width, height * 16d / layer.Height);
            int width = Math.Max(1, (int)(layer.Width * scale)), h = Math.Max(1, (int)(layer.Height * scale));
            var image = new ScriptImageLayer(0, 0, 0, 0, new(width, h, [])).WithFrame(new(layer.Width, layer.Height, layer.Pixels)).Image;
            yield return (long.MaxValue, (layout.Width * 8 - width) / 2, 0, image);
        }
        foreach (var item in layout.ImageLayers.Select(layer => (layer.Depth, layer.X, layer.Y, layer.Image))
            .Concat(document.BackgroundLayers.Where(l => !l.ButtonMap && l.BackgroundName is null).Select(layer => ((long)layer.Z, layer.X, layer.Y + height * 16 - layer.Height,
                layer.ButtonValue is { } value && selected == -(long)value - 1 && layer.SelectedImage is { } chosen ? chosen : new RasterImage(layer.Width, layer.Height, layer.Pixels))))
            .OrderByDescending(layer => layer.Item1)) yield return item;
    }
    public static uint? Pixel((long Depth, int X, int Y, RasterImage Image) layer, int column, int row)
    {
        int x = column * 8 + 4 - layer.X, y = row * 16 + 8 - layer.Y;
        return x >= 0 && y >= 0 && x < layer.Image.Width && y < layer.Image.Height ? layer.Image.Pixels[y * layer.Image.Width + x] : null;
    }
    public static int Blend(uint argb, int rgb)
    {
        int alpha = (int)(argb >> 24);
        int Channel(int shift) => (((int)(argb >> shift) & 255) * alpha + (rgb >> shift & 255) * (255 - alpha)) / 255;
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }
}
