using mEmuera.Contracts;

namespace mEmuera.Cells;

/// <summary>
/// 触屏字符格前端的状态：最新快照、可见排版、滚动、手指位置与提示框，规则与 TUI 的 DocumentView 相同。
/// 平台层只把像素换算成格子坐标后调用 Press/Release/Cancel/Scroll，按 <see cref="Frame"/> 重画。
/// 手指按住时等同鼠标悬停（按钮高亮、提示框计时），松开时等同单击；在 WAIT 类输入上点空白处继续。
/// </summary>
public sealed class CellScreen(PointerQueryState? pointerQuery = null)
{
    private readonly PointerQueryState pointerQuery = pointerQuery ?? new();
    private readonly ActionRouter router = new();
    private readonly InputScrollState scrolling = new();
    private readonly TooltipState tooltip = new();
    private SessionSnapshot? snapshot;
    private LayoutSnapshot? layout;
    private long revision;
    private (int X, int Y)? pointer;
    private PointerMessage? lastPointer;

    public int Columns { get; private set; } = 1;
    public int Rows { get; private set; } = 1;
    public SessionSnapshot? Snapshot => snapshot;
    public int OffsetFromBottom => scrolling.OffsetFromBottom;
    public event Action<InputRequest, long?, InputPointerInfo>? Clicked;
    /// <summary>在 WAIT 或任意键输入上点了空白处。</summary>
    public event Action<InputRequest>? Advanced;
    public event Action<PointerMessage>? PointerChanged;
    public event Action<KeyStateMessage>? KeyStateChanged;

    /// <summary>格子数变化时返回 true，调用方据此向引擎发送 ViewportMessage。</summary>
    public bool Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns); rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows) return false;
        Columns = columns; Rows = rows; layout = null;
        return true;
    }

    public void Update(SessionSnapshot next)
    {
        if (snapshot is not null && snapshot.Epoch != next.Epoch) tooltip.Clear();
        int offset = scrolling.OffsetFromBottom; scrolling.ObserveInput(next.Input);
        if (offset != scrolling.OffsetFromBottom) layout = null;
        snapshot = next;
        EnsureLayout();
    }

    /// <summary>正数向新内容（下方）滚动，负数看历史。</summary>
    public void Scroll(int delta)
    {
        EnsureLayout();
        if (snapshot is null || layout is null || delta == 0) return;
        scrolling.Scroll(delta);
        layout = CellLayout.Visible(snapshot.Document, Columns, Rows, scrolling.OffsetFromBottom, ++revision);
        int offset = scrolling.OffsetFromBottom;
        scrolling.Clamp(layout.Rows - Rows);
        if (offset != scrolling.OffsetFromBottom)
            layout = CellLayout.Visible(snapshot.Document, Columns, Rows, scrolling.OffsetFromBottom, ++revision);
        Observe();
    }

    public void Press(int x, int y, long now)
    {
        pointer = (x, y);
        if (snapshot is { } current) KeyStateChanged?.Invoke(new(current.Epoch, 1, true));
        EnsureLayout();
        if (layout is not null) tooltip.Rehit(layout, pointer, now);
    }

    /// <summary>手指开始拖动或被系统收回：取消按住状态，不触发点击。</summary>
    public void Cancel()
    {
        if (pointer is null) return;
        if (snapshot is { } current) KeyStateChanged?.Invoke(new(current.Epoch, 1, false));
        Leave();
    }

    public void Release(long now)
    {
        if (pointer is not { } point) return;
        EnsureLayout();
        // 长按看过提示框后松手不算点击。
        bool sawTooltip = snapshot is not null && tooltip.Visible(now, snapshot.Document.Tooltip);
        if (snapshot is { } current) KeyStateChanged?.Invoke(new(current.Epoch, 1, false));
        if (!sawTooltip && snapshot?.Input is { } input && layout is not null)
        {
            if (router.TryMouseHit(layout.Revision, input.Epoch, input.Id, point.X, point.Y, out long? button))
                Clicked?.Invoke(input, button, new(InputMouseButton.Left));
            else if (!input.IgnoreInput && (input.Kind == InputKind.Wait || input.AnyKey))
                Advanced?.Invoke(input);
        }
        Leave();
    }

    public CellFrame? Frame(long now)
    {
        EnsureLayout();
        if (snapshot is null || layout is null) return null;
        bool showTip = tooltip.Visible(now, snapshot.Document.Tooltip);
        return new(snapshot.Document, layout, Rows)
        { Selected = lastPointer?.ButtonId, Tooltip = showTip ? (tooltip.Text!, tooltip.X, tooltip.Y) : null };
    }

    /// <summary>提示框正在计时（手指按住且目标有提示），平台层应稍后再画一次。</summary>
    public bool TooltipPending(long now) => pointer is not null && tooltip.Text is { Length: > 0 } && snapshot is not null
        && !tooltip.Visible(now, snapshot.Document.Tooltip);

    private void Leave()
    {
        pointer = null; tooltip.Clear();
        PublishPointer();
    }

    private void EnsureLayout()
    {
        if (snapshot is null) return;
        if (layout?.DocumentRevision != snapshot.Document.Revision || layout.Width != Columns || layout.ViewportHeight != Rows)
        {
            if (layout is not null && layout.DocumentRevision != snapshot.Document.Revision) scrolling.DocumentChanged();
            layout = CellLayout.Visible(snapshot.Document, Columns, Rows, scrolling.OffsetFromBottom, ++revision);
            int offset = scrolling.OffsetFromBottom;
            scrolling.Clamp(layout.Rows - Rows);
            if (offset != scrolling.OffsetFromBottom) layout = CellLayout.Visible(snapshot.Document, Columns, Rows, scrolling.OffsetFromBottom, ++revision);
        }
        Observe();
    }

    private void Observe()
    {
        router.Update(layout!, snapshot!.Input);
        pointerQuery.Observe(layout!, snapshot.Epoch, Rows, scrolling.OffsetFromBottom);
        PublishPointer();
    }

    private void PublishPointer()
    {
        pointerQuery.Move(pointer);
        if (snapshot is null || layout is null) return;
        var message = ScriptPointer.Point(layout, snapshot.Epoch, snapshot.Document.Revision, pointer, Rows);
        if (message == lastPointer) return;
        lastPointer = message; PointerChanged?.Invoke(message);
    }
}
