using System.Collections.Immutable;

namespace mEmuera.Contracts;

public enum CharacterFileOperation { Save, Load, Check, Find }
public sealed record CharacterFilePayload(CharacterFileOperation Operation, string Name, string Profile, SaveSnapshot? Snapshot = null) : ServicePayload
{
    public SaveCompatibility? Compatibility { get; init; }
}
public sealed record CharacterFileData(int Status, string Description = "", SaveSnapshot? Snapshot = null,
    ImmutableArray<string> Names = default) : ServiceData;
