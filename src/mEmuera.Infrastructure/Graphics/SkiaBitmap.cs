using SkiaSharp;

namespace mEmuera.Infrastructure.Graphics;

internal sealed class SkiaBitmap : IDisposable
{
    public SKBitmap Bitmap { get; }
    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;
    public SKColor this[int x, int y] { get => Bitmap.GetPixel(x, y); set => Bitmap.SetPixel(x, y, value); }
    public SkiaBitmap(int width, int height)
    {
        Bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Bitmap.Erase(SKColors.Transparent);
    }
    private SkiaBitmap(SKBitmap bitmap) => Bitmap = bitmap;
    public SkiaBitmap Clone() => new(Bitmap.Copy());
    public static Task<SKImageInfo> IdentifyAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var codec = SKCodec.Create(path) ?? throw new IOException("无法识别图像: " + path);
        return Task.FromResult(codec.Info);
    }
    public static Task<SkiaBitmap> LoadAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(new SkiaBitmap(SKBitmap.Decode(path) ?? throw new IOException("无法解码图像: " + path)));
    }
    public void Dispose() => Bitmap.Dispose();
}
