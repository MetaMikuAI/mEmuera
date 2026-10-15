using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed class TimesHandler : IBuiltinHandler
{
    public string Id => "era.TIMES";
    public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
    {
        var reference = a[0].Reference!; var value = context.Read(reference); double multiplier = a[1].Value.AsFloat();
        if (value.Kind == ValueKind.Float) { context.Write(reference, new(value.Float * multiplier)); return null; }
        long result;
        if (context.LegacyTimes)
        {
            double product = value.AsInt() * multiplier;
            try { result = checked((long)product); }
            catch (OverflowException) { context.WarnOnce("TIMES_OVERFLOW", "TIMES 整数溢出，钳制到边界"); result = product > 0 ? long.MaxValue : long.MinValue; }
        }
        else
        {
            decimal product;
            try { product = value.AsInt() * (decimal)multiplier; }
            catch (OverflowException) { throw new ScriptException("TIMES_OVERFLOW", "TIMES 超出 decimal 范围"); }
            if (product > long.MaxValue || product < long.MinValue)
            { context.WarnOnce("TIMES_OVERFLOW", "TIMES 整数溢出，钳制到边界"); result = product > 0 ? long.MaxValue : long.MinValue; }
            else result = (long)product;
        }
        context.Write(reference, new(result)); return null;
    }
}
