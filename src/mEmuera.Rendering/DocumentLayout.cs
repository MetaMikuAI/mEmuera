using System.Collections.Immutable;
using System.Globalization;
using mEmuera.Contracts;
using mEmuera.Runtime.Presentation;

namespace mEmuera.Rendering;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
    public bool Intersects(int top, int bottom) => Y < bottom && Bottom > top;
}
/// <param name="X">相对所在项左边缘的像素。</param>
public readonly record struct TextCluster(string Text, int X);
public abstract record LayoutItem(PixelRect Bounds, StyledRun Run);
public sealed record TextItem(PixelRect Bounds, StyledRun Run, ImmutableArray<TextCluster> Clusters) : LayoutItem(Bounds, Run);
public sealed record ImageItem(PixelRect Bounds, StyledRun Run, InlineRaster Raster) : LayoutItem(Bounds, Run);
public sealed record PanelItem(PixelRect Bounds, StyledRun Run, InlinePanel Panel, DocumentLayout Content) : LayoutItem(Bounds, Run);
public sealed record RowLayout(int Top, ImmutableArray<LayoutItem> Items);

public sealed class LayoutOptions
{
    /// <summary>true：行高随内嵌图片增高（TUI 的做法）；false：每行固定为 LineHeight，图片向下盖住后面的行。</summary>
    public bool ImageGrowsLine { get; init; }
}

/// <summary>一份文档在给定窗口宽度下的像素布局。坐标都是文档坐标（左上角为原点，向下增长）。</summary>
public sealed class DocumentLayout
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ImmutableArray<RowLayout> Rows { get; init; }
    /// <summary>每个逻辑行第一行的顶部；图片层的锚定行用它换算。</summary>
    public required ImmutableArray<int> LineTops { get; init; }

    public static DocumentLayout Build(IReadOnlyList<LogicalLine> lines, int width, DisplayMetrics metrics, ITextMetrics text, LayoutOptions? options = null)
        => Build(lines, width, metrics, text, options ?? new(), 0, 0);

    private static DocumentLayout Build(IReadOnlyList<LogicalLine> lines, int width, DisplayMetrics metrics, ITextMetrics text, LayoutOptions options, int originX, int originY)
    {
        int cell = metrics.CellWidth, lineHeight = metrics.LineHeight;
        int columns = Math.Max(1, width / cell);
        var rows = ImmutableArray.CreateBuilder<RowLayout>();
        var tops = ImmutableArray.CreateBuilder<int>(lines.Count);
        int y = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            var source = lines[index];
            tops.Add(y);
            // 文档末尾那个没有任何内容的行是还没提交的打印缓冲，原版只保存已提交的行，它不占行。
            // 显式的空行（PRINTL）带一个空格 run，不受影响。
            if (index == lines.Count - 1 && source.Runs.IsEmpty) break;
            foreach (var physical in PhysicalLineLayout.Wrap(source, columns, text))
            {
                var items = ImmutableArray.CreateBuilder<LayoutItem>();
                long x = 0; int rowHeight = lineHeight;
                foreach (var run in physical.Line.Runs)
                {
                    int left = originX + (int)x;
                    if (run.Panel is { } panel)
                    {
                        items.Add(PlacePanel(run, panel, left + panel.X, originY + y + panel.Y, metrics, text, options));
                        continue;
                    }
                    if (run.Image is { } raster)
                    {
                        int top = originY + y + raster.Y;
                        if (raster.Width > 0 && raster.Height > 0) items.Add(new ImageItem(new(left, top, raster.Width, raster.Height), run, raster));
                        if (options.ImageGrowsLine) rowHeight = Math.Max(rowHeight, raster.Y + raster.Height);
                        x += raster.Width; continue;
                    }
                    var clusters = ImmutableArray.CreateBuilder<TextCluster>();
                    int offset = 0;
                    var elements = StringInfo.GetTextElementEnumerator(run.Text);
                    while (elements.MoveNext())
                    {
                        string element = elements.GetTextElement();
                        int span = Math.Max(1, text.Columns(element)) * cell;
                        if (!physical.Line.NoWrap || x + offset + span <= width) clusters.Add(new(element, offset));
                        offset += span;
                    }
                    if (clusters.Count > 0) items.Add(new TextItem(new(left, originY + y, offset, lineHeight), run, clusters.ToImmutable()));
                    x += offset;
                }
                Align(items, physical.Line.Alignment, width, originX);
                rows.Add(new(originY + y, items.ToImmutable()));
                y += rowHeight;
            }
        }
        return new() { Width = width, Height = y, Rows = rows.ToImmutable(), LineTops = tops.ToImmutable() };
    }

    private static void Align(ImmutableArray<LayoutItem>.Builder items, TextAlignment alignment, int width, int originX)
    {
        if (alignment == TextAlignment.Left) return;
        // 面板是绝对定位的，不参与对齐。
        int right = originX; bool any = false;
        foreach (var item in items) if (item is not PanelItem) { right = Math.Max(right, item.Bounds.Right); any = true; }
        if (!any) return;
        int used = right - originX;
        int shift = alignment == TextAlignment.Center ? Math.Max(0, (width - used) / 2) : Math.Max(0, width - used);
        for (int i = 0; i < items.Count; i++)
            items[i] = items[i] switch
            {
                TextItem t => t with { Bounds = t.Bounds with { X = t.Bounds.X + shift } },
                ImageItem m => m with { Bounds = m.Bounds with { X = m.Bounds.X + shift } },
                var other => other,
            };
    }

    private static PanelItem PlacePanel(StyledRun run, InlinePanel panel, int x, int y, DisplayMetrics metrics, ITextMetrics text, LayoutOptions options)
    {
        int inset = panel.Padding + panel.Border;
        int innerWidth = Math.Max(metrics.CellWidth, panel.Width - inset * 2);
        var content = Build(panel.Lines, innerWidth, metrics, text, options, x + inset, y + inset);
        int height = panel.Height == 0 ? content.Height + inset * 2 : panel.Height;
        return new(new(x, y, panel.Width, height), run, panel, content);
    }

    /// <summary>命中点下的按钮编号；多个重叠时取最后绘制的。</summary>
    public long? Hit(int x, int y)
    {
        long? found = null;
        foreach (var row in Rows)
            foreach (var item in row.Items)
            {
                if (item is PanelItem panel)
                {
                    if (!panel.Bounds.Contains(x, y)) continue;
                    found = panel.Content.Hit(x, y) ?? found;
                }
                else if (item.Run.ButtonId is { } id && item.Bounds.Contains(x, y)) found = id;
            }
        return found;
    }
}
