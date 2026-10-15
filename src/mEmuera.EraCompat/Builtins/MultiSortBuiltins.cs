using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class MultiSortBuiltins
{
    private sealed record DynamicHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long length = a[3].Value.AsInt();
            if (length == 0) return new(0L);
            if (length < -1 || length > int.MaxValue) throw new ScriptException("BOUNDS", "ARRAYMSORTEX 长度无效");
            var names = a[1].Reference!; var spec = context.VariableInfo(names);
            if (spec.Shape.Length != 1 || spec.Scope == VariableScope.Character) throw new ScriptException("REFERENCE", "目标名称需要非角色一维字符串数组");
            string key = a[0].Reference is { } reference ? context.VariableInfo(reference).Name : a[0].Value.AsString();
            var targets = Enumerable.Range(0, spec.Length).Select(i => context.Read(names with { Index = i }).AsString()).ToImmutableArray();
            context.MultiSort(new(key, targets, a[2].Value.AsInt() != 0, length));
            return null;
        }
    }

    private sealed record Handler(string Id, bool Extended) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            int keyIndex = Extended ? 2 : 0;
            var key = a[keyIndex].Reference!; var keySpec = context.VariableInfo(key);
            if (keySpec.Shape.Length != 1 || keySpec.Scope == VariableScope.Character || keySpec.ReadOnly)
                throw new ScriptException("REFERENCE", "关联排序键需要可写非角色一维数组");
            long requested = Extended ? a[1].Value.AsInt() : -1;
            bool ascending = !Extended || a[0].Value.AsInt() != 0;
            int length = requested < 0 ? keySpec.Length : (int)Math.Min(requested, keySpec.Length);
            var keys = new EraValue[length];
            for (int i = 0; i < length; i++)
            {
                keys[i] = context.Read(key with { Index = i });
                if (requested < 0 && keys[i] == EraValue.Zero(keySpec.Kind)) { length = i; break; }
            }
            var indices = Enumerable.Range(0, length).ToArray();
            Array.Sort(indices, (x, y) =>
            {
                if (!ascending) (x, y) = (y, x);
                return keySpec.Kind switch
                {
                    ValueKind.Integer => Extended ? keys[x].Integer.CompareTo(keys[y].Integer) : Math.Sign(unchecked(keys[x].Integer - keys[y].Integer)),
                    ValueKind.Float => keys[x].Float.CompareTo(keys[y].Float),
                    _ => string.Compare(keys[x].Text, keys[y].Text, Extended ? StringComparison.Ordinal : StringComparison.CurrentCulture)
                };
            });
            foreach (var argument in a.Skip(Extended ? 3 : 0))
            {
                var reference = argument.Reference!; var spec = context.VariableInfo(reference);
                if (spec.ReadOnly || spec.Scope == VariableScope.Character) throw new ScriptException("REFERENCE", "关联排序目标需要可写非角色数组");
                if (spec.Shape[0] < length) return new(0L);
                int stride = spec.Length / spec.Shape[0];
                var values = new EraValue[length * stride];
                for (int i = 0; i < values.Length; i++) values[i] = context.Read(reference with { Index = i });
                for (int i = 0; i < length; i++)
                    for (int j = 0; j < stride; j++) context.Write(reference with { Index = i * stride + j }, values[indices[i] * stride + j]);
            }
            return new(1L);
        }
    }

    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("ARRAYMSORT", "era.ARRAYMSORT", [new(null, Reference: true)], ValueKind.Integer, BuiltinEffect.Query, new(null, Reference: true)));
        handlers.Add(new Handler("era.ARRAYMSORT", false));
        signatures.Add(new("ARRAYMSORTEX", "era.ARRAYMSORTEX", [new(null, CaptureReference: true), new(ValueKind.String, Reference: true),
            new(ValueKind.Integer, Default: new EraValue(1L)), new(ValueKind.Integer, Default: new EraValue(-1L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new DynamicHandler("era.ARRAYMSORTEX"));
        signatures.Add(new("$ARRAYMSORTEX", "era.ARRAYMSORTEX.lowered", [new(ValueKind.Integer), new(ValueKind.Integer), new(null, Reference: true)],
            ValueKind.Integer, BuiltinEffect.Query, new(null, Reference: true)));
        handlers.Add(new Handler("era.ARRAYMSORTEX.lowered", true));
    }
}
