using mEmuera.Contracts;

namespace mEmuera.Runtime.Execution;

public sealed partial class GameSession
{
    void IBuiltinContext.CharacterFile(CharacterFileOperation operation, string name, string description, IReadOnlyList<long>? indices)
    {
        SaveSnapshot? snapshot = operation == CharacterFileOperation.Save
            ? new SaveSnapshot(2, game.GameId, game.Profile, DateTimeOffset.UtcNow, [], 0)
            {
                Characters = characters.SelectSnapshots(indices ?? []), Description = description,
                CompatibilityFingerprint = SaveCompatibilityPolicy.Fingerprint,
                GameVersion = globals.TryGetValue("GAMEBASE_VERSION", out var version) ? version.Values[0].AsInt() : SaveCompatibilityPolicy.GameVersion
            } : null;
        Request(operation == CharacterFileOperation.Save ? ServiceKind.Save : ServiceKind.Load,
            new CharacterFilePayload(operation, name, game.Profile, snapshot) { Compatibility = SaveCompatibilityPolicy });
    }
    private void ApplyCharacterFile(CharacterFilePayload request, CharacterFileData data)
    {
        switch (request.Operation)
        {
            case CharacterFileOperation.Load:
                bool loaded = data.Status == 0 && data.Snapshot is { } candidate
                    && candidate.GameId == game.GameId && SaveCompatibilityPolicy.CanRead(candidate);
                if (loaded) characters.Append(data.Snapshot!.Characters);
                globals["RESULT"].Values[0] = new(loaded ? 1L : 0L);
                break;
            case CharacterFileOperation.Check:
                Put(new((long)data.Status));
                globals["RESULTS"].Values[0] = new(data.Description);
                pendingBuiltinType = null;
                break;
            case CharacterFileOperation.Find:
                Put(new((long)data.Names.Length));
                for (int i = 0; i < Math.Min(data.Names.Length, globals["RESULTS"].Values.Length); i++)
                    globals["RESULTS"].Values[i] = new(data.Names[i]);
                pendingBuiltinType = null;
                break;
        }
    }
}
