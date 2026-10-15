using System.Globalization;
using System.Numerics;
using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Compiler.Syntax;

internal static class StringEscapes
{
    public static string Character(char value) => value switch
    { 's' => " ", 'S' => "　", 't' => "\t", 'n' => "\n", 'e' => "\\e", '\n' => "", _ => value.ToString() };
    public static string Decode(string text, SourceSpan? span)
    {
        var value = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\') { value.Append(c); continue; }
            if (++i == text.Length) throw new ScriptException("STRING", "转义符后缺少字符", span);
            value.Append(Character(text[i]));
        }
        return value.ToString();
    }
}

internal sealed record Token(string Text, EraValue? Value = null, int Start = 0, int Length = 1, string? Format = null, string? Original = null);
internal sealed class Tokens
{
    private readonly List<Token> tokens = [];
    private int position;
    private readonly SourceSpan? span;
    public Token Current => tokens[Math.Min(position, tokens.Count - 1)];
    public SourceSpan? At(Token token) => span is null ? null : span with { Start = span.Start + token.Start, Column = span.Column + token.Start, Length = token.Length };
    public ScriptException Error(string code, string message) => new(code, message, At(Current));
    public Tokens(string text, SourceSpan? span = null, bool assignmentPrefix = false)
    {
        this.span = span;
        int parentheses = 0;
        for (int i = 0; i < text.Length;)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\\' && text.AsSpan(i).StartsWith("\\@"))
            {
                int start = i; i = FormattedText.ConditionalEnd(text, i, span);
                tokens.Add(new("format", Start: start, Length: i - start, Format: text[start..i])); continue;
            }
            if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
            {
                int start = i; i += 2; int content = i;
                i = FormattedText.QuotedEnd(text, i, span);
                if (i == text.Length) throw new ScriptException("FORMAT", "格式字符串未结束", At(new("format", Start: start)));
                tokens.Add(new("format", Start: start, Length: i + 1 - start, Format: text[content..i])); i++; continue;
            }
            if (c == '"')
            {
                int start = i;
                i++;
                var value = new StringBuilder();
                bool closed = false;
                while (i < text.Length)
                {
                    c = text[i++];
                    if (c == '"') { closed = true; break; }
                    if (c == '\\' && i < text.Length) { value.Append(StringEscapes.Character(text[i++])); continue; }
                    value.Append(c);
                }
                if (!closed) throw new ScriptException("STRING", "字符串未结束", At(new("string", Start: start, Length: i - start)));
                tokens.Add(new("string", new(value.ToString()), start, i - start)); continue;
            }
            if (char.IsDigit(c))
            {
                int start = i, radix = 10;
                if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X' or 'b' or 'B')
                { radix = char.ToUpperInvariant(text[i + 1]) == 'X' ? 16 : 2; i += 2; }
                int digits = i;
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || radix == 16 && char.IsAsciiHexDigit(text[i]))) i++;
                if (radix == 10 && i < text.Length && text[i] == '.')
                {
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    if (i < text.Length && text[i] is 'e' or 'E')
                    {
                        i++; if (i < text.Length && text[i] is '+' or '-') i++;
                        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    }
                    if (!double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double real) || !double.IsFinite(real))
                        throw new ScriptException("FLOAT", "浮点常量无效", At(new("number", Start: start)));
                    tokens.Add(new("number", new(real), start, i - start)); continue;
                }
                bool suffix = i < text.Length && text[i] is 'p' or 'P' or 'e' or 'E';
                if (radix == 10 && !suffix && text[digits..i] == "9223372036854775808")
                { tokens.Add(new("minMagnitude", Start: start, Length: i - start)); continue; }
                try
                {
                    long number = radix == 10 ? long.Parse(text[digits..i], NumberStyles.None, CultureInfo.InvariantCulture) : Convert.ToInt64(text[digits..i], radix);
                    if (suffix)
                    {
                        int basis = char.ToUpperInvariant(text[i++]) == 'P' ? 2 : 10, exponentStart = i;
                        if (i < text.Length && text[i] is '+' or '-') i++;
                        while (i < text.Length && (char.IsAsciiDigit(text[i]) || radix == 16 && char.IsAsciiHexDigit(text[i]))) i++;
                        long rawExponent = radix == 10 ? long.Parse(text[exponentStart..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                            : Convert.ToInt64(text[exponentStart..i], radix);
                        int exponent = checked((int)rawExponent);
                        if (basis == 2 && exponent is > 0 and <= 63)
                        {
                            BigInteger expanded = (BigInteger)number << exponent;
                            // 最高位常量按 Int64 位模式解释，其余值仍检查范围。
                            number = expanded == (BigInteger.One << 63) ? long.MinValue : checked((long)expanded);
                        }
                        else if (exponent != 0)
                        {
                            double expanded = number * Math.Pow(basis, exponent);
                            if (!double.IsFinite(expanded) || expanded >= 9223372036854775808d || expanded < -9223372036854775808d) throw new OverflowException();
                            number = (long)expanded;
                        }
                    }
                    tokens.Add(new("number", new(number), start, i - start));
                }
                catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                { throw new ScriptException("INTEGER", "整数常量或指数无效、超出范围", At(new("number", Start: start, Length: i - start))); }
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c >= 128)
            {
                int start = i++;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] >= 128)) i++;
                tokens.Add(new(text[start..i].ToUpperInvariant(), Start: start, Length: i - start, Original: text[start..i])); continue;
            }
            string pair = i + 1 < text.Length ? text.Substring(i, 2) : "";
            if (pair is "<<" or ">>" && i + 2 < text.Length && text[i + 2] == '=')
            { tokens.Add(new(pair + "=", Start: i, Length: 3)); i += 3; }
            else if (new[] { "==", "!=", "<=", ">=", "&&", "||", "^^", "!&", "!|", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "++", "--", "'=" }.Contains(pair))
            { tokens.Add(new(pair, Start: i, Length: 2)); i += 2; }
            else { tokens.Add(new(c.ToString(), Start: i)); i++; }
            if (c == '(') parentheses++;
            if (c == ')') parentheses--;
            if (assignmentPrefix && parentheses == 0 && tokens[^1].Text is "=" or "'=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=") break;
        }
        tokens.Add(new("<end>", Start: text.Length, Length: 0));
    }
    public Token Take() { var current = Current; if (position < tokens.Count - 1) position++; return current; }
    public bool Match(string value) { if (Current.Text != value) return false; position++; return true; }
    public void Need(string value) { if (!Match(value)) throw Error("SYNTAX", "需要 " + value + "，实际为 " + Current.Text); }
}
internal abstract record Expr { public SourceSpan? Span { get; init; } }
internal sealed record Literal(EraValue Value) : Expr;
internal sealed record FormattedLiteral(string Text) : Expr;
internal sealed record Omitted : Expr;
internal sealed record Variable(string Name, Expr Index, IReadOnlyList<Expr>? MoreIndices = null, bool ExplicitIndices = false) : Expr
{
    public string? Keyword { get; init; }
    public IReadOnlyList<Expr> Indices => MoreIndices is null ? [Index] : new[] { Index }.Concat(MoreIndices).ToArray();
}
internal sealed record Unary(string Operator, Expr Operand) : Expr;
internal sealed record Binary(string Operator, Expr Left, Expr Right) : Expr;
internal sealed record Conditional(Expr Test, Expr Yes, Expr No) : Expr;
internal sealed record Invocation(string Name, IReadOnlyList<Expr> Arguments) : Expr;
internal sealed record Mutation(Variable Variable, int Delta, bool Postfix) : Expr;

