using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed record BitHandler(string Id, string Operation) : IBuiltinHandler
{
    public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
    {
        long bit = a[1].Value.AsInt();
        if (bit < 0 || bit > 63) throw new ScriptException("BIT_RANGE", "位下标必须在 0 到 63 之间");
        var reference = a[0].Reference!; long value = context.Read(reference).AsInt(), mask = 1L << (int)bit;
        context.Write(reference, new(Operation == "SETBIT" ? value | mask : Operation == "CLEARBIT" ? value & ~mask : value ^ mask));
        return null;
    }
}
