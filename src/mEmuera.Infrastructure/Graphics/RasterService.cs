using System.Collections.Immutable;
using mEmuera.Contracts;
using mEmuera.Infrastructure.FileSystem;
using SkiaSharp;

namespace mEmuera.Infrastructure.Graphics;

public sealed partial class RasterService(GamePathResolver paths, IGameSource source, SourceManifest manifest, TimeProvider? clock = null,
    long maxCanvasPixels = 128_000_000) : IGraphicsService
{
    private sealed record Sprite(string File, int X, int Y, int Width, int Height, int DestWidth, int DestHeight, bool Animated,
        long? GraphicsId = null, int OffsetX = 0, int OffsetY = 0)
    {
        public List<Frame> Frames { get; } = [];
        public long? Started { get; set; }
    }
    private sealed record Frame(Sprite Image, int Delay);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<long, SkiaBitmap> graphics = [];
    private readonly Dictionary<long, SKColor> brushes = [];
    private readonly Dictionary<string, Sprite> sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Sprite> definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SkiaBitmap> files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Width, int Height)> dimensions = new(StringComparer.Ordinal);
    private long cachedPixels;
    private readonly List<(long Id, int X, int Y, int Z, int Width, int Height)> backgrounds = [];
    private readonly SemaphoreSlim gate = new(1);
    private bool indexed, disposed;
    private long allocated;
    private async Task IndexSprites(CancellationToken ct)
    {
        if (indexed) return;
        var candidate = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files.Where(f => f.Category == "CSV" && f.Id.Path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)))
        {
            string directory = file.Id.Path[..(file.Id.Path.LastIndexOf('/') + 1)];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in (await source.ReadAsync(file.Id, ct)).Text.Split('\n'))
            {
                ct.ThrowIfCancellationRequested(); string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                string[] values = line.Split(',').Select(v => v.Trim()).ToArray();
                if (values.Length < 2) continue;
                int Number(int i) => i < values.Length && int.TryParse(values[i], out int n) ? n : 0;
                bool animated = values[1].Equals("ANIME", StringComparison.OrdinalIgnoreCase);
                if (animated)
                {
                    if (Number(2) <= 0 || Number(3) <= 0) continue;
                    Size(Number(2), Number(3));
                    candidate[values[0]] = new("", 0, 0, Number(2), Number(3), Number(2), Number(3), true);
                    seen.Add(values[0]); continue;
                }
                var image = new Sprite(directory + values[1].Replace('\\', '/'), Number(2), Number(3), Number(4), Number(5), Number(9), Number(10), false, OffsetX: Number(6), OffsetY: Number(7));
                if (seen.Add(values[0])) candidate[values[0]] = image;
                else if (candidate[values[0]].Animated)
                {
                    if (candidate[values[0]].Frames.Count >= 10000) throw new ScriptException("GRAPHICS_LIMIT", "动画帧数超限");
                    candidate[values[0]].Frames.Add(new(image, Math.Max(1, Number(8))));
                }
            }
        }
        foreach (var item in candidate) { definitions.Add(item.Key, item.Value); sprites.Add(item.Key, Copy(item.Value)); }
        indexed = true;
    }
    private static Sprite Copy(Sprite source)
    {
        var result = new Sprite(source.File, source.X, source.Y, source.Width, source.Height, source.DestWidth, source.DestHeight,
            source.Animated, source.GraphicsId, source.OffsetX, source.OffsetY);
        result.Frames.AddRange(source.Frames); return result;
    }
    private static void Size(int width, int height)
    {
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 16_000_000)
            throw new ScriptException("GRAPHICS_SIZE", "图像尺寸或像素总量超限");
    }
    private async Task<SkiaBitmap> Load(string file, CancellationToken ct)
    {
        if (files.TryGetValue(file, out var cached)) return cached;
        string path = paths.Source(file);
        var info = await SkiaBitmap.IdentifyAsync(path, ct); Size(info.Width, info.Height);
        long pixels = (long)info.Width * info.Height;
        while (cachedPixels + pixels > 16_000_000 && files.Count > 0)
        {
            var oldest = files.First(); files.Remove(oldest.Key);
            cachedPixels -= (long)oldest.Value.Width * oldest.Value.Height; oldest.Value.Dispose();
        }
        var bitmap = await SkiaBitmap.LoadAsync(path, ct);
        dimensions[file] = (bitmap.Width, bitmap.Height);
        files.Add(file, bitmap); cachedPixels += pixels; return bitmap;
    }
    private bool Created(Sprite sprite) => sprite.Animated || (sprite.GraphicsId is { } id
        ? graphics.ContainsKey(id) : File.Exists(paths.Source(sprite.File)));
    private async Task<SkiaBitmap> Snapshot(Sprite sprite, CancellationToken ct)
    {
        if (sprite.Animated)
        {
            var canvas = new SkiaBitmap(sprite.DestWidth, sprite.DestHeight);
            if (sprite.Frames.Count == 0) return canvas;
            long now = time.GetTimestamp(); sprite.Started ??= now;
            long elapsed = (long)time.GetElapsedTime(sprite.Started.Value, now).TotalMilliseconds % sprite.Frames.Sum(f => (long)f.Delay);
            Frame frame = sprite.Frames[0];
            foreach (var item in sprite.Frames) { frame = item; elapsed -= item.Delay; if (elapsed <= 0) break; }
            if (!Created(frame.Image)) return canvas;
            using var pixels = await Snapshot(frame.Image, ct);
            ct.ThrowIfCancellationRequested();
            using var drawing = new SKCanvas(canvas.Bitmap);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            drawing.DrawBitmap(pixels.Bitmap, frame.Image.OffsetX, frame.Image.OffsetY, paint);
            drawing.Flush();
            return canvas;
        }
        SkiaBitmap bitmap = sprite.GraphicsId is { } id ? graphics[id] : await Load(sprite.File, ct);
        {
            int sx = sprite.Width == 0 ? 0 : sprite.X, sy = sprite.Width == 0 ? 0 : sprite.Y;
            int sw = sprite.Width == 0 ? bitmap.Width : sprite.Width, sh = sprite.Width == 0 ? bitmap.Height : sprite.Height;
            Size(Math.Abs(sw), Math.Abs(sh));
            ct.ThrowIfCancellationRequested();
            var result = new SkiaBitmap(Math.Abs(sw), Math.Abs(sh));
            using var drawing = new SKCanvas(result.Bitmap);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            drawing.Translate(sw > 0 ? -sx : sx, sh > 0 ? -sy : sy);
            drawing.Scale(sw > 0 ? 1 : -1, sh > 0 ? 1 : -1);
            drawing.DrawBitmap(bitmap.Bitmap, 0, 0, paint);
            drawing.Flush();
            return result;
        }
    }
    private ImmutableArray<RasterLayer> Layers()
    {
        var result = ImmutableArray.CreateBuilder<RasterLayer>();
        result.AddRange(spriteBackgrounds);
        if (buttonMapId is { } mapId && graphics.TryGetValue(mapId, out var map))
            result.Add(new(mapId, 0, 0, 0, map.Width, map.Height, map.Bitmap.Pixels.Select(Argb).ToImmutableArray()) { ButtonMap = true });
        foreach (var position in backgrounds.OrderByDescending(p => p.Z))
        {
            long id = position.Id;
            if (!graphics.TryGetValue(id, out var image)) continue;
            var pixels = new uint[position.Width * position.Height];
            for (int y = 0; y < Math.Min(position.Height, image.Height); y++)
                for (int x = 0; x < Math.Min(position.Width, image.Width); x++) pixels[y * position.Width + x] = Argb(image[x, y]);
            result.Add(new(id, position.X, position.Y, position.Z, position.Width, position.Height, pixels.ToImmutableArray()));
        }
        return result.ToImmutable();
    }
    private bool HasBackground(long id) => buttonMapId == id || backgrounds.Any(p => p.Id == id);
    private static uint Argb(SKColor p) => (uint)(p.Alpha << 24 | p.Red << 16 | p.Green << 8 | p.Blue);
    public async ValueTask<HtmlResourceData> HtmlAsync(HtmlResourcePayload request, CancellationToken cancellation)
    {
        var fragment = HtmlFragment.Parse(request.Text);
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await IndexSprites(cancellation);
            var images = ImmutableDictionary.CreateBuilder<string, RasterImage>(StringComparer.OrdinalIgnoreCase);
            long pixelsTotal = 0;
            foreach (var element in fragment.Descendants().Where(e => e.Name.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase)))
            {
                string sourceName = element.Attribute("src")?.Value ?? throw new ScriptException("HTML_IMAGE", "img 缺少 src");
                foreach (string name in new[] { sourceName, element.Attribute("srcb")?.Value }.OfType<string>())
                {
                    if (images.ContainsKey(name) || !sprites.TryGetValue(name, out var sprite)) continue;
                    if (!Created(sprite)) continue;
                    using var bitmap = await Snapshot(sprite, cancellation);
                    int sx = 0, sy = 0, sw = bitmap.Width, sh = bitmap.Height;
                    int dw = sprite.DestWidth > 0 ? sprite.DestWidth : sw, dh = sprite.DestHeight > 0 ? sprite.DestHeight : sh;
                    if (!sprite.Animated && sprite.GraphicsId is null && (sprite.DestWidth <= 0 || sprite.DestHeight <= 0))
                    { dw = sw; dh = sh; }
                    Size(dw, dh); pixelsTotal += (long)dw * dh;
                    if (pixelsTotal > 16_000_000) throw new ScriptException("GRAPHICS_LIMIT", "HTML 图像总量超限");
                    if (sx < 0 || sy < 0 || sw <= 0 || sh <= 0 || sx + sw > bitmap.Width || sy + sh > bitmap.Height) throw new ScriptException("SPRITE_RECT", "精灵裁剪区域越界");
                    var pixels = new uint[dw * dh];
                    bool offset = !sprite.Animated && (sprite.OffsetX != 0 || sprite.OffsetY != 0);
                    for (int y = 0; y < dh; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        for (int x = 0; x < dw; x++)
                        {
                            if (offset)
                            {
                                long bx = (long)x - sprite.OffsetX, by = (long)y - sprite.OffsetY;
                                if (bx >= 0 && bx < sw && by >= 0 && by < sh) pixels[y * dw + x] = Argb(bitmap[(int)bx, (int)by]);
                            }
                            else pixels[y * dw + x] = Argb(bitmap[sx + (int)((long)x * sw / dw), sy + (int)((long)y * sh / dh)]);
                        }
                    }
                    images.Add(name, new(dw, dh, pixels.ToImmutableArray(), sprite.Animated));
                }
            }
            return new(images.ToImmutable());
        }
        finally { gate.Release(); }
    }
    public async ValueTask<GraphicsData> ExecuteAsync(GraphicsPayload request, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = await ExecuteCoreAsync(request, cancellation);
            await RefreshBackgrounds(cancellation);
            return spriteBackgrounds.Count > 0 || !result.Background.IsDefault ? result with { Background = Layers() } : result;
        }
        finally { gate.Release(); }
    }
    private async ValueTask<GraphicsData> ExecuteCoreAsync(GraphicsPayload request, CancellationToken cancellation)
    {
        {
            var args = request.Arguments; string operation = request.Operation;
            int Int(int i) => checked((int)args[i].AsInt());
            GraphicsData Result(long value, bool changed = false) => new(new(value), changed ? Layers() : default);
            if (await ExecuteDrawingAsync(request, cancellation) is { } drawingResult) return drawingResult;
            if (await ExecuteBackgroundAsync(request, cancellation) is { } backgroundResult) return backgroundResult;
            if (operation is "SPRITEDISPOSEALL" or "SPRITECREATEFROMFILE" or "SPRITEMOVE" or "SPRITESETPOS" or "SPRITEGETCOLOR")
            {
                await IndexSprites(cancellation);
                if (operation == "SPRITEDISPOSEALL")
                {
                    int count = sprites.Count; sprites.Clear();
                    foreach (var bitmap in files.Values) bitmap.Dispose();
                    files.Clear(); cachedPixels = 0; return Result(count);
                }
                string name = args[0].AsString();
                sprites.TryGetValue(name, out var sprite);
                if (operation == "SPRITECREATEFROMFILE")
                {
                    if (name.Length == 0 || sprite is not null && Created(sprite)) return Result(0);
                    string file = args[1].AsString().Replace('\\', '/');
                    if (args[2].AsInt() == 0) file = "resources/" + file;
                    if (!File.Exists(paths.Source(file))) return Result(0);
                    try
                    {
                        var bitmap = await Load(file, cancellation);
                        sprites[name] = new(file, 0, 0, bitmap.Width, bitmap.Height, bitmap.Width, bitmap.Height, false);
                        return Result(1);
                    }
                    catch (IOException) { return Result(0); }
                }
                if (sprite is null || !Created(sprite)) return Result(operation == "SPRITEGETCOLOR" ? -1 : 0);
                if (operation == "SPRITEGETCOLOR")
                {
                    using var bitmap = await Snapshot(sprite, cancellation);
                    int x = Int(1), y = Int(2);
                    return Result(x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height ? -1 : Argb(bitmap[x, y]));
                }
                sprites[name] = sprite with
                {
                    OffsetX = checked(Int(1) + (operation == "SPRITEMOVE" ? sprite.OffsetX : 0)),
                    OffsetY = checked(Int(2) + (operation == "SPRITEMOVE" ? sprite.OffsetY : 0))
                };
                return Result(1);
            }
            if (operation is "SETIMAGELAYERL" or "SETIMAGELAYER")
            {
                await IndexSprites(cancellation);
                if (!sprites.TryGetValue(args[0].AsString(), out var image) || !Created(image)) return Result(0);
                using var bitmap = await Snapshot(image, cancellation);
                int width = Int(4) > 0 ? Int(4) : image.DestWidth > 0 ? image.DestWidth : bitmap.Width;
                int height = Int(5) > 0 ? Int(5) : image.DestHeight > 0 ? image.DestHeight : bitmap.Height;
                Size(width, height);
                var pixels = new uint[bitmap.Width * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int x = 0; x < bitmap.Width; x++) pixels[y * bitmap.Width + x] = Argb(bitmap[x, y]);
                }
                var layer = new ScriptImageLayer(args[1].AsInt(), Int(2), Int(3), request.AnchorLine ?? 0, new(width, height, []))
                { Source = args[0].AsString(), Opacity = Math.Clamp(Int(6), 0, 255), Matrix = request.Matrix.IsDefault ? [] : request.Matrix,
                    BottomAnchored = operation == "SETIMAGELAYER", FollowScroll = operation == "SETIMAGELAYER" && args[8].AsInt() != 0 };
                return new(new EraValue(1L)) { ImageLayer = layer.WithFrame(new(bitmap.Width, bitmap.Height, pixels.ToImmutableArray(), image.Animated)) };
            }
            if (operation is "$SPRITE_ACTIVE" or "$SPRITE_ANIMATION")
            {
                await IndexSprites(cancellation);
                bool active = sprites.TryGetValue(args[0].AsString(), out var sprite) && Created(sprite);
                return Result(active && (operation == "$SPRITE_ACTIVE" || sprite!.Animated) ? 1 : 0);
            }
            if (operation == "$GDRAW_MASK_SIZE")
            {
                long sourceId = args[0].AsInt(), maskId = args[1].AsInt();
                if (sourceId is < 0 or > int.MaxValue || maskId is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
                return Result(graphics.TryGetValue(sourceId, out var original) && graphics.TryGetValue(maskId, out var mask)
                    && original.Width == mask.Width && original.Height == mask.Height ? 1 : 0);
            }
            if (operation == "$SPRITE_RECT_READY")
            {
                long parentId = args[0].AsInt();
                if (parentId is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
                if (!graphics.TryGetValue(parentId, out var parent)) return Result(0);
                int x = Int(1), y = Int(2), width = Int(3), height = Int(4);
                if (args[5].AsInt() == 1)
                    return Result(x >= 0 && y >= 0 && width > 0 && height > 0 && (long)x + width <= parent.Width && (long)y + height <= parent.Height ? 1 : 0);
                Size(Math.Abs(width), Math.Abs(height));
                if (Math.Max(x, (long)x + width) <= 0 || Math.Min(x, (long)x + width) >= parent.Width
                    || Math.Max(y, (long)y + height) <= 0 || Math.Min(y, (long)y + height) >= parent.Height)
                    throw new ScriptException("SPRITE_RECT", "精灵裁剪区域在图像之外");
                return Result(1);
            }
            if (operation == "CBGCLEAR") { backgrounds.Clear(); spriteBackgrounds.RemoveAll(l => l.BackgroundName is null); buttonMapId = null; return Result(1, true); }
            if (operation is "SPRITECREATE" or "SPRITEDISPOSE" or "SPRITEANIMECREATE" or "SPRITEANIMEADDFRAME")
            {
                await IndexSprites(cancellation);
                string name = args[0].AsString(); if (name.Length == 0) return Result(0);
                sprites.TryGetValue(name, out var existing);
                if (operation == "SPRITEDISPOSE")
                {
                    if (existing is null || !Created(existing)) return Result(0);
                    if (definitions.TryGetValue(name, out var definition)) sprites[name] = Copy(definition);
                    else sprites.Remove(name);
                    return Result(1);
                }
                if (operation == "SPRITEANIMECREATE")
                {
                    if (existing is not null && Created(existing)) return Result(0);
                    Size(Int(1), Int(2)); sprites[name] = new("", 0, 0, Int(1), Int(2), Int(1), Int(2), true); return Result(1);
                }
                long parentId = args[1].AsInt();
                if (parentId is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
                if (!graphics.TryGetValue(parentId, out var parent)) return Result(0);
                if (operation == "SPRITEANIMEADDFRAME")
                {
                    if (existing is null || !existing.Animated) return Result(0);
                    int x = Int(2), y = Int(3), width = Int(4), height = Int(5);
                    long delay = args[8].AsInt();
                    if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > parent.Width || (long)y + height > parent.Height || delay <= 0 || delay > int.MaxValue) return Result(0);
                    if (existing.Frames.Count >= 10000) throw new ScriptException("GRAPHICS_LIMIT", "动画帧数超限");
                    existing.Frames.Add(new(new("", x, y, width, height, width, height, false, parentId, Int(6), Int(7)), (int)delay));
                    return Result(1);
                }
                if (existing is not null && Created(existing)) return Result(0);
                int sx = args.Length >= 6 ? Int(2) : 0, sy = args.Length >= 6 ? Int(3) : 0;
                int sw = args.Length >= 6 ? Int(4) : parent.Width, sh = args.Length >= 6 ? Int(5) : parent.Height;
                Size(Math.Abs(sw), Math.Abs(sh));
                if (Math.Max(sx, (long)sx + sw) <= 0 || Math.Min(sx, (long)sx + sw) >= parent.Width || Math.Max(sy, (long)sy + sh) <= 0 || Math.Min(sy, (long)sy + sh) >= parent.Height)
                    throw new ScriptException("SPRITE_RECT", "精灵裁剪区域在图像之外");
                int dw = args.Length == 10 ? Math.Abs(Int(8)) : Math.Abs(sw), dh = args.Length == 10 ? Math.Abs(Int(9)) : Math.Abs(sh);
                Size(dw, dh);
                sprites[name] = new("", sx, sy, sw, sh, dw, dh, false, parentId, args.Length >= 8 ? Int(6) : 0, args.Length >= 8 ? Int(7) : 0);
                return Result(1);
            }
            if (operation is "SPRITEWIDTH" or "SPRITEHEIGHT" or "SPRITECREATED" or "SPRITEPOSX" or "SPRITEPOSY")
            {
                await IndexSprites(cancellation);
                if (!sprites.TryGetValue(args[0].AsString(), out var sprite)) return Result(0);
                if (operation == "SPRITECREATED") return Result(1);
                if (operation is "SPRITEPOSX" or "SPRITEPOSY") return Result(operation == "SPRITEPOSX" ? sprite.OffsetX : sprite.OffsetY);
                if (sprite.DestWidth > 0 && sprite.DestHeight > 0) return Result(operation == "SPRITEWIDTH" ? sprite.DestWidth : sprite.DestHeight);
                if (!Created(sprite)) return Result(0);
                int width = Math.Abs(sprite.Width), height = Math.Abs(sprite.Height);
                if (width == 0)
                {
                    if (!dimensions.TryGetValue(sprite.File, out var size))
                    {
                        var info = await SkiaBitmap.IdentifyAsync(paths.Source(sprite.File), cancellation); Size(info.Width, info.Height);
                        dimensions[sprite.File] = size = (info.Width, info.Height);
                    }
                    width = size.Width; height = size.Height;
                }
                return Result(operation == "SPRITEWIDTH" ? width : height);
            }
            long id = args[0].AsInt();
            if (id is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
            graphics.TryGetValue(id, out var destination);
            if (operation == "GCREATEFROMFILE")
            {
                if (destination is not null) return Result(0);
                string file = args[1].AsString().Replace('\\', '/');
                string path = paths.Source(args[2].AsInt() != 0 ? file : "resources/" + file);
                if (!File.Exists(path)) return Result(0);
                try
                {
                    var info = await SkiaBitmap.IdentifyAsync(path, cancellation); Size(info.Width, info.Height);
                    if (allocated + (long)info.Width * info.Height > maxCanvasPixels) throw new ScriptException("GRAPHICS_LIMIT", "离屏图像总量超限");
                    var loaded = await SkiaBitmap.LoadAsync(path, cancellation);
                    graphics.Add(id, loaded); allocated += (long)loaded.Width * loaded.Height; return Result(1, HasBackground(id));
                }
                catch (Exception error) when (error is IOException) { return Result(0); }
            }
            if (operation == "GCREATE")
            {
                if (destination is not null) return Result(0);
                int width = Int(1), height = Int(2); Size(width, height);
                if (allocated + (long)width * height > maxCanvasPixels) throw new ScriptException("GRAPHICS_LIMIT", $"离屏图像总量超限：已有 {allocated} 像素，请求 {width}×{height}，上限 {maxCanvasPixels}");
                graphics.Add(id, new(width, height)); allocated += (long)width * height; return Result(1, HasBackground(id));
            }
            if (operation == "GDISPOSE")
            {
                if (destination is null) return Result(0);
                allocated -= (long)destination.Width * destination.Height; destination.Dispose(); graphics.Remove(id);
                brushes.Remove(id);
                if (drawingStates.Remove(id, out var state)) state.Dispose();
                return Result(1, HasBackground(id));
            }
            if (operation is "GCREATED" or "GWIDTH" or "GHEIGHT")
                return Result(destination is null ? 0 : operation == "GWIDTH" ? destination.Width : operation == "GHEIGHT" ? destination.Height : 1);
            if (destination is null) return Result(operation == "GGETCOLOR" ? -1 : 0);
            if (operation is "GDRAWG" or "GDRAWGWITHMASK")
            {
                long sourceId = args[1].AsInt();
                if (sourceId is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
                if (!graphics.TryGetValue(sourceId, out var original)) return Result(0);
                using var bitmap = original.Clone();
                if (operation == "GDRAWGWITHMASK")
                {
                    long maskId = args[2].AsInt();
                    if (maskId is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
                    if (!graphics.TryGetValue(maskId, out var originalMask) || originalMask.Width != bitmap.Width || originalMask.Height != bitmap.Height) return Result(0);
                    int dx = Int(3), dy = Int(4);
                    if ((long)dx + bitmap.Width > destination.Width || (long)dy + bitmap.Height > destination.Height) return Result(0);
                    using var mask = originalMask.Clone();
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            var pixel = bitmap[x, y]; pixel = pixel.WithAlpha((byte)((pixel.Alpha * mask[x, y].Alpha + 127) / 255));
                            bitmap[x, y] = pixel;
                        }
                    }
                    DrawBitmap(id, destination, bitmap, dx, dy, bitmap.Width, bitmap.Height, 0, 0, bitmap.Width, bitmap.Height, default);
                }
                else
                {
                    int dx = Int(2), dy = Int(3), dw = Int(4), dh = Int(5), sx = Int(6), sy = Int(7), sw = Int(8), sh = Int(9);
                    if (dw == 0 || dh == 0 || sw == 0 || sh == 0) return Result(1);
                    Size(Math.Abs(dw), Math.Abs(dh));
                    DrawBitmap(id, destination, bitmap, dx, dy, dw, dh, sx, sy, sw, sh, request.Matrix);
                }
                return Result(1, HasBackground(id));
            }
            if (operation is "GSETCOLOR" or "GSETBRUSH" or "GFILLRECTANGLE")
            {
                long value = operation == "GFILLRECTANGLE" ? args[5].AsInt() : args[1].AsInt();
                if (value < 0 || value > uint.MaxValue) throw new ScriptException("COLOR", "ARGB 颜色越界");
                uint color = unchecked((uint)value);
                var pixel = new SKColor((byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)(color >> 24));
                if (operation == "GSETBRUSH") { brushes[id] = pixel; return Result(1); }
                if (operation == "GSETCOLOR")
                {
                    int x = Int(2), y = Int(3);
                    if (x < 0 || y < 0 || x >= destination.Width || y >= destination.Height) return Result(0);
                    destination[x, y] = pixel; return Result(1, HasBackground(id));
                }
                pixel = brushes.GetValueOrDefault(id, pixel);
                int left = Int(1), top = Int(2), width = Int(3), height = Int(4);
                if (width == 0 || height == 0) throw new ScriptException("GRAPHICS_SIZE", "矩形宽高不能为零");
                long right = (long)left + width, bottom = (long)top + height;
                long minX = Math.Min(left, right), maxX = Math.Max(left, right);
                long minY = Math.Min(top, bottom), maxY = Math.Max(top, bottom);
                using var canvas = new SKCanvas(destination.Bitmap);
                if (drawingStates.TryGetValue(id, out var drawing)) canvas.SetMatrix(drawing.Transform);
                using var paint = new SKPaint { Color = pixel, Style = SKPaintStyle.Fill };
                canvas.DrawRect(new SKRect(minX, minY, maxX, maxY), paint);
                canvas.Flush();
                return Result(1, HasBackground(id));
            }
            if (operation == "GCLEAR")
            {
                long value = args[1].AsInt();
                if (value < 0 || value > uint.MaxValue) throw new ScriptException("COLOR", "ARGB 颜色越界");
                uint color = (uint)value;
                var pixel = new SKColor((byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)(color >> 24));
                int x = args.Length == 6 ? Int(2) : 0, y = args.Length == 6 ? Int(3) : 0;
                long right = args.Length == 6 ? (long)x + Int(4) : destination.Width, bottom = args.Length == 6 ? (long)y + Int(5) : destination.Height;
                using var canvas = new SKCanvas(destination.Bitmap);
                using var paint = new SKPaint { Color = pixel, BlendMode = SKBlendMode.Src };
                if (right > x && bottom > y) canvas.DrawRect(new SKRect(x, y, right, bottom), paint);
                canvas.Flush();
                return Result(1, HasBackground(id));
            }
            if (operation == "CBGSETG")
            {
                int z = Int(3); if (z == 0) throw new ScriptException("GRAPHICS_DEPTH", "背景深度不能为零");
                backgrounds.Add((id, Int(1), Int(2), z, destination.Width, destination.Height)); return Result(1, true);
            }
            if (operation == "GGETCOLOR")
            {
                int x = Int(1), y = Int(2);
                if (x < 0 || y < 0 || x >= destination.Width || y >= destination.Height) return Result(-1);
                return Result(Argb(destination[x, y]));
            }
            if (operation == "GDRAWSPRITE")
            {
                await IndexSprites(cancellation);
                if (!sprites.TryGetValue(args[1].AsString(), out var sprite) || !Created(sprite)) return Result(0);
                using var bitmap = await Snapshot(sprite, cancellation);
                int sx = 0, sy = 0, sw = bitmap.Width, sh = bitmap.Height;
                int dx = Int(2), dy = Int(3), dw = Int(4), dh = Int(5);
                if (dw == 0) dw = sprite.DestWidth > 0 ? sprite.DestWidth : sw;
                if (dh == 0) dh = sprite.DestHeight > 0 ? sprite.DestHeight : sh;
                if (sprite.OffsetX != 0 || sprite.OffsetY != 0)
                {
                    int baseWidth = sprite.DestWidth > 0 ? sprite.DestWidth : sw, baseHeight = sprite.DestHeight > 0 ? sprite.DestHeight : sh;
                    dx += sprite.OffsetX * dw / baseWidth; dy += sprite.OffsetY * dh / baseHeight;
                    dw = dw * sw / baseWidth; dh = dh * sh / baseHeight;
                }
                Size(Math.Abs(dw), Math.Abs(dh));
                if (sx < 0 || sy < 0 || sw <= 0 || sh <= 0 || sx + sw > bitmap.Width || sy + sh > bitmap.Height) throw new ScriptException("SPRITE_RECT", "精灵裁剪区域越界");
                DrawBitmap(id, destination, bitmap, dx, dy, dw, dh, sx, sy, sw, sh, request.Matrix);
                return Result(1, HasBackground(id));
            }
            throw new ScriptException("GRAPHICS_OPERATION", "未实现图形操作: " + operation);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; foreach (var state in drawingStates.Values) state.Dispose(); drawingStates.Clear(); foreach (var image in graphics.Values) image.Dispose(); foreach (var image in files.Values) image.Dispose(); graphics.Clear(); files.Clear(); backgrounds.Clear(); }
        finally { gate.Release(); }
    }
}
