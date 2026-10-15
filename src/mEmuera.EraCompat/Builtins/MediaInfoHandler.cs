using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed class MediaInfoHandler : IBuiltinHandler
{
    public string Id => "era.GETSOUNDORBGMINFO";
    public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
    {
        context.Defer(ServiceKind.Media, new MediaInfoPayload(unchecked((int)arguments[0].Value.AsInt()),
            arguments[1].IsOmitted ? null : unchecked((int)arguments[1].Value.AsInt())));
        return null;
    }
}
