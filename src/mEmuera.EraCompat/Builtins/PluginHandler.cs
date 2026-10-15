using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed class PluginHandler : IBuiltinHandler
{
    public string Id => "era.CALLSHARP";
    public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
    {
        context.Defer(ServiceKind.Extension, new PluginPayload(arguments[0].Value.AsString(), arguments[1..]));
        return null;
    }
}
