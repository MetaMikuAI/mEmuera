using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class NumberedText
{
    private sealed record Word(int Start, int Length, bool Space, long? Value);
    public static IReadOnlyList<NumberedTextPart> Split(string text)
    {
        NumberedTextPart[] Plain() => [new(0, text.Length, null)];
        var words = new List<Word>();
        for (int at = 0; at < text.Length;)
        {
            int start = at; char c = text[at++]; long? value = null;
            bool space = char.IsWhiteSpace(c);
            if (c == ']') return Plain();
            if (c == '[')
            {
                while (at < text.Length && text[at] is not ('[' or ']')) at++;
                if (at == text.Length || text[at] != ']') return Plain();
                string number = text[(start + 1)..at].Trim(); at++;
                // 旧按钮只接受整数核；小数和十六进制字母不是按钮。
                bool syntax = number.All(ch => char.IsAsciiDigit(ch) || "xXbB+-eEpP".Contains(ch));
                if (syntax && NumericText.Parse(number, false, out long parsed)) value = parsed;
            }
            else while (at < text.Length && text[at] is not ('[' or ']') && char.IsWhiteSpace(text[at]) == space) at++;
            words.Add(new(start, at - start, space, value));
        }
        int count = 0; bool before = false, after = false; long? only = null;
        foreach (var word in words)
        {
            if (word.Space) continue;
            if (word.Value is { } value) { count++; only = value; after = false; }
            else { after = true; if (count == 0) before = true; }
        }
        if (count <= 1) return [new(0, text.Length, only)];
        bool right = !before && after, left = before && !after;
        var parts = new List<NumberedTextPart>(); int begin = 0, end = 0, state = 0; long? current = null;
        void Flush()
        {
            if (end > begin) parts.Add(new(begin, end - begin, current));
            begin = end; current = null;
        }
        foreach (var word in words)
        {
            if (word.Space)
            {
                if (state == 3 && !left && !right && word.Length >= 2) { Flush(); state = 0; }
                end += word.Length; continue;
            }
            if (word.Value is { } value)
            {
                if ((state & 1) != 0 || right) { Flush(); end += word.Length; current = value; state = 1; }
                else if (left) { end += word.Length; current = value; Flush(); state = 0; }
                else { end += word.Length; current = value; state = 1; }
            }
            else { end += word.Length; state |= 2; }
        }
        Flush(); return parts;
    }
}
