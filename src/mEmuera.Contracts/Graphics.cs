using System.Collections.Immutable;

namespace mEmuera.Contracts;

public sealed record GraphicsPayload(string Operation, ImmutableArray<EraValue> Arguments, ImmutableArray<long> Matrix = default) : ServicePayload
{
    public int? AnchorLine { get; init; }
    public string FontName { get; init; } = "monospace";
    public int FontSize { get; init; } = 16;
    public uint Foreground { get; init; } = 0xffc0c0c0;
}
public sealed record RasterLayer(long Id, int X, int Y, int Z, int Width, int Height, ImmutableArray<uint> Pixels)
{
    public bool ButtonMap { get; init; }
    public int? ButtonValue { get; init; }
    public RasterImage? SelectedImage { get; init; }
    public string? Tooltip { get; init; }
    public string? BackgroundName { get; init; }
    public long BackgroundDepth { get; init; }
    public string? Source { get; init; }
    public string? SelectedSource { get; init; }
    public bool Animated { get; init; }
    public int Opacity { get; init; } = 255;
    public ImmutableArray<long> Matrix { get; init; } = [];
}
public sealed record GraphicsData(EraValue Value, ImmutableArray<RasterLayer> Background = default) : ServiceData
{
    public ScriptImageLayer? ImageLayer { get; init; }
    public ImmutableArray<long> ResultTail { get; init; }
}
public sealed record ScriptImageLayer(long Depth, int X, int Y, int AnchorLine, RasterImage Image)
{
    public bool BottomAnchored { get; init; }
    public bool FollowScroll { get; init; }
    public int InitialScrollRows { get; init; }
    public string? Source { get; init; }
    public int Opacity { get; init; } = 255;
    public ImmutableArray<long> Matrix { get; init; } = [];
    public ScriptImageLayer WithFrame(RasterImage frame)
    {
        var pixels = new uint[Image.Width * Image.Height];
        for (int y = 0; y < Image.Height; y++)
            for (int x = 0; x < Image.Width; x++)
            {
                uint pixel = frame.Pixels[(int)((long)y * frame.Height / Image.Height) * frame.Width + (int)((long)x * frame.Width / Image.Width)];
                if (Matrix.IsDefaultOrEmpty) pixel = (pixel & 0xffffff) | (uint)((pixel >> 24) * Opacity / 255) << 24;
                else
                {
                    uint Component(int row) => (uint)Math.Clamp((((pixel >> 16) & 255) * (double)Matrix[row * 5] + ((pixel >> 8) & 255) * (double)Matrix[row * 5 + 1]
                        + (pixel & 255) * (double)Matrix[row * 5 + 2] + (pixel >> 24) * (double)Matrix[row * 5 + 3] + 255d * Matrix[row * 5 + 4]) / 256, 0, 255);
                    pixel = Component(3) << 24 | Component(0) << 16 | Component(1) << 8 | Component(2);
                }
                pixels[y * Image.Width + x] = pixel;
            }
        return this with { Image = new(Image.Width, Image.Height, pixels.ToImmutableArray(), frame.Animated) };
    }
}
public sealed record RasterImage(int Width, int Height, ImmutableArray<uint> Pixels, bool Animated = false);
public sealed record InlineRaster(RasterImage Image, int Width, int Height, int Y = 0, string? Source = null)
{
    public ImmutableArray<float> WidthSteps { get; init; }
    public string? Html { get; init; }
    public RasterImage? SelectedImage { get; init; }
    public string? SelectedSource { get; init; }
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
}
public sealed record AnimationFrames(long Epoch, long DocumentRevision, HtmlResourceData? Data, Diagnostic? Error = null) : SessionMessage
{
    public long InputRequestId { get; init; }
}
public sealed record HtmlResourcePayload(string Text, bool ToBuffer, int? ColumnWidth = null, bool AlignRight = false) : ServicePayload
{
    public int? SplitLength { get; init; }
    public bool CountLines { get; init; }
    public bool RemainderOnly { get; init; }
    public bool MeasureLength { get; init; }
    public bool Pixels { get; init; }
}
public sealed record HtmlResourceData(ImmutableDictionary<string, RasterImage> Images) : ServiceData;
public interface IGraphicsService : IAsyncDisposable
{
    ValueTask<GraphicsData> ExecuteAsync(GraphicsPayload request, CancellationToken cancellation);
    ValueTask<HtmlResourceData> HtmlAsync(HtmlResourcePayload request, CancellationToken cancellation) => throw new ScriptException("UNSUPPORTED", "HTML 图像不可用");
}
