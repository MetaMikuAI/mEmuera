using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed record CharacterRangeHandler(string Id) : IBuiltinHandler
{
    public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
    {
        string name = a[0].Value.AsString(); var spec = context.VariableInfo(name);
        if (spec.Scope != VariableScope.Character || spec.Shape.Length != 1 || spec.ReadOnly) throw new ScriptException("REFERENCE", "CVARSET 需要一维可写角色变量");
        int count = checked((int)context.CharacterCount);
        int start = a[3].IsOmitted ? 0 : unchecked((int)a[3].Value.AsInt()), end = a[4].IsOmitted ? count : unchecked((int)a[4].Value.AsInt());
        if (!a[3].IsOmitted && (start < 0 || start >= count) || end < 0 || end > count) throw new ScriptException("CHARACTER_BOUNDS", "CVARSET 角色范围无效");
        if (start > end) (start, end) = (end, start);
        EraValue value = a[2].IsOmitted ? EraValue.Zero(spec.Kind) : a[2].Value;
        if (value.Kind != spec.Kind) throw new ScriptException("TYPE", "CVARSET 值类型不匹配");
        if (count == 0) return null;
        long index = spec.CharacterScalar ? 0 : a[1].Value.Kind == ValueKind.String
            ? spec.NameIndices.GetValueOrDefault(a[1].Value.AsString(), -1) : a[1].Value.AsInt();
        if (index < 0 || index >= spec.Length) throw new ScriptException("BOUNDS", "CVARSET 元素下标越界");
        context.FillCharacters(name, (int)index, value, start, end); return null;
    }
}
