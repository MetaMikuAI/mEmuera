using System.Collections.Immutable;
using mEmuera.Contracts;
using SkiaSharp;

namespace mEmuera.Infrastructure.Graphics;

public sealed partial class RasterService
{
    private sealed class DrawingState : IDisposable
    {
        public SKFont? Font;
        public string FontName = "";
        public int FontStyle;
        public SKColor? Pen;
        public float PenWidth = 1;
        public int DashStyle, DashCap;
        public SKMatrix Transform = SKMatrix.Identity;
        public List<SKPoint> Points = [];
        public void Dispose() { Font?.Typeface.Dispose(); Font?.Dispose(); }
    }
    private readonly Dictionary<long, DrawingState> drawingStates = [];
    private HashSet<string>? fontFamilies;
    private void DrawBitmap(long id, SkiaBitmap destination, SkiaBitmap source, int dx, int dy, int dw, int dh,
        int sx, int sy, int sw, int sh, ImmutableArray<long> matrix)
    {
        if (dw == 0 || dh == 0 || sw == 0 || sh == 0) return;
        using var canvas = new SKCanvas(destination.Bitmap);
        if (drawingStates.TryGetValue(id, out var state)) canvas.SetMatrix(state.Transform);
        canvas.ClipRect(new(Math.Min(dx, (float)dx + dw), Math.Min(dy, (float)dy + dh), Math.Max(dx, (float)dx + dw), Math.Max(dy, (float)dy + dh)));
        canvas.Translate(dx, dy); canvas.Scale((float)dw / sw, (float)dh / sh);
        float[]? values = null;
        if (!matrix.IsDefaultOrEmpty)
        {
            if (matrix.Length != 25) throw new ScriptException("GRAPHICS_MATRIX", "颜色矩阵需要 5×5 个数值");
            values = new float[20];
            for (int i = 0; i < 20; i++) values[i] = (float)matrix[i] / 256 * (i % 5 == 4 ? 255 : 1);
        }
        using var filter = values is null ? null : SKColorFilter.CreateColorMatrix(values);
        using var paint = new SKPaint { ColorFilter = filter };
        canvas.DrawBitmap(source.Bitmap, -sx, -sy, paint); canvas.Flush();
    }
    private static readonly HashSet<string> drawingOperations = ["GSETFONT", "GGETFONT", "GGETFONTSIZE", "GGETFONTSTYLE", "CHKFONT", "GGETTEXTSIZE",
        "GSETPEN", "GGETPEN", "GGETPENWIDTH", "GGETBRUSH", "GDASHSTYLE", "GDRAWLINE", "GDRAWTEXT", "GDRAWGWITHROTATE",
        "G_POLYGON_DRAW", "G_POLYGON_FILL", "G_POLYGON_POINT_ADD", "G_POLYGON_POINT_CLEAR", "GSAVE", "GLOAD"];
    private static SKFont CreateFont(string name, long size, int style)
    {
        if (size is <= 0 or > 4096) throw new ScriptException("GRAPHICS_FONT", "字体尺寸越界");
        var face = SKTypeface.FromFamilyName(name, (style & 1) != 0 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal, (style & 2) != 0 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        return new(face, size) { Edging = SKFontEdging.Antialias };
    }
    private static float Text(SKCanvas? canvas, string text, float x, float baseline, SKFont font, SKPaint? paint)
    {
        float start = x;
        foreach (var rune in text.EnumerateRunes())
        {
            using var face = font.ContainsGlyph(rune.Value) ? null : SKFontManager.Default.MatchCharacter(rune.Value);
            using var fallback = face is null ? null : new SKFont(face, font.Size) { Edging = font.Edging };
            var selected = fallback ?? font;
            string value = rune.ToString();
            if (canvas is not null) canvas.DrawText(value, x, baseline, selected, paint!);
            x += selected.MeasureText(value);
        }
        return x - start;
    }
    private async ValueTask<GraphicsData?> ExecuteDrawingAsync(GraphicsPayload request, CancellationToken ct)
    {
        string op = request.Operation;
        if (!drawingOperations.Contains(op)) return null;
        var a = request.Arguments;
        int Int(int i) => checked((int)a[i].AsInt());
        GraphicsData Result(long value) => new(new(value));
        if (op == "CHKFONT")
        {
            fontFamilies ??= new(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase);
            return Result(fontFamilies.Contains(a[0].AsString()) ? 1 : 0);
        }
        if (op == "GGETTEXTSIZE")
        {
            using var font = CreateFont(a[1].AsString(), a[2].AsInt(), Int(3));
            using var face = font.Typeface;
            return new(new EraValue((long)Text(null, a[0].AsString(), 0, 0, font, null))) { ResultTail = [(long)(font.Metrics.Descent - font.Metrics.Ascent + font.Metrics.Leading)] };
        }
        long id = a[0].AsInt();
        if (id is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
        graphics.TryGetValue(id, out var image);
        if (op is "GSAVE" or "GLOAD")
        {
            long slot = a[1].AsInt();
            if (slot is < 0 or > int.MaxValue || (op == "GLOAD") == (image is not null)) return Result(0);
            string path = paths.Data($"graphics/graphics{slot:D4}.png");
            try
            {
                if (op == "GSAVE")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var encoded = image!.Bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    await File.WriteAllBytesAsync(path, encoded.ToArray(), ct);
                }
                else
                {
                    if (!File.Exists(path)) return Result(0);
                    var info = await SkiaBitmap.IdentifyAsync(path, ct); Size(info.Width, info.Height);
                    if (allocated + (long)info.Width * info.Height > maxCanvasPixels) throw new ScriptException("GRAPHICS_LIMIT", "离屏图像总量超限");
                    var loaded = await SkiaBitmap.LoadAsync(path, ct);
                    graphics.Add(id, loaded); allocated += (long)loaded.Width * loaded.Height;
                }
                return Result(1);
            }
            catch (IOException) { return Result(0); }
        }
        if (image is null) return op == "GGETFONT" ? new(new EraValue("")) : Result(0);
        if (!drawingStates.TryGetValue(id, out var state)) drawingStates[id] = state = new();
        switch (op)
        {
            case "GSETFONT":
                var replacement = CreateFont(a[1].AsString(), a[2].AsInt(), Int(3));
                state.Dispose(); state.Font = replacement; state.FontName = replacement.Typeface.FamilyName; state.FontStyle = Int(3) & 15; return Result(1);
            case "GGETFONT": return new(new EraValue(state.FontName));
            case "GGETFONTSIZE": return Result((long)(state.Font?.Size ?? 0));
            case "GGETFONTSTYLE": return Result(state.FontStyle);
            case "GGETPEN": return Result(state.Pen is { } pen ? (uint)pen : 0);
            case "GGETPENWIDTH": return Result(state.Pen is null ? 0 : (long)state.PenWidth);
            case "GGETBRUSH": return Result((uint)brushes.GetValueOrDefault(id));
            case "GSETPEN":
                if (a[1].AsInt() is < 0 or > uint.MaxValue) throw new ScriptException("COLOR", "ARGB 颜色越界");
                if (a[2].AsInt() is < 0 or > 8192) throw new ScriptException("GRAPHICS_PEN", "画笔宽度越界");
                state.Pen = new SKColor((uint)a[1].AsInt()); state.PenWidth = a[2].AsInt(); return Result(1);
            case "GDASHSTYLE":
                if (Int(1) is < 0 or > 4 || Int(2) is not (0 or 2 or 3)) throw new ScriptException("GRAPHICS_PEN", "虚线样式或端点无效");
                state.Pen ??= new(request.Foreground); state.DashStyle = Int(1); state.DashCap = Int(2); return Result(1);
            case "G_POLYGON_POINT_ADD":
                if (state.Points.Count >= 100000) throw new ScriptException("GRAPHICS_LIMIT", "多边形顶点超限");
                state.Points.Add(new(Int(1), Int(2))); return Result(1);
            case "G_POLYGON_POINT_CLEAR": state.Points.Clear(); return Result(1);
        }
        using var canvas = new SKCanvas(image.Bitmap);
        canvas.SetMatrix(state.Transform);
        using var paint = new SKPaint { Color = state.Pen ?? new SKColor(request.Foreground), StrokeWidth = state.PenWidth,
            Style = SKPaintStyle.Stroke, StrokeCap = state.DashCap == 2 ? SKStrokeCap.Round : state.DashCap == 3 ? SKStrokeCap.Square : SKStrokeCap.Butt };
        float w = Math.Max(1, state.PenWidth);
        float[]? dash = state.DashStyle switch { 1 => [3 * w, w], 2 => [w, w], 3 => [3 * w, w, w, w], 4 => [3 * w, w, w, w, w, w], _ => null };
        using var effect = dash is null ? null : SKPathEffect.CreateDash(dash, 0);
        paint.PathEffect = effect;
        ImmutableArray<long> tail = default;
        switch (op)
        {
            case "GDRAWLINE": canvas.DrawLine(Int(1), Int(2), Int(3), Int(4), paint); break;
            case "G_POLYGON_DRAW":
            case "G_POLYGON_FILL":
                if (state.Points.Count < 2) throw new ScriptException("GRAPHICS_POLYGON", "多边形顶点不足");
                using (var path = new SKPath())
                {
                    path.MoveTo(state.Points[0]); foreach (var point in state.Points.Skip(1)) path.LineTo(point); path.Close();
                    if (op == "G_POLYGON_FILL") { paint.Style = SKPaintStyle.Fill; paint.Color = brushes.GetValueOrDefault(id, SKColors.Black); paint.PathEffect = null; }
                    canvas.DrawPath(path, paint);
                }
                break;
            case "GDRAWGWITHROTATE":
                long sourceId = a[1].AsInt();
                if (sourceId is < 0 or > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界");
                if (!graphics.TryGetValue(sourceId, out var original)) return Result(0);
                using (var copy = original.Clone())
                {
                    canvas.RotateDegrees(a[2].AsInt(), a[3].AsInt() == long.MinValue ? original.Width / 2 : Int(3), a[4].AsInt() == long.MinValue ? original.Height / 2 : Int(4));
                    state.Transform = canvas.TotalMatrix; canvas.DrawBitmap(copy.Bitmap, 0, 0);
                }
                break;
            case "GDRAWTEXT":
                using (var fallback = state.Font is null ? CreateFont(request.FontName, request.FontSize, 0) : null)
                using (var fallbackFace = fallback?.Typeface)
                {
                    var font = state.Font ?? fallback!; string text = a[1].AsString();
                    paint.PathEffect = null; paint.Style = SKPaintStyle.Fill; paint.Color = brushes.GetValueOrDefault(id, new SKColor(request.Foreground));
                    float baseline = Int(3) - font.Metrics.Ascent;
                    float width = Text(canvas, text, Int(2), baseline, font, paint);
                    if (state.Pen is { } outline) { paint.Style = SKPaintStyle.Stroke; paint.Color = outline; Text(canvas, text, Int(2), baseline, font, paint); }
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = Math.Max(1, font.Size / 16);
                    if ((state.FontStyle & 4) != 0) canvas.DrawLine(Int(2), baseline - font.Size / 3, Int(2) + width, baseline - font.Size / 3, paint);
                    if ((state.FontStyle & 8) != 0) canvas.DrawLine(Int(2), baseline + 1, Int(2) + width, baseline + 1, paint);
                    tail = [(long)width, (long)font.Size];
                }
                break;
        }
        canvas.Flush();
        return new(new EraValue(1L), HasBackground(id) ? Layers() : default) { ResultTail = tail };
    }
}
