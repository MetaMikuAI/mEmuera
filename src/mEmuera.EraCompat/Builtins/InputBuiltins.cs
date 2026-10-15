using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class InputBuiltins
{
    private sealed record AwaitHandler(string Id, bool AnyKey) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            if (AnyKey) { context.Input(InputKind.Wait); context.InputMode(false, false, true); }
            else
            {
                long duration = a[0].IsOmitted ? 0 : a[0].Value.AsInt();
                if (duration is < 0 or > 10000) throw new ScriptException("AWAIT_RANGE", "AWAIT 时长应在 0～10000 毫秒内");
                context.ClearKeyLatches();
                context.TimedWait(duration, true);
            }
            return null;
        }
    }
    private sealed record WaitHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long duration = a[0].Value.AsInt();
            context.TimedWait(duration > 0 ? duration : null, a[1].Value.AsInt() != 0); return null;
        }
    }
    private sealed record Handler(string Id, bool Timed, bool String, bool NoFocus, bool Single, bool Buttons) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            int at = Timed ? 1 : 0;
            EraValue? value = a[at].IsOmitted ? null : a[at].Value;
            int skipIndex = Timed ? 5 : 2;
            if (!a[skipIndex].IsOmitted && context.MessageSkip)
            {
                if (value is null) throw new ScriptException("INPUT_DEFAULT", "跳过输入需要默认值");
                int resultIndex = a[Timed ? 4 : 1].Value.AsInt() == 0 ? 0 : 1;
                if (String) context.SetStringResult(value.Value.AsString(), resultIndex);
                else context.SetResult(value.Value.AsInt(), resultIndex);
                return null;
            }
            long? timeout = Timed && a[0].Value.AsInt() > 0 ? a[0].Value.AsInt() : null;
            context.Input(String ? InputKind.String : InputKind.Integer, timeout, value);
            context.InputOptions(NoFocus, Timed && a[2].Value.AsInt() != 0, Timed ? a[3].IsOmitted ? context.TimeupLabel : a[3].Value.AsString() : "",
                Timed ? a[4].Value.AsInt() == 1 : a[1].Value.AsInt() != 0);
            context.InputMode(Single, Buttons);
            return null;
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("AWAIT", "era.AWAIT", [new(ValueKind.Integer, Default: new EraValue(0L))], null, BuiltinEffect.Input));
        handlers.Add(new AwaitHandler("era.AWAIT", false));
        signatures.Add(new("WAITANYKEY", "era.WAITANYKEY", [], null, BuiltinEffect.Input));
        handlers.Add(new AwaitHandler("era.WAITANYKEY", true));
        signatures.Add(new("TWAIT", "era.TWAIT", [new(ValueKind.Integer), new(ValueKind.Integer)], null, BuiltinEffect.Input));
        handlers.Add(new WaitHandler("era.TWAIT"));
        foreach (string name in new[] { "INPUT", "INPUTS", "TINPUT", "TINPUTS", "TINPUTNF", "TINPUTSNF", "TONEINPUT", "TONEINPUTS", "TONEINPUTNF", "TONEINPUTSNF", "BINPUT", "BINPUTS", "ONEINPUT", "ONEINPUTS", "ONEBINPUT", "ONEBINPUTS" })
        {
            bool timed = name.StartsWith('T'), text = name.Contains('S');
            ValueKind kind = text ? ValueKind.String : ValueKind.Integer;
            ImmutableArray<BuiltinParameter> parameters = timed
                ? [new(ValueKind.Integer), new(kind), new(ValueKind.Integer, Default: new EraValue(1L)), new(ValueKind.String, Default: new EraValue("")), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))]
                : [new(kind, Default: EraValue.Zero(kind)), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))];
            string id = "era." + name;
            signatures.Add(new(name, id, parameters, null, BuiltinEffect.Input));
            handlers.Add(new Handler(id, timed, text, name.EndsWith("NF", StringComparison.Ordinal), name.Contains("ONE", StringComparison.Ordinal), name.Contains('B')));
        }
    }
}
