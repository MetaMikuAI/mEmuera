using System.Collections.Immutable;

namespace mEmuera.Contracts;

public sealed record PluginPayload(string Method, ImmutableArray<BuiltinArgument> Arguments) : ServicePayload;
public sealed record PluginData(ImmutableArray<EraValue> Values) : ServiceData;
public interface IPluginService
{
    ValueTask<PluginData> ExecuteAsync(PluginPayload request, CancellationToken cancellation);
}
