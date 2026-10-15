using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class HtmlTextBuiltins
{
    private sealed record PlainHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string text;
            try { text = Regex.Replace(a[0].Value.AsString(), "<[^<]*>", "", RegexOptions.None, TimeSpan.FromSeconds(1)); }
            catch (RegexMatchTimeoutException) { throw new ScriptException("HTML_LIMIT", "HTML 文本转换超时"); }
            var output = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '&') { output.Append(text[i]); continue; }
                int end = text.IndexOf(';', i + 1);
                if (end <= i + 1) throw new ScriptException("HTML_ENTITY", "HTML 实体缺少名称或分号");
                string entity = text[(i + 1)..end].ToLowerInvariant();
                char value;
                switch (entity)
                {
                    case "nbsp": value = ' '; break;
                    case "amp": value = '&'; break;
                    case "lt": value = '<'; break;
                    case "gt": value = '>'; break;
                    case "quot": value = '"'; break;
                    case "apos": value = '\''; break;
                    default:
                        if (!entity.StartsWith('#')) throw new ScriptException("HTML_ENTITY", "未知 HTML 实体");
                        try
                        {
                            int code = entity.StartsWith("#x", StringComparison.Ordinal) ? Convert.ToInt32(entity[2..], 16) : Convert.ToInt32(entity[1..], 10);
                            if (code < 0 || code > 65535) throw new FormatException();
                            value = (char)code;
                        }
                        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
                        { throw new ScriptException("HTML_ENTITY", "HTML 数字实体无效"); }
                        break;
                }
                output.Append(value); i = end;
            }
            return new(output.ToString());
        }
    }
    private sealed record SplitHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string text = a[0].Value.AsString(); var pieces = new List<string>();
            var target = a[1].Reference!; var count = a[2].Reference!;
            var spec = context.VariableInfo(target);
            if (spec.Shape.Length != 1 || spec.Scope == VariableScope.Character || spec.ReadOnly) throw new ScriptException("REFERENCE", "HTML_TAGSPLIT 需要可写非角色一维字符串数组");
            int at = 0;
            while (at < text.Length)
            {
                int open = text.IndexOf('<', at);
                if (open < 0) { pieces.Add(text[at..]); break; }
                if (open > at) pieces.Add(text[at..open]);
                int close = text.IndexOf('>', open);
                if (close < 0) { context.Write(count, new(-1L)); return null; }
                pieces.Add(text[open..(close + 1)]); at = close + 1;
            }
            context.Write(count, new((long)pieces.Count));
            for (int i = 0; i < Math.Min(pieces.Count, spec.Length); i++) context.Write(target with { Index = i }, new(pieces[i]));
            return null;
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("HTML_TOPLAINTEXT", "era.HTML_TOPLAINTEXT", [new(ValueKind.String)], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new PlainHandler("era.HTML_TOPLAINTEXT"));
        signatures.Add(new("HTML_TAGSPLIT", "era.HTML_TAGSPLIT", [new(ValueKind.String), new(ValueKind.String, Reference: true), new(ValueKind.Integer, Reference: true)], null, BuiltinEffect.Query));
        handlers.Add(new SplitHandler("era.HTML_TAGSPLIT"));
    }
}
