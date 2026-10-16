using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class StringBuiltins
{
    [ThreadStatic] private static HashSet<string>? validatedRegexPatterns;
    private sealed record MatchHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            bool explicitOutput = !a[3].IsOmitted;
            SlotReference? count = a[2].Reference, output = a[3].Reference;
            if (explicitOutput && (count is null || output is null || context.VariableInfo(count).ReadOnly
                || context.VariableInfo(output) is var spec && (spec.ReadOnly || spec.Shape.Length != 1 || spec.Scope == VariableScope.Character)))
                throw new ScriptException("REFERENCE", "REGEXPMATCH 输出需要可写整数和一维字符串数组");
            try
            {
                var regex = new Regex(a[1].Value.AsString(), RegexOptions.None, TimeSpan.FromSeconds(1));
                var matches = regex.Matches(a[0].Value.AsString());
                int result = matches.Count;
                if (explicitOutput || a[2].Value.AsInt() != 0)
                {
                    string[] names = regex.GetGroupNames();
                    if (explicitOutput) context.Write(count!, new((long)names.Length)); else context.SetResult(names.Length, 1);
                    int length = explicitOutput ? context.ArrayLength(output!) : context.VariableInfo("RESULTS").Length, at = 0;
                    foreach (Match match in matches)
                        foreach (string name in names)
                        {
                            if (at >= length) return new((long)result);
                            string value = match.Groups[name].Value;
                            if (explicitOutput) context.Write(output! with { Index = at }, new(value)); else context.SetStringResult(value, at);
                            at++;
                        }
                }
                return new((long)result);
            }
            catch (ArgumentException) { throw new ScriptException("REGEX", "REGEXPMATCH 正则表达式无效"); }
            catch (RegexMatchTimeoutException) { throw new ScriptException("REGEX_TIMEOUT", "REGEXPMATCH 执行超时"); }
        }
    }
    private sealed record MoneyHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            try
            {
                string value = a[0].Value.AsInt().ToString(a[1].Value.AsString(), System.Globalization.CultureInfo.InvariantCulture);
                return new(context.MoneyFirst ? context.MoneyLabel + value : value + context.MoneyLabel);
            }
            catch (FormatException) { throw new ScriptException("MONEYSTR", "金额格式无效"); }
        }
    }
    private sealed record EscapeHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a) => new(Regex.Escape(a[0].Value.AsString()));
    }
    private sealed record FullWidthHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var encoding = EncodingFor(context.LanguageCodePage);
            string source = encoding.GetString(encoding.GetBytes(a[0].Value.AsString()));
            var result = new StringBuilder();
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (c is >= '\uff61' and <= '\uff9f')
                {
                    int start = i;
                    while (i + 1 < source.Length && source[i + 1] is >= '\uff61' and <= '\uff9f') i++;
                    result.Append(source[start..(i + 1)].Normalize(NormalizationForm.FormKC).Replace('\u3099', '\u309b').Replace('\u309a', '\u309c'));
                }
                else result.Append(c switch
                {
                    ' ' => '\u3000', '\\' => '\\',
                    >= '!' and <= '~' => (char)(c + 0xfee0),
                    '\u00a2' => '\uffe0', '\u00a3' => '\uffe1', '\u00a5' => '\uffe5',
                    '\u00a6' => '\uff5c', '\u00ac' => '\uffe2', '\u00af' => '\uffe3', _ => c
                });
            }
            return new(encoding.GetString(encoding.GetBytes(result.ToString())));
        }
    }
    private static readonly Dictionary<char, string> NarrowKana = CreateNarrowKana();
    private static Dictionary<char, string> CreateNarrowKana()
    {
        var result = new Dictionary<char, string>();
        for (char c = '\uff61'; c <= '\uff9f'; c++)
        {
            string full = c.ToString().Normalize(NormalizationForm.FormKC);
            if (full.Length == 1) result[full[0]] = c.ToString();
            foreach (char mark in "\uff9e\uff9f")
            {
                full = (c.ToString() + mark).Normalize(NormalizationForm.FormKC);
                if (full.Length == 1) result[full[0]] = c.ToString() + mark;
            }
        }
        result['\u309b'] = "\uff9e"; result['\u309c'] = "\uff9f";
        return result;
    }
    private sealed record HalfWidthHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var encoding = EncodingFor(context.LanguageCodePage);
            string source = encoding.GetString(encoding.GetBytes(a[0].Value.AsString()));
            var result = new StringBuilder();
            foreach (char c in source)
                if (NarrowKana.TryGetValue(c, out string? kana)) result.Append(kana);
                else result.Append(c switch
                {
                    '\uff3c' when context.LanguageCodePage is 932 or 936 or 949 or 950 => '\uff3c',
                    '\u3000' => ' ', >= '\uff01' and <= '\uff5e' => (char)(c - 0xfee0),
                    '\u2018' or '\u2019' => '\'', '\u201c' or '\u201d' => '"', '\uffe4' => '|',
                    '\uffe0' => '\u00a2', '\uffe1' => '\u00a3', '\uffe5' => '\u00a5',
                    '\uffe2' => '\u00ac', '\uffe3' => '\u00af', _ => c
                });
            return new(encoding.GetString(encoding.GetBytes(result.ToString())));
        }
    }
    private sealed record KanaHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string text = a[0].Value.AsString();
            if (context.KanaMode == 0) return new(text);
            if (context.KanaMode == 3) text = new FullWidthHandler("").Execute(context, a)!.Value.AsString();
            var result = new StringBuilder(text.Length);
            foreach (char c in text)
                result.Append(context.KanaMode == 1 && c is >= '\u3041' and <= '\u3096' ? (char)(c + 0x60)
                    : context.KanaMode > 1 && c is >= '\u30a1' and <= '\u30f6' ? (char)(c - 0x60) : c);
            return new(result.ToString());
        }
    }
    private sealed record ReplaceHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string text = a[0].Value.AsString(), pattern = a[1].Value.AsString(); int mode = unchecked((int)a[3].Value.AsInt());
            try
            {
                if (mode == 2) return new(text.Replace(pattern, a[2].Value.AsString(), StringComparison.Ordinal));
                var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
                if (mode != 1) return new(regex.Replace(text, a[2].Value.AsString()));
                var reference = a[2].Reference ?? throw new ScriptException("REFERENCE", "REPLACE 模式 1 需要字符串数组");
                var spec = context.VariableInfo(reference);
                if (spec.Shape.Length != 1 || spec.ReadOnly || spec.Scope == VariableScope.Character) throw new ScriptException("REFERENCE", "REPLACE 模式 1 需要可写一维字符串数组");
                int at = 0, length = context.ArrayLength(reference);
                return new(regex.Replace(text, _ => at < length ? context.Read(reference with { Index = at++ }).AsString() : ""));
            }
            catch (ArgumentException) { throw new ScriptException("REPLACE", "替换模式或参数无效"); }
            catch (RegexMatchTimeoutException) { throw new ScriptException("REGEX_TIMEOUT", "替换正则表达式超时"); }
        }
    }
    private static readonly ConcurrentDictionary<int, Encoding> Encodings = new();
    static StringBuiltins() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static Encoding EncodingFor(int code) => Encodings.GetOrAdd(code, Encoding.GetEncoding);
    private static int Width(char character, Encoding language)
    {
        string value = character.ToString(); byte[] bytes = language.GetBytes(value);
        if (language.GetString(bytes) == value) return bytes.Length;
        var japanese = EncodingFor(932); byte[] alternative = japanese.GetBytes(value);
        return japanese.GetString(alternative) == value ? alternative.Length : bytes.Length;
    }
    private sealed record Handler(string Name, string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string text = a[0].Value.AsString(); bool unicode = Name.EndsWith('U');
            var language = EncodingFor(context.LanguageCodePage);
            int total = unicode ? text.Length : text.Sum(c => Width(c, language));
            if (Name.StartsWith("STRLENS", StringComparison.Ordinal)) return new((long)total);
            int start = unchecked((int)a[1].Value.AsInt()), length = unchecked((int)a[2].Value.AsInt());
            if (start >= total || length == 0) return new("");
            if (length < 0 || length > total) length = total;
            start = Math.Max(0, start);
            if (unicode) return new(text.Substring(start, Math.Min(length, text.Length - start)));
            int first = 0, skipped = 0;
            while (first < text.Length && skipped < start) skipped += Width(text[first++], language);
            int end = first, taken = 0;
            while (end < text.Length && taken < length) taken += Width(text[end++], language);
            return new(text[first..end]);
        }
    }
    private sealed record SplitHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string[] pieces = a[0].Value.AsString().Split([a[1].Value.AsString()], StringSplitOptions.None);
            var destination = a[2].Reference ?? throw new ScriptException("REFERENCE", "SPLIT 需要字符串数组");
            if (a[3].IsOmitted) context.SetResult(pieces.Length);
            else context.Write(a[3].Reference!, new((long)pieces.Length));
            var shape = context.VariableInfo(destination).Shape;
            int count = Math.Min(pieces.Length, Math.Min(shape[0], shape[^1]));
            for (int i = 0; i < count; i++) context.Write(destination with { Index = i }, new(pieces[i]));
            return null;
        }
    }
    private sealed record FindHandler(string Id, bool Unicode) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string text = a[0].Value.AsString(); int requested = unchecked((int)a[2].Value.AsInt()), start = requested;
            var language = EncodingFor(context.LanguageCodePage);
            if (!Unicode)
            {
                start = 0; int bytes = 0;
                while (start < text.Length && bytes < requested) bytes += Width(text[start++], language);
            }
            if (start < 0 || start >= text.Length) return new(-1L);
            int found = text.IndexOf(a[1].Value.AsString(), start, StringComparison.Ordinal);
            return new((long)(Unicode || found <= 0 ? found : text[..found].Sum(c => Width(c, language))));
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("$REGEX_PATTERN", "era.audit.REGEX_PATTERN", [new(ValueKind.String)], null, BuiltinEffect.Query));
        handlers.Add(new RegexPatternHandler("era.audit.REGEX_PATTERN"));
        signatures.Add(new("$REGEXPMATCH_CHECK", "era.audit.REGEXPMATCH_CHECK", [new(ValueKind.String), new(ValueKind.String)], null, BuiltinEffect.Query));
        handlers.Add(new RegexMatchCheckHandler("era.audit.REGEXPMATCH_CHECK"));
        signatures.Add(new("REGEXPMATCH", "era.REGEXPMATCH", [new(ValueKind.String), new(ValueKind.String),
            new(ValueKind.Integer, Default: new EraValue(0L), CaptureReference: true), new(ValueKind.String, Default: new EraValue(""), CaptureReference: true)], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new MatchHandler("era.REGEXPMATCH"));
        signatures.Add(new("MONEYSTR", "era.MONEYSTR", [new(ValueKind.Integer), new(ValueKind.String, Default: new EraValue(""))], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new MoneyHandler("era.MONEYSTR"));
        signatures.Add(new("ESCAPE", "era.ESCAPE", [new(ValueKind.String)], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new EscapeHandler("era.ESCAPE"));
        signatures.Add(new("TOFULL", "era.TOFULL", [new(ValueKind.String)], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new FullWidthHandler("era.TOFULL"));
        signatures.Add(new("TOHALF", "era.TOHALF", [new(ValueKind.String)], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new HalfWidthHandler("era.TOHALF"));
        signatures.Add(new("$KANA", "era.KANA", [new(ValueKind.String)], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new KanaHandler("era.KANA"));
        signatures.Add(new("REPLACE", "era.REPLACE", [new(ValueKind.String), new(ValueKind.String), new(ValueKind.String, CaptureReference: true),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new ReplaceHandler("era.REPLACE"));
        const string split = "era.SPLIT";
        signatures.Add(new("SPLIT", split, [new(ValueKind.String), new(ValueKind.String), new(ValueKind.String, Reference: true),
            new(ValueKind.Integer, Reference: true, Default: new EraValue(0L))], null, BuiltinEffect.Query));
        handlers.Add(new SplitHandler(split));
        foreach (string name in new[] { "STRFIND", "STRFINDU" })
        {
            string id = "era." + name;
            signatures.Add(new(name, id, [new(ValueKind.String), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new FindHandler(id, name.EndsWith('U')));
        }
        foreach (string name in new[] { "STRLENS", "STRLENSU", "SUBSTRING", "SUBSTRINGU" })
        {
            string id = "era." + name; bool length = name.StartsWith("STRLENS", StringComparison.Ordinal);
            ImmutableArray<BuiltinParameter> parameters = length ? [new(ValueKind.String)] :
                [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(-1L))];
            signatures.Add(new(name, id, parameters, length ? ValueKind.Integer : ValueKind.String, BuiltinEffect.Query));
            handlers.Add(new Handler(name, id));
        }
    }
    private sealed record RegexPatternHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string pattern = a[0].Value.AsString();
            var validated = validatedRegexPatterns ??= new(StringComparer.Ordinal);
            if (validated.Contains(pattern)) return null;
            try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { throw new ScriptException("REGEX", "正则表达式无效"); }
            if (pattern.Length <= 4096)
            {
                if (validated.Count == 256) validated.Clear();
                validated.Add(pattern);
            }
            return null;
        }
    }
    private sealed record RegexMatchCheckHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            try { _ = new Regex(a[1].Value.AsString(), RegexOptions.None, TimeSpan.FromSeconds(1)).Matches(a[0].Value.AsString()).Count; }
            catch (ArgumentException) { throw new ScriptException("REGEX", "REGEXPMATCH 正则表达式无效"); }
            catch (RegexMatchTimeoutException) { throw new ScriptException("REGEX_TIMEOUT", "REGEXPMATCH 执行超时"); }
            return null;
        }
    }
}
