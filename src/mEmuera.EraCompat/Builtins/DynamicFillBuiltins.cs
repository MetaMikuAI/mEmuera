using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class DynamicFillBuiltins
{
    private sealed record Handler(string Id, bool Lowered) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            if (!Lowered)
            {
                context.DynamicFill(new(a[0].Value.AsString(), a[1].Value, a[2].Value.AsInt() != 0, a[3].Value.AsInt(), a[4].IsOmitted ? null : a[4].Value.AsInt()));
                return null;
            }
            var target = a[0].Reference!; var spec = context.VariableInfo(target);
            if (spec.ReadOnly || spec.Scope == VariableScope.Character && spec.Shape.Length != 1) throw new ScriptException("REFERENCE", "VARSETEX 不支持常量或多维角色数组");
            var value = a[1].Value;
            if (value.Kind != spec.Kind && !(spec.Kind == ValueKind.Float && value.Kind == ValueKind.Integer)) throw new ScriptException("TYPE", "VARSETEX 填充值类型不匹配");
            value = value.ConvertTo(spec.Kind);
            var shape = spec.Shape; int width = shape[^1];
            int start = unchecked((int)a[3].Value.AsInt());
            int end = a[4].IsOmitted ? shape.Length == 3 ? 0 : width : unchecked((int)a[4].Value.AsInt());
            bool all = a[2].Value.AsInt() != 0 && spec.Kind != ValueKind.String;
            if (shape.Length > 1) start = Math.Max(start, target.Index % width);
            if (start >= end) return new(1L);
            if (start < 0 || end > width) throw new ScriptException("BOUNDS", "VARSETEX 填充范围越界");
            if (spec.Scope == VariableScope.Character)
            {
                for (int column = start; column < end; column++) context.Write(target with { Index = column }, value);
                return new(1L);
            }
            int firstRow = shape.Length == 1 || all ? 0 : target.Index / width;
            if (shape.Length == 3 && !all)
            {
                int first = target.Index / (shape[1] * width), second = target.Index / width % shape[1];
                if (second >= shape[0] || first >= shape[1]) throw new ScriptException("BOUNDS", "VARSETEX 三维下标越界");
                firstRow = second * shape[1] + first;
            }
            int rows = shape.Length > 1 && all ? spec.Length / width : 1;
            for (int row = firstRow; row < firstRow + rows; row++)
                for (int column = start; column < end; column++) context.Write(target with { Index = row * width + column }, value);
            return new(1L);
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("$VARSETEX_EXPRESSIONS", "era.audit.VARSETEX_EXPRESSIONS", Enumerable.Repeat(new BuiltinParameter(ValueKind.String), 5).ToImmutableArray(), ValueKind.Integer, BuiltinEffect.Flow));
        handlers.Add(new ExpressionHandler("era.audit.VARSETEX_EXPRESSIONS"));
        signatures.Add(new("$VARSETEX_RAW", "era.audit.VARSETEX_RAW", [new(null, Reference: true), new(null), new(ValueKind.Integer), new(ValueKind.Integer),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Flow, new(ValueKind.Integer)));
        handlers.Add(new RawHandler("era.audit.VARSETEX_RAW"));
        foreach (bool lowered in new[] { false, true })
        {
            string name = lowered ? "$VARSETEX" : "VARSETEX", id = "era." + name;
            signatures.Add(new(name, id, [lowered ? new(null, Reference: true) : new(ValueKind.String), new(null),
                new(ValueKind.Integer, Default: new EraValue(1L)), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Flow));
            handlers.Add(new Handler(id, lowered));
        }
    }
    private sealed record ExpressionHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            string? Argument(int at) => a[at].Value.AsString() is { Length: > 0 } value ? value : null;
            context.DynamicFill(new(a[0].Value.AsString(), new(0L), true, 0, null)
            { ValueExpression = Argument(1), AllExpression = Argument(2), StartExpression = Argument(3), EndExpression = Argument(4) });
            return null;
        }
    }
    private sealed record RawHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var target = a[0].Reference!; var spec = context.VariableInfo(target); var shape = spec.Shape;
            if (spec.ReadOnly || spec.Scope == VariableScope.Character && shape.Length != 1) throw new ScriptException("REFERENCE", "VARSETEX 不支持常量或多维角色数组");
            EraValue value = a[1].Value.ConvertTo(spec.Kind);
            int width = shape[^1], start = unchecked((int)a[3].Value.AsInt()), end = a[4].IsOmitted ? shape.Length == 3 ? 0 : width : unchecked((int)a[4].Value.AsInt());
            bool all = a[2].Value.AsInt() != 0 && spec.Kind != ValueKind.String;
            var indices = a.Skip(5).Select(v => v.Value.AsInt()).ToArray();
            if (shape.Length > 1)
            {
                if (indices.Length != shape.Length) throw new ScriptException("RANK", "VARSETEX 下标维数不匹配");
                start = Math.Max(start, unchecked((int)indices[^1]));
            }
            if (start >= end) return new(1L);
            if (start < 0 || end > width) throw new ScriptException("BOUNDS", "VARSETEX 填充范围越界");
            if (spec.Scope == VariableScope.Character)
            {
                for (int column = start; column < end; column++) context.Write(target with { Index = column }, value);
                return new(1L);
            }
            int firstRow = 0;
            if (shape.Length > 1 && !all)
            {
                if (shape.Length == 3)
                {
                    if (indices[1] < 0 || indices[1] >= shape[0] || indices[0] < 0 || indices[0] >= shape[1]) throw new ScriptException("BOUNDS", "VARSETEX 三维下标越界");
                    firstRow = checked((int)(indices[1] * shape[1] + indices[0]));
                }
                else
                {
                    if (indices[0] < 0 || indices[0] >= shape[0]) throw new ScriptException("BOUNDS", "VARSETEX 行下标越界");
                    firstRow = (int)indices[0];
                }
            }
            int rows = shape.Length > 1 && all ? spec.Length / width : 1;
            for (int row = firstRow; row < firstRow + rows; row++)
                for (int column = start; column < end; column++) context.Write(target with { Index = row * width + column }, value);
            return new(1L);
        }
    }
}
