using System.Globalization;
using mEmuera.Contracts;
using mEmuera.EraCompat.Builtins;
using mEmuera.Runtime.Presentation;

namespace mEmuera.Cells;

public sealed record CellGlyph(int X, int Y, int Width, string Text, int? Color, long? ButtonId)
{
    public string? Tooltip { get; init; }
    public long? TooltipId { get; init; }
    public uint? Background { get; init; }
    public int Style { get; init; }
    public uint? ImagePixel { get; init; }
    public uint? SelectedImagePixel { get; init; }
    public bool Overlay { get; init; }
    public InlineRaster? Raster { get; init; }
    public int RasterColumn { get; init; }
    public int RasterRow { get; init; }
    public int RasterColumns { get; init; }
    public int RasterRows { get; init; }
}
public sealed record LayoutSnapshot(long Revision, long DocumentRevision, int Width, IReadOnlyList<CellGlyph> Glyphs, int Rows)
{
    public IReadOnlyList<ScriptImageLayer> ImageLayers { get; init; } = [];
    public RasterLayer? ButtonMap { get; init; }
    public int ViewportHeight { get; init; }
    public IReadOnlyDictionary<long, string> BackgroundTips { get; init; } = new Dictionary<long, string>();
    public long? Hit(int x, int y)
    {
        if (ButtonMap is { } map)
        {
            int px = x * 8 + 4, py = y * 16 + 8 - ViewportHeight * 16 + map.Height;
            if (px >= 0 && py >= 0 && px < map.Width && py < map.Height)
            {
                uint color = map.Pixels[py * map.Width + px];
                if (color >> 24 == 255) return -(long)(color & 0xffffff) - 1;
            }
        }
        return Glyphs.LastOrDefault(g => g.Y == y && x >= g.X && x < g.X + g.Width)?.ButtonId;
    }
}
public static class CellLayout
{
    private static readonly TerminalTextMetrics Metrics = new();
    private sealed class LineCache { public int Width; public LayoutSnapshot? Layout; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LogicalLine, LineCache> CachedLines = new();
    private static LayoutSnapshot LayoutLine(LogicalLine line, int width, long revision)
    {
        if (!line.CacheLayout) return Build(new(revision, [line]), width, revision);
        var cache = CachedLines.GetOrCreateValue(line);
        lock (cache)
        {
            if (cache.Layout is null || cache.Width != width)
            { cache.Width = width; cache.Layout = Build(new(revision, [line]), width, revision); }
            return cache.Layout;
        }
    }
    public static LayoutSnapshot Visible(PresentationDocument document, int width, int height, int offsetFromBottom, long revision)
    {
        height = Math.Max(1, height); width = Math.Max(1, width);
        var result = new List<CellGlyph>(); int consumed = 0;
        var anchors = new Dictionary<int, int>();
        int firstAnchor = document.ImageLayers.IsEmpty ? int.MaxValue : document.ImageLayers.Any(l => l.FollowScroll) ? 0 : document.ImageLayers.Where(l => !l.BottomAnchored).Select(l => l.AnchorLine).DefaultIfEmpty(int.MaxValue).Min();
        for (int i = document.Lines.Count - 1; i >= 0 && (consumed < offsetFromBottom + height || i >= firstAnchor); i--)
        {
            var line = LayoutLine(document.Lines[i], width, revision);
            int bottom = height + offsetFromBottom - consumed;
            anchors[i] = bottom - line.Rows;
            foreach (var glyph in line.Glyphs)
            {
                int y = bottom - line.Rows + glyph.Y;
                if (y >= 0 && y < height) result.Add(glyph with { Y = y });
            }
            consumed += line.Rows;
        }
        // 短文档顶端对齐；长文档只保留可见字形。
        if (consumed < height && offsetFromBottom == 0)
            result = result.Select(g => g with { Y = g.Y - (height - consumed) }).ToList();
        int shift = consumed < height && offsetFromBottom == 0 ? height - consumed : 0;
        return new(revision, document.Revision, width, result.OrderBy(g => g.Overlay).ToArray(), consumed)
        { ButtonMap = document.BackgroundLayers.FirstOrDefault(l => l.ButtonMap), ViewportHeight = height,
            BackgroundTips = document.BackgroundLayers.Where(l => l.ButtonValue is not null && l.Tooltip is not null)
                .GroupBy(l => -(long)l.ButtonValue!.Value - 1).ToDictionary(g => g.Key, g => g.First().Tooltip!),
            ImageLayers = document.ImageLayers.Where(l => l.BottomAnchored || anchors.ContainsKey(l.AnchorLine)).Select(l => l with
            { Y = l.BottomAnchored ? l.Y + height * 16 - l.Image.Height - (l.FollowScroll ? (Math.Max(0, consumed - height - offsetFromBottom) - l.InitialScrollRows) * 16 : 0)
                : l.Y + (anchors[l.AnchorLine] - shift) * 16 }).OrderByDescending(l => l.Depth).ToArray() };
    }
    public static LayoutSnapshot Build(PresentationDocument document, int width, long revision)
    {
        width = Math.Max(1, width); var glyphs = new List<CellGlyph>(); int x = 0, y = 0;
        var anchors = new List<int>();
        foreach (var source in document.Lines)
        {
            anchors.Add(y);
            foreach (var physical in PhysicalLineLayout.Wrap(source, width, Metrics))
                Render(physical.Line);
        }
        void Render(LogicalLine line)
        {
            int firstGlyph = glyphs.Count;
            int rowHeight = 1;
            long pixelX = 0;
            int Column(long pixels) => checked((int)(pixels < 0 ? Math.Floor((double)pixels / Metrics.CellWidth) : Math.Ceiling((double)pixels / Metrics.CellWidth)));
            foreach (var run in line.Runs)
            {
                x = Column(pixelX);
                if (run.Panel is { } panel)
                {
                    int cw = Metrics.CellWidth, ch = cw * 2;
                    int px = x + (int)Math.Floor((double)panel.X / cw), py = y + (int)Math.Floor((double)panel.Y / ch);
                    int columns = Math.Max(1, (panel.Width + cw - 1) / cw);
                    int padX = (panel.Padding + panel.Border + cw - 1) / cw, padY = (panel.Padding + panel.Border + ch - 1) / ch;
                    var content = Build(new(document.Revision, panel.Lines), Math.Max(1, columns - padX * 2), revision);
                    int rows = panel.Height == 0 ? content.Rows + padY * 2 : Math.Max(1, (panel.Height + ch - 1) / ch);
                    for (int iy = 0; iy < rows; iy++)
                        for (int ix = 0; ix < columns; ix++)
                        {
                            bool border = panel.Border > 0 && (ix == 0 || iy == 0 || ix == columns - 1 || iy == rows - 1);
                            string text = " ";
                            if (border)
                            {
                                bool top = iy == 0, bottom = iy == rows - 1, left = ix == 0, right = ix == columns - 1;
                                text = top && left ? panel.Radius > 0 ? "╭" : "┌" : top && right ? panel.Radius > 0 ? "╮" : "┐"
                                    : bottom && left ? panel.Radius > 0 ? "╰" : "└" : bottom && right ? panel.Radius > 0 ? "╯" : "┘" : top || bottom ? "─" : "│";
                            }
                            if (border || panel.Color is not null)
                                glyphs.Add(new(px + ix, py + iy, 1, text, panel.BorderColor ?? run.Color, null)
                                { Background = panel.Color is { } color ? 0xff000000u | (uint)color : null, Overlay = true });
                        }
                    foreach (var glyph in content.Glyphs)
                    {
                        int gx = glyph.X + padX, gy = glyph.Y + padY;
                        if (gx < 0 || gy < 0 || gx + glyph.Width > columns || gy >= rows) continue;
                        glyphs.Add(glyph with { X = px + gx, Y = py + gy, Overlay = true,
                            Background = glyph.Background ?? (panel.Color is { } color ? 0xff000000u | (uint)color : null) });
                    }
                    continue;
                }
                if (run.Image is { } raster)
                {
                    pixelX += raster.Width;
                    if (raster.Width <= 0) continue;
                    int columns = Math.Max(0, Column(pixelX) - x);
                    if (raster.Height <= 0) continue;
                    int rows = Math.Max(1, (raster.Height + Metrics.CellWidth * 2 - 1) / (Metrics.CellWidth * 2));
                    int offsetY = (int)Math.Floor((double)raster.Y / (Metrics.CellWidth * 2));
                    for (int iy = 0; iy < rows; iy++)
                        for (int ix = 0; ix < Math.Min(columns, width - x); ix++)
                        {
                            int sx = Math.Min(raster.Image.Width - 1, (int)((ix + .5) * raster.Image.Width / columns));
                            int sy = Math.Min(raster.Image.Height - 1, (int)((iy + .5) * raster.Image.Height / rows));
                            if (raster.FlipX) sx = raster.Image.Width - 1 - sx;
                            if (raster.FlipY) sy = raster.Image.Height - 1 - sy;
                            glyphs.Add(new(x + ix, y + iy + offsetY, 1, " ", run.Color, run.ButtonId)
                            { ImagePixel = raster.Image.Pixels[sy * raster.Image.Width + sx], Tooltip = run.Tooltip, TooltipId = run.TooltipId,
                                Raster = raster, RasterColumn = ix, RasterRow = iy, RasterColumns = columns, RasterRows = rows,
                                SelectedImagePixel = SelectedPixel(raster, ix, iy, columns, rows) });
                        }
                    rowHeight = Math.Max(rowHeight, rows + offsetY); continue;
                }
                var elements = StringInfo.GetTextElementEnumerator(run.Text);
                while (elements.MoveNext())
                {
                    string element = elements.GetTextElement();
                    int cells = Metrics.Columns(element);
                    cells = Math.Max(1, cells);
                    x = Column(pixelX);
                    if (!line.NoWrap || x + cells <= width) glyphs.Add(new(x, y, cells, element, run.Color, run.ButtonId) { Background = run.Background, Style = run.Style, Tooltip = run.Tooltip, TooltipId = run.TooltipId });
                    pixelX += (long)cells * Metrics.CellWidth;
                }
            }
            if (line.Alignment != TextAlignment.Left)
            {
                foreach (var row in glyphs.Skip(firstGlyph).Select((glyph, i) => (Glyph: glyph, Index: firstGlyph + i)).Where(p => !p.Glyph.Overlay).GroupBy(p => p.Glyph.Y).ToArray())
                {
                    int used = row.Max(p => p.Glyph.X + p.Glyph.Width);
                    int offset = line.Alignment == TextAlignment.Center ? Math.Max(0, (width - used) / 2) : Math.Max(0, width - used);
                    foreach (var item in row) glyphs[item.Index] = item.Glyph with { X = item.Glyph.X + offset };
                }
            }
            x = 0; y += rowHeight;
        }
        return new(revision, document.Revision, width, glyphs, y)
        { ImageLayers = document.ImageLayers.Where(l => l.AnchorLine >= 0 && l.AnchorLine < anchors.Count).Select(l => l with { Y = l.Y + anchors[l.AnchorLine] * 16 }).OrderByDescending(l => l.Depth).ToArray() };
    }
    private static uint? SelectedPixel(InlineRaster raster, int x, int y, int columns, int rows)
    {
        if (raster.SelectedImage is not { } selected) return null;
        int sx = Math.Min(selected.Width - 1, (int)((x + .5) * selected.Width / columns));
        int sy = Math.Min(selected.Height - 1, (int)((y + .5) * selected.Height / rows));
        if (raster.FlipX) sx = selected.Width - 1 - sx;
        if (raster.FlipY) sy = selected.Height - 1 - sy;
        return selected.Pixels[sy * selected.Width + sx];
    }
}
public sealed class ActionRouter
{
    public LayoutSnapshot? Layout { get; private set; }
    public InputRequest? Input { get; private set; }
    public void Update(LayoutSnapshot layout, InputRequest? input) { Layout = layout; Input = input; }
    public long? Hit(long layoutRevision, long epoch, long request, int x, int y)
    {
        if (Layout?.Revision != layoutRevision || Input?.Epoch != epoch || Input.Id != request) return null;
        long? id = Layout.Hit(x, y);
        return id is { } button && Input.Buttons.ContainsKey(button) ? id : null;
    }
    public bool TryMouseHit(long layoutRevision, long epoch, long request, int x, int y, out long? button)
    {
        button = null;
        if (Layout?.Revision != layoutRevision || Input?.Epoch != epoch || Input.Id != request || Input.IgnoreInput) return false;
        button = Layout.Hit(x, y);
        return button is { } id ? Input.Buttons.ContainsKey(id) : Input.MouseInput;
    }
}
