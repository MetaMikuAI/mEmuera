using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed class RandomHandler : IBuiltinHandler
{
    public string Id => "era.RAND";
    public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> args)
    {
        bool single = args[1].IsOmitted;
        if (args.Any(a => a.Value.Kind == ValueKind.Float))
        {
            double min = single ? 0 : args[0].Value.AsFloat(), max = args[single ? 0 : 1].Value.AsFloat();
            if (max <= min) { context.WarnOnce("RANDOM_RANGE", "随机区间为空，返回下界"); return new(min); }
            return new(context.Random(1L << 53) / (double)(1L << 53) * (max - min) + min);
        }
        long lower = single ? 0 : args[0].Value.AsInt(), upper = args[single ? 0 : 1].Value.AsInt();
        if (upper <= lower) { context.WarnOnce("RANDOM_RANGE", "随机区间为空，返回下界"); return new(lower); }
        return new(unchecked(context.Random(unchecked(upper - lower)) + lower));
    }
}
