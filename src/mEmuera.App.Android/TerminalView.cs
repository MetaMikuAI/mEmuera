using Android.Content;
using Android.Views;
using mEmuera.Cells;
using SkiaSharp;
using SkiaSharp.Views.Android;

namespace mEmuera.App.Android;

/// <summary>
/// 游戏正文：用 <see cref="CellPainter"/> 画 <see cref="CellScreen"/> 的当前帧。
/// 手指按下等同鼠标悬停，原地松开等同单击；上下拖动按行滚动历史；双指缩放改字号，格子数随之变化。
/// </summary>
internal sealed class TerminalView : SKCanvasView
{
    private readonly CellScreen screen;
    private readonly CellPainter painter;
    private readonly ScaleGestureDetector scaler;
    private readonly int touchSlop;
    private float downX, downY, scrollY;
    private bool dragging, pressed;

    public TerminalView(Context context, CellScreen screen, CellPainter painter) : base(context)
    {
        this.screen = screen; this.painter = painter;
        touchSlop = ViewConfiguration.Get(context)!.ScaledTouchSlop;
        scaler = new ScaleGestureDetector(context, new Pinch(this));
    }

    /// <summary>格子数变化（尺寸或字号改变）。</summary>
    public event Action<int, int>? GridChanged;
    /// <summary>双指缩放结束后的字号（像素）。</summary>
    public event Action<float>? FontSizeChanged;

    private static long Now => System.Environment.TickCount64;

    public void Redraw() => Invalidate();

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        UpdateGrid();
    }

    private void UpdateGrid()
    {
        if (Width <= 0 || Height <= 0) return;
        var (columns, rows) = painter.Grid(Width, Height);
        if (screen.Resize(columns, rows)) GridChanged?.Invoke(columns, rows);
        Invalidate();
    }

    /// <summary>上一帧和取样以来最慢一帧的绘制耗时（毫秒，含需要时的重新排版）。</summary>
    public double LastPaintMs { get; private set; }
    public double TakeSlowestPaintMs() { double slowest = slowestPaintMs; slowestPaintMs = 0; return slowest; }
    private double slowestPaintMs;

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (screen.Frame(Now) is { } frame) painter.Paint(e.Surface.Canvas, frame);
        else e.Surface.Canvas.Clear(new SKColor(0xff000000u | (uint)painter.Theme.Background));
        LastPaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        slowestPaintMs = Math.Max(slowestPaintMs, LastPaintMs);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        scaler.OnTouchEvent(e);
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                downX = e.GetX(); downY = scrollY = e.GetY(); dragging = false; pressed = true;
                screen.Press((int)(downX / painter.CellWidth), (int)(downY / painter.CellHeight), Now);
                Tick();
                break;
            case MotionEventActions.PointerDown:
                StartDrag();
                break;
            case MotionEventActions.Move:
                if (scaler.IsInProgress || e.PointerCount > 1) { StartDrag(); break; }
                float y = e.GetY();
                if (!dragging && (Math.Abs(e.GetX() - downX) > touchSlop || Math.Abs(y - downY) > touchSlop)) { StartDrag(); scrollY = y; }
                if (dragging)
                {
                    // 手指往下拖看更早的内容。
                    int rows = (int)((y - scrollY) / painter.CellHeight);
                    if (rows != 0) { screen.Scroll(-rows); scrollY += rows * painter.CellHeight; Invalidate(); }
                }
                break;
            case MotionEventActions.Up:
                if (pressed && !dragging) screen.Release(Now);
                pressed = false; Invalidate();
                PerformClick();
                break;
            case MotionEventActions.Cancel:
                StartDrag(); pressed = false;
                break;
        }
        return true;
    }

    public override bool PerformClick() => base.PerformClick();

    private void StartDrag()
    {
        if (dragging) return;
        dragging = true;
        screen.Cancel(); Invalidate();
    }

    // 手指按住时提示框在计时，到点要重画一次才会出现。
    private void Tick()
    {
        Invalidate();
        if (pressed && screen.TooltipPending(Now)) PostDelayed(Tick, 50);
    }

    private void Zoom(float factor)
    {
        float size = Math.Clamp(painter.FontSize * factor, 6, 96);
        int cell = painter.CellWidth;
        painter.FontSize = size;
        if (painter.CellWidth != cell) UpdateGrid(); else Invalidate();
    }

    private sealed class Pinch(TerminalView view) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScale(ScaleGestureDetector detector) { view.Zoom(detector.ScaleFactor); return true; }
        public override void OnScaleEnd(ScaleGestureDetector detector) => view.FontSizeChanged?.Invoke(view.painter.FontSize);
    }
}
