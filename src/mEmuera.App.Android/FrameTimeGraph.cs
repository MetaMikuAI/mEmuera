using Android.Content;
using Android.Graphics;
using Android.Views;

namespace mEmuera.App.Android;

// 只记录正文绘制；由性能面板定时刷新，避免绘制回调触发下一帧。
internal sealed class FrameTimeGraph : View
{
    private readonly double[] samples = new double[120];
    private readonly Paint paint = new(PaintFlags.AntiAlias);
    private readonly float inset, labelHeight;
    private int next, count;

    public FrameTimeGraph(Context context) : base(context)
    {
        inset = Ui.Dp(context, 6);
        labelHeight = Ui.Dp(context, 17);
        paint.TextSize = Ui.Sp(context, 10);
        SetMinimumWidth(Ui.Dp(context, 240));
    }

    public bool Recording { get; set; }

    public void AddSample(double milliseconds)
    {
        if (!Recording) return;
        samples[next] = milliseconds;
        next = (next + 1) % samples.Length;
        count = Math.Min(count + 1, samples.Length);
    }

    public void Clear() { next = count = 0; Invalidate(); }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        double peak = 0;
        for (int i = 0; i < count; i++) peak = Math.Max(peak, samples[i]);
        double scale = Math.Max(50, peak);
        float bottom = Height - inset, height = bottom - labelHeight;
        float right = Width - inset - paint.MeasureText("33.3 ms"), width = right - inset;
        if (width <= 0 || height <= 0) return;
        float step = width / samples.Length;
        for (int i = 0; i < count; i++)
        {
            double value = samples[(next - count + i + samples.Length) % samples.Length];
            paint.Color = value > 33.3 ? Ui.Error : value > 16.7 ? Color.Rgb(249, 226, 175) : Ui.Accent;
            float x = inset + (samples.Length - count + i) * step;
            canvas.DrawRect(x, bottom - Math.Max(1, (float)(value / scale * height)), x + Math.Max(1, step - 1), bottom, paint);
        }
        paint.Color = Ui.Muted;
        Guide(16.7, "16.7");
        Guide(33.3, "33.3");
        paint.Color = Ui.Text;
        canvas.DrawText(count == 0 ? "正文绘制 ms/帧 · 等待画面" : $"正文绘制 ms/帧 · 最近 {count} 帧 · 峰值 {peak:F1}", inset, labelHeight - 3, paint);

        void Guide(double value, string label)
        {
            float y = bottom - (float)(value / scale * height);
            canvas.DrawLine(inset, y, right, y, paint);
            canvas.DrawText(label, right + 3, y, paint);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) paint.Dispose();
        base.Dispose(disposing);
    }
}
