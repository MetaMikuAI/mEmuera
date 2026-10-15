using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class TemplateBuiltins
{
    private sealed class FindHandler : IBuiltinHandler
    {
        public string Id => "era.GETCHARA";
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long special = -1;
            for (long i = 0; i < context.CharacterCount; i++)
            {
                if (context.ReadCharacter("NO", [0], i).AsInt() != a[0].Value.AsInt()) continue;
                if (!context.AllowSpCharacters || context.ReadCharacter("CFLAG", [0], i).AsInt() == 0) return new(i);
                if (special < 0) special = i;
            }
            return new(context.AllowSpCharacters && a[1].Value.AsInt() != 0 ? special : -1L);
        }
    }
    private sealed record Handler(string Id, string Variable, bool Scalar) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            if (a[^1].Value.AsInt() != 0) throw new ScriptException("SP_CHARACTER", "尚未支持 SP 角色模板");
            if (Variable == "EXISTCSV") return new(context.TemplateExists(a[0].Value.AsInt()) ? 1L : 0L);
            return context.TemplateValue(a[0].Value.AsInt(), Variable, Scalar ? 0 : a[1].Value.AsInt());
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("GETCHARA", "era.GETCHARA", [new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new FindHandler());
        signatures.Add(new("EXISTCSV", "era.EXISTCSV", [new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new Handler("era.EXISTCSV", "EXISTCSV", true));
        foreach (string name in "BASE ABL EXP TALENT MARK RELATION CFLAG EQUIP JUEL NAME CALLNAME NICKNAME MASTERNAME CSTR".Split(' '))
        {
            bool text = name is "NAME" or "CALLNAME" or "NICKNAME" or "MASTERNAME" or "CSTR", scalar = text && name != "CSTR";
            string id = "era.CSV" + name;
            ImmutableArray<BuiltinParameter> parameters = scalar
                ? [new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L))]
                : [new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L))];
            signatures.Add(new("CSV" + name, id, parameters, text ? ValueKind.String : ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new Handler(id, name, scalar));
        }
    }
}
