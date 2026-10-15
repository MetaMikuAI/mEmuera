using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace mEmuera.App.Android;

/// <summary>界面外框的样式，取自 chuchu 的默认主题（Catppuccin Mocha）：直角、1dp 边框、等宽字体、无阴影。</summary>
internal static class Ui
{
    public static readonly Color Background = Color.ParseColor("#1E1E2E");
    public static readonly Color Surface = Color.ParseColor("#313244");
    public static readonly Color SurfaceVariant = Color.ParseColor("#181825");
    public static readonly Color Border = Color.ParseColor("#45475A");
    public static readonly Color Text = Color.ParseColor("#CDD6F4");
    public static readonly Color Muted = Color.ParseColor("#6C7086");
    public static readonly Color Accent = Color.ParseColor("#B4BEFE");
    public static readonly Color OnAccent = Color.ParseColor("#1E1E2E");
    public static readonly Color Error = Color.ParseColor("#F38BA8");

    public static Color Translucent(Color color, int alpha) => Color.Argb(alpha, color.R, color.G, color.B);

    public static int Dp(Context context, float value) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, value, context.Resources!.DisplayMetrics);
    public static float Sp(Context context, float value) => TypedValue.ApplyDimension(ComplexUnitType.Sp, value, context.Resources!.DisplayMetrics);

    public static TextView Label(Context context, string text, float sp = 14, Color? color = null)
    {
        var view = new TextView(context) { Text = text, Typeface = Typeface.Monospace };
        view.SetTextSize(ComplexUnitType.Sp, sp);
        view.SetTextColor(color ?? Text);
        return view;
    }

    public static Drawable Box(Context context, Color fill, Color border)
    {
        var shape = new GradientDrawable();
        shape.SetColor(fill);
        shape.SetStroke(Math.Max(1, Dp(context, 1)), border);
        return shape;
    }

    /// <summary>filled 用强调色填充（主操作），否则是带边框的透明按钮。</summary>
    public static Button Button(Context context, string text, bool filled = false)
    {
        var button = new Button(context) { Text = text, Typeface = Typeface.Monospace, StateListAnimator = null };
        button.SetAllCaps(false);
        button.SetTextSize(ComplexUnitType.Sp, 14);
        button.SetTextColor(filled ? OnAccent : Text);
        button.SetMinHeight(Dp(context, 36)); button.SetMinimumHeight(Dp(context, 36));
        button.SetMinWidth(0); button.SetMinimumWidth(0);
        button.SetPadding(Dp(context, 12), Dp(context, 6), Dp(context, 12), Dp(context, 6));
        button.Background = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(60, 255, 255, 255)),
            Box(context, filled ? Accent : SurfaceVariant, filled ? Accent : Border), null);
        return button;
    }

    public static EditText Field(Context context, string text = "", string hint = "")
    {
        var field = new EditText(context) { Text = text, Hint = hint, Typeface = Typeface.Monospace, Background = Box(context, SurfaceVariant, Border) };
        field.SetTextSize(ComplexUnitType.Sp, 15);
        field.SetTextColor(Text); field.SetHintTextColor(Muted);
        field.SetPadding(Dp(context, 10), Dp(context, 6), Dp(context, 10), Dp(context, 6));
        field.SetSingleLine(true);
        return field;
    }

    public static void SetEnabled(View view, bool enabled)
    {
        view.Enabled = enabled;
        view.Alpha = enabled ? 1f : 0.4f;
    }

    public static LinearLayout.LayoutParams Wrap(Context context, float marginDp = 4)
    {
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        int margin = Dp(context, marginDp);
        layout.SetMargins(margin, margin, margin, margin);
        return layout;
    }

    public static LinearLayout.LayoutParams Fill(Context context, float marginDp = 4)
    {
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        int margin = Dp(context, marginDp);
        layout.SetMargins(margin, margin, margin, margin);
        return layout;
    }
}
