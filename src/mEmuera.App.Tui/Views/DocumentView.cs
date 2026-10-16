using mEmuera.Cells;
using mEmuera.App.Tui.Layout;
using System.Collections.Immutable;
using mEmuera.Contracts;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace mEmuera.App.Tui.Views;

public sealed class DocumentView : View
{
    private SessionSnapshot? snapshot;
    private LayoutSnapshot? layout;
    private readonly ActionRouter router = new();
    private long revision;
    private readonly InputScrollState scrolling = new();
    private int layoutHeight;
    private readonly TooltipState tooltip = new();
    private bool tooltipVisible;
    private (int X, int Y)? pointerPosition;
    private PointerMessage? lastPointer;
    private readonly PointerQueryState pointerQuery;
    public bool KittyEnabled { get; set; }
    public bool KittyVisible => KittyEnabled && !tooltipVisible && snapshot is not null && layout is not null;
    private (long Layout, long? Selected)? kittyKey;
    private (byte[] Background, byte[] Foreground)? kittyFrame;
    public (byte[] Background, byte[] Foreground)? KittyFrame()
    {
        if (!KittyVisible || Viewport.Width <= 0 || Viewport.Height <= 0) return null;
        EnsureLayout();
        var key = (layout!.Revision, lastPointer?.ButtonId);
        if (kittyKey == key) return kittyFrame;
        kittyKey = key;
        return kittyFrame = (KittyGraphics.Compose(snapshot!.Document, layout, Viewport.Height, lastPointer?.ButtonId, false),
            KittyGraphics.Compose(snapshot.Document, layout, Viewport.Height, lastPointer?.ButtonId, true));
    }
    public event Action<InputRequest, long?, InputPointerInfo>? Clicked;
    public event Action<PointerMessage>? PointerChanged;
    public event Action<KeyStateMessage>? KeyStateChanged;
    public DocumentView(PointerQueryState? pointerQuery = null)
    {
        this.pointerQuery = pointerQuery ?? new();
        CanFocus = true;
        MouseLeave += (_, _) => { pointerPosition = null; PublishPointer(); tooltip.Clear(); TickTooltip(); };
        KeyDown += (_, key) =>
        {
            if (key == Key.PageUp) { Scroll(-Math.Max(1, Viewport.Height - 1)); key.Handled = true; }
            if (key == Key.PageDown) { Scroll(Math.Max(1, Viewport.Height - 1)); key.Handled = true; }
        };
    }
    public void Update(SessionSnapshot next)
    {
        if (snapshot is not null && snapshot.Epoch != next.Epoch) tooltip.Clear();
        int offset = scrolling.OffsetFromBottom; scrolling.ObserveInput(next.Input);
        if (offset != scrolling.OffsetFromBottom) layout = null;
        snapshot = next;
        if (pointerPosition is not null && Viewport.Width > 0 && Viewport.Height > 0) EnsureLayout();
        SetNeedsDraw();
    }
    public void TickTooltip()
    {
        bool visible = snapshot is not null && tooltip.Visible(Environment.TickCount64, snapshot.Document.Tooltip);
        if (visible == tooltipVisible) return;
        tooltipVisible = visible; SetNeedsDraw();
    }
    private void EnsureLayout()
    {
        if (snapshot is null) return;
        if (layout?.DocumentRevision != snapshot.Document.Revision || layout.Width != Viewport.Width || layoutHeight != Viewport.Height)
        {
            if (layout?.DocumentRevision != snapshot.Document.Revision) scrolling.DocumentChanged();
            layout = CellLayout.Visible(snapshot.Document, Viewport.Width, Viewport.Height, scrolling.OffsetFromBottom, ++revision);
            int offset = scrolling.OffsetFromBottom;
            scrolling.Clamp(layout.Rows - Viewport.Height);
            if (offset != scrolling.OffsetFromBottom) layout = CellLayout.Visible(snapshot.Document, Viewport.Width, Viewport.Height, scrolling.OffsetFromBottom, ++revision);
            layoutHeight = Viewport.Height;
        }
        router.Update(layout, snapshot.Input);
        pointerQuery.Observe(layout, snapshot.Epoch, Viewport.Height, scrolling.OffsetFromBottom);
        PublishPointer();
        tooltip.Rehit(layout, pointerPosition, Environment.TickCount64); TickTooltip();
    }
    private void PublishPointer()
    {
        pointerQuery.Move(pointerPosition);
        if (snapshot is null || layout is null) return;
        var message = ScriptPointer.Point(layout, snapshot.Epoch, snapshot.Document.Revision, pointerPosition, Viewport.Height);
        if (message == lastPointer) return;
        lastPointer = message; PointerChanged?.Invoke(message);
    }
    private void Scroll(int delta)
    {
        EnsureLayout();
        if (snapshot is null || layout is null) return;
        scrolling.Scroll(delta);
        layout = CellLayout.Visible(snapshot.Document, Viewport.Width, Viewport.Height, scrolling.OffsetFromBottom, ++revision);
        int offset = scrolling.OffsetFromBottom;
        scrolling.Clamp(layout.Rows - Viewport.Height);
        if (offset != scrolling.OffsetFromBottom)
            layout = CellLayout.Visible(snapshot.Document, Viewport.Width, Viewport.Height, scrolling.OffsetFromBottom, ++revision);
        router.Update(layout, snapshot.Input);
        pointerQuery.Observe(layout, snapshot.Epoch, Viewport.Height, scrolling.OffsetFromBottom);
        PublishPointer(); tooltip.Rehit(layout, pointerPosition, Environment.TickCount64); TickTooltip(); SetNeedsDraw();
    }
    protected override bool OnDrawingContent(DrawContext? context)
    {
        EnsureLayout(); if (layout is null) return true;
        int background = snapshot?.Document.BackgroundColor ?? 0x101820;
        bool fullGraphics = KittyEnabled && !tooltipVisible;
        var layers = fullGraphics ? [] : RasterComposition.Layers(snapshot!.Document, layout, Viewport.Height, lastPointer?.ButtonId).ToArray();
        int Pixel(int column, int row)
        {
            int result = background;
            foreach (var layer in layers.Where(l => l.Depth >= 0))
                if (RasterComposition.Pixel(layer, column, row) is { } pixel) result = RasterComposition.Blend(pixel, result);
            return result;
        }
        var drawnBackground = new int[Viewport.Width, Viewport.Height];
        for (int y = 0; y < Viewport.Height; y++)
            for (int x = 0; x < Viewport.Width; x++)
            {
                drawnBackground[x, y] = Pixel(x, y);
                SetAttribute(new Terminal.Gui.Drawing.Attribute(new Color(0xDDDDDD), new Color(drawnBackground[x, y])));
                Move(x, y); AddStr(" ");
            }
        foreach (var glyph in layout.Glyphs.Where(g => g.X >= 0 && g.X + g.Width <= Viewport.Width && g.Y >= 0 && g.Y < Viewport.Height))
        {
            int rgb = glyph.Color ?? (glyph.ButtonId is null ? 0xDDDDDD : 0x66DDFF);
            if (glyph.ButtonId is { } button && lastPointer?.ButtonId == button) rgb = snapshot!.Document.FocusColor;
            int glyphBackground = Pixel(glyph.X, glyph.Y);
            var pixel = glyph.ButtonId is { } imageButton && lastPointer?.ButtonId == imageButton ? glyph.SelectedImagePixel ?? glyph.ImagePixel : glyph.ImagePixel;
            if (!fullGraphics && pixel is { } imagePixel) glyphBackground = RasterComposition.Blend(imagePixel, glyphBackground);
            if (glyph.Background is { } textBackground) glyphBackground = RasterComposition.Blend(textBackground, glyphBackground);
            var style = TextStyle.None;
            if ((glyph.Style & 1) != 0) style |= TextStyle.Bold;
            if ((glyph.Style & 2) != 0) style |= TextStyle.Italic;
            if ((glyph.Style & 4) != 0) style |= TextStyle.Strikethrough;
            if ((glyph.Style & 8) != 0) style |= TextStyle.Underline;
            SetAttribute(new Terminal.Gui.Drawing.Attribute(new Color(rgb), new Color(glyphBackground), style));
            for (int x = Math.Max(0, glyph.X); x < Math.Min(Viewport.Width, glyph.X + glyph.Width); x++)
                drawnBackground[x, glyph.Y] = glyphBackground;
            Move(glyph.X, glyph.Y); AddStr(glyph.Text);
        }
        foreach (var layer in layers.Where(l => l.Depth < 0))
            for (int y = Math.Max(0, layer.Y / 16); y < Math.Min(Viewport.Height, (layer.Y + layer.Image.Height + 15) / 16); y++)
                for (int x = Math.Max(0, layer.X / 8); x < Math.Min(Viewport.Width, (layer.X + layer.Image.Width + 7) / 8); x++)
                {
                    if (RasterComposition.Pixel(layer, x, y) is not { } pixel) continue;
                    if ((pixel >> 24) == 0) continue;
                    drawnBackground[x, y] = RasterComposition.Blend(pixel, drawnBackground[x, y]);
                    SetAttribute(new Terminal.Gui.Drawing.Attribute(new Color(0xDDDDDD), new Color(drawnBackground[x, y])));
                    Move(x, y); AddStr(" ");
                }
        if (tooltip.Visible(Environment.TickCount64, snapshot!.Document.Tooltip) && Viewport.Width > 2 && Viewport.Height > 1)
        {
            var settings = snapshot.Document.Tooltip;
            int width = Math.Min(60, Viewport.Width);
            var help = CellLayout.Build(new(0, tooltip.Text!.Split('\n').Select(line => new LogicalLine([new StyledRun(line)])).ToImmutableList()), width, 0);
            int height = Math.Min(help.Rows, Viewport.Height), left = Math.Clamp(tooltip.X, 0, Viewport.Width - width);
            int top = Math.Clamp(tooltip.Y + 1, 0, Viewport.Height - height);
            SetAttribute(new Terminal.Gui.Drawing.Attribute(new Color(settings.Foreground), new Color(settings.Background)));
            for (int y = 0; y < height; y++) { Move(left, top + y); AddStr(new string(' ', width)); }
            foreach (var glyph in help.Glyphs.Where(g => g.Y < height && g.X + g.Width <= width))
            { Move(left + glyph.X, top + glyph.Y); AddStr(glyph.Text); }
        }
        return true;
    }
    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (snapshot is { } current)
            foreach (var (code, pressed, released, clicked) in new[]
            {
                (1, MouseFlags.LeftButtonPressed, MouseFlags.LeftButtonReleased, MouseFlags.LeftButtonClicked),
                (2, MouseFlags.RightButtonPressed, MouseFlags.RightButtonReleased, MouseFlags.RightButtonClicked),
                (4, MouseFlags.MiddleButtonPressed, MouseFlags.MiddleButtonReleased, MouseFlags.MiddleButtonClicked)
            })
            {
                if ((mouse.Flags & (pressed | clicked)) != 0) KeyStateChanged?.Invoke(new(current.Epoch, code, true));
                if ((mouse.Flags & (released | clicked)) != 0) KeyStateChanged?.Invoke(new(current.Epoch, code, false));
            }
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp)) { Scroll(-3); return true; }
        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown)) { Scroll(3); return true; }
        EnsureLayout();
        if (mouse.Position is { } hover && layout is not null)
        {
            pointerPosition = (hover.X, hover.Y); PublishPointer();
            tooltip.Rehit(layout, pointerPosition, Environment.TickCount64); TickTooltip(); SetNeedsDraw();
        }
        if (mouse.Position is { } point && snapshot?.Input is { } input && layout is not null
            && ScriptMouse.Click(mouse.Flags, input.MouseInput) is { } pointer)
        {
            if (router.TryMouseHit(layout.Revision, input.Epoch, input.Id, point.X, point.Y, out long? button))
                Clicked?.Invoke(input, button, pointer);
            return true;
        }
        return base.OnMouseEvent(mouse);
    }
}
