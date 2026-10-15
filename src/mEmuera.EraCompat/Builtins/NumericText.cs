using System.Globalization;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class NumericText
{
    public static bool Parse(string text, bool check, out long value)
    {
        value = 0;
        if (text.Length == 0 || !char.IsAsciiDigit(text[0]) && text[0] is not ('+' or '-')) return false;
        if (text[0] is '+' or '-' && (text.Length == 1 || !char.IsAsciiDigit(text[1]))) return false;
        int at = 0, radix = 10;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; at = 2; }
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { radix = 2; at = 2; }
        if (at == text.Length) return false;
        long Digits()
        {
            int start = at;
            if (at < text.Length && text[at] is '+' or '-') at++;
            while (at < text.Length && (char.IsAsciiDigit(text[at]) || radix == 16 && char.IsAsciiHexDigit(text[at]))) at++;
            string part = text[start..at];
            return radix == 10 ? long.Parse(part, CultureInfo.InvariantCulture) : Convert.ToInt64(part, radix);
        }
        try
        {
            value = Digits();
            if (at < text.Length && text[at] is 'p' or 'P' or 'e' or 'E')
            {
                int basis = char.ToUpperInvariant(text[at++]) == 'P' ? 2 : 10;
                if (check && (at == text.Length || !char.IsAsciiDigit(text[at]))) return false;
                int exponent = unchecked((int)Digits());
                if (exponent != 0)
                {
                    double result = value * Math.Pow(basis, exponent);
                    if (!double.IsFinite(result) || result > long.MaxValue || result < long.MinValue) throw new OverflowException();
                    value = unchecked((long)result);
                }
            }
            if (at < text.Length && text[at] == '.')
            {
                at++;
                while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
            }
            return at == text.Length;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            if (check) throw new ScriptException("INTEGER", "数值格式错误或超出 64 位整数范围");
            return false;
        }
    }
    public static EraValue ToInt(EraValue value)
    {
        if (value.Kind == ValueKind.Float) return new(unchecked((long)value.AsFloat()));
        if (value.Kind != ValueKind.String) throw new ScriptException("TYPE", "TOINT 需要字符串或浮点数");
        return new(Parse(value.AsString(), false, out long result) ? result : 0L);
    }
}
