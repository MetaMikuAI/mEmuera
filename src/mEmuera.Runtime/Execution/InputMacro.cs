using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Runtime.Execution;

internal static class InputMacro
{
    private const int MaximumLength = 1_000_000;
    public static bool HasSyntax(string text) => text.IndexOfAny(['(', '\r', '\n']) >= 0
        || text.Contains("\\n", StringComparison.Ordinal) || text.Contains("\\e", StringComparison.Ordinal);
    public static string[] Expand(string text)
    {
        if (text.Length > MaximumLength) throw new ScriptException("INPUT_MACRO_LIMIT", "输入宏超过长度限制");
        int at = 0;
        string expanded = text.Contains('(') ? Parse(text, ref at, 0) : text;
        return expanded.Split(["\\n", "\r\n", "\n", "\r"], StringSplitOptions.None);
    }
    private static string Parse(string text, ref int at, int depth)
    {
        if (depth > 128) throw new ScriptException("INPUT_MACRO_LIMIT", "输入宏嵌套过深");
        var result = new StringBuilder();
        bool afterSkip = false;
        while (at < text.Length && (depth == 0 || text[at] != ')'))
        {
            char current = text[at++];
            if (current == '(')
            {
                string inner = Parse(text, ref at, depth + 1);
                int repeat = 1;
                if (at < text.Length)
                {
                    at++;
                    if (at < text.Length && text[at] == '*')
                    {
                        int start = ++at;
                        while (at < text.Length && char.IsNumber(text[at])) at++;
                        if (!int.TryParse(text.AsSpan(start, at - start), out repeat)) repeat = 0;
                    }
                }
                if ((long)inner.Length * repeat + result.Length > MaximumLength)
                    throw new ScriptException("INPUT_MACRO_LIMIT", "输入宏展开超过长度限制");
                if (inner.Length > 0) for (int i = 0; i < repeat; i++) result.Append(inner);
            }
            else if (current == '\\' && at < text.Length)
            {
                switch (text[at++])
                {
                    case 'n': if (!afterSkip) result.Append('\n'); else afterSkip = false; break;
                    case 'r': result.Append('\r'); break;
                    case 'e': result.Append("\\e\n"); afterSkip = true; break;
                    case '\n': break;
                    default: result.Append(text[at - 1]); break;
                }
            }
            else result.Append(current);
            if (result.Length > MaximumLength) throw new ScriptException("INPUT_MACRO_LIMIT", "输入宏展开超过长度限制");
        }
        return result.ToString();
    }
}
