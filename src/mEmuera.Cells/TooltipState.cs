using mEmuera.Contracts;

namespace mEmuera.Cells;

public sealed class TooltipState
{
    public string? Text { get; private set; }
    public int X { get; private set; }
    public int Y { get; private set; }
    private long started;
    private long? targetId;
    public void Hover(string? text, int x, int y, long now, long? target = null)
    {
        text = text?.Replace("<br>", "\n", StringComparison.Ordinal);
        if (Text == text && targetId == target) return;
        Text = text; targetId = target; X = x; Y = y; started = now;
    }
    public void Clear() { Text = null; targetId = null; }
    public void Rehit(LayoutSnapshot layout, (int X, int Y)? position, long now)
    {
        if (position is not { } point) { Clear(); return; }
        if (layout.Hit(point.X, point.Y) is < 0 and var button && layout.BackgroundTips.TryGetValue(button, out var tip))
        { Hover(tip, point.X, point.Y, now, button); return; }
        var glyph = layout.Glyphs.LastOrDefault(g => g.Y == point.Y && point.X >= g.X && point.X < g.X + g.Width);
        Hover(glyph?.Tooltip, point.X, point.Y, now, glyph?.TooltipId ?? glyph?.ButtonId);
    }
    public bool Visible(long now, TooltipSettings settings) => !string.IsNullOrEmpty(Text)
        && now - started >= settings.Delay && (settings.Duration == 0 || now - started - settings.Delay < settings.Duration);
}
