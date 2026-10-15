namespace mEmuera.Contracts;

public sealed record LoadTextPayload(EraValue Path, bool ForceSaveDirectory, bool ForceUtf8) : ServicePayload;
public sealed record SaveTextPayload(string Text, EraValue Path, bool ForceSaveDirectory, bool ForceUtf8) : ServicePayload;
public sealed record SoundExistsPayload(string Path) : ServicePayload;
public interface ITextFileService
{
    ValueTask<string> LoadAsync(LoadTextPayload request, CancellationToken ct);
    ValueTask<long> SaveAsync(SaveTextPayload request, CancellationToken ct);
    ValueTask<long> SoundExistsAsync(string path, CancellationToken ct);
}
