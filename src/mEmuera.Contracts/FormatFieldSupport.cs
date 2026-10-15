namespace mEmuera.Contracts;

public static class FormatFieldSupport
{
    public static string Pad(string text, long width, bool left, ITextMetrics metrics)
    {
        if (width < 0 || width > 1_000_000) throw new ScriptException("FORMAT", "插值宽度越界");
        int padding = (int)Math.Max(0, width - metrics.Columns(text));
        return left ? text + new string(' ', padding) : new string(' ', padding) + text;
    }
}