internal static class ExpressionParser
{
    // OperatorCode.__PRIORITY_MASK__，同组运算符左结合。
    private static int Priority(string op) => op switch
    {
        "*" or "/" or "%" => 90, "+" or "-" => 80, "<<" or ">>" => 70,
        ">" or "<" or ">=" or "<=" => 65, "==" or "!=" => 60,
        "&" or "|" or "^" => 50, "&&" or "||" or "^^" or "!&" or "!|" => 40, _ => -1
    };
    public static Expr Parse(string text, SourceSpan? span = null)
    {
        var tokens = new Tokens(text, span);
        var expression = Parse(tokens);
        tokens.Need("<end>");
        return expression;
    }
    public static Expr Parse(Tokens tokens, int minimum = 0, bool subscripts = true)
    {
        Token token = tokens.Take();
        Expr left;
        if (token.Format is { } format) left = new FormattedLiteral(format);
        else if (token.Value is { } value) left = new Literal(value);
        else if (token.Text == "(") { left = Parse(tokens); tokens.Need(")"); }
        else if (token.Text == "-" && tokens.Match("minMagnitude")) left = new Literal(new(long.MinValue));
        else if (token.Text is "+" or "-" or "!" or "~") left = new Unary(token.Text, Parse(tokens, 100));
        else if (token.Text is "++" or "--")
        {
            if (Parse(tokens, 100) is not Variable variable) throw new ScriptException("REFERENCE", "递增需要变量");
            left = new Mutation(variable, token.Text == "++" ? 1 : -1, false);
        }
        else if (char.IsLetter(token.Text[0]) || token.Text[0] == '_' || token.Text[0] >= 128)
        {
            if (tokens.Match("("))
            {
                var args = new List<Expr>();
                if (!tokens.Match(")"))
                {
                    do
                    {
                        if (tokens.Current.Text == ")") break;
                        args.Add(tokens.Current.Text == "," ? new Omitted() : Parse(tokens));
                    } while (tokens.Match(","));
                    tokens.Need(")");
                }
                left = new Invocation(token.Text, args);
            }
            else
            {
                var indices = new List<Expr>();
                while (subscripts && tokens.Match(":")) indices.Add(Parse(tokens, 100, false));
                left = new Variable(token.Text, indices.Count == 0 ? new Literal(new(0L)) : indices[0], indices.Skip(1).ToArray(), indices.Count > 0) { Keyword = token.Original };
            }
        }
        else throw new ScriptException("EXPRESSION", "非法表达式标记: " + token.Text, tokens.At(token));
        left = left with { Span = tokens.At(token) };
        while (true)
        {
            string op = tokens.Current.Text;
            if (subscripts && op is "++" or "--" && left is Variable variable)
            { tokens.Take(); left = new Mutation(variable, op == "++" ? 1 : -1, true); continue; }
            int priority = Priority(op);
            if (priority < minimum) break;
            tokens.Take(); left = new Binary(op, left, Parse(tokens, priority + 1));
        }
        if (minimum == 0 && tokens.Match("?"))
        {
            var yes = Parse(tokens); tokens.Need("#"); left = new Conditional(left, yes, Parse(tokens));
        }
        return left;
    }
}
