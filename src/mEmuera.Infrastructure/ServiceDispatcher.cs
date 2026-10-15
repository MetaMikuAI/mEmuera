using mEmuera.Contracts;

namespace mEmuera.Infrastructure;

public sealed class ServiceDispatcher(string gameId, ICompilationService compiler, ISaveStore saves, IDatabaseService database, IMediaService? media = null, IGraphicsService? graphics = null, ITextFileService? textFiles = null, IPluginService? plugins = null) : IServiceDispatcher
{
    public async ValueTask<ServiceResult> ExecuteAsync(ServiceRequest request, CancellationToken ct)
    {
        ServiceData? data = null; Diagnostic? error = null;
        try
        {
            switch (request.Payload)
            {
                case DynamicVariablePayload dynamicVariable:
                    var dynamicResult = await compiler.CompileVariableAsync(dynamicVariable, ct);
                    if (dynamicResult.Success) data = new DynamicVariableData(dynamicResult.Function!, dynamicVariable.ResultKind);
                    else error = dynamicResult.Diagnostics.FirstOrDefault() ?? new("COMPILE", "动态变量编译失败");
                    break;
                case PluginPayload plugin when plugins is not null:
                    data = await plugins.ExecuteAsync(plugin, ct); break;
                case CompilePayload compile:
                    var result = await compiler.CompileAsync(compile.FunctionId, ct);
                    if (result.Success) data = new CompiledData(result.Function!); else error = result.Diagnostics.FirstOrDefault() ?? new("COMPILE", "编译失败"); break;
                case SavePayload save:
                    var saved = await saves.WriteAsync(new(gameId, save.Slot, save.Scope), save.Snapshot, ct); data = new SavedData(saved.Committed); error = saved.Error; break;
                case DeleteSavePayload delete:
                    await saves.DeleteAsync(new(gameId, delete.Slot), ct); data = new SavedData(true); break;
                case CharacterFilePayload character:
                    if (character.Operation == CharacterFileOperation.Find)
                        data = new CharacterFileData(0, Names: await saves.FindCharactersAsync(gameId, character.Name, ct));
                    else if (character.Operation == CharacterFileOperation.Save)
                    {
                        var write = await saves.WriteAsync(new(gameId, 0, SaveScope.Character, character.Name), character.Snapshot!, ct);
                        data = new SavedData(write.Committed); error = write.Error;
                    }
                    else
                    {
                        var read = await saves.ReadAsync(new(gameId, 0, SaveScope.Character, character.Name), ct);
                        int status = read.Snapshot is { } characterSnapshot
                            ? (character.Compatibility?.CanRead(characterSnapshot) ?? characterSnapshot.CompatibilityProfile == character.Profile) ? 0 : 3
                            : read.Error?.Code == "SAVE_MISSING" ? 1 : 4;
                        data = new CharacterFileData(status, read.Snapshot?.Description ?? (status == 1 ? "----" : "存档损坏"), status == 0 ? read.Snapshot : null);
                    }
                    break;
                case LoadPayload load:
                    var loaded = await saves.ReadAsync(new(gameId, load.Slot, load.Scope), ct);
                    if (loaded.Snapshot is not null) data = new LoadedData(loaded.Snapshot);
                    error = loaded.Error;
                    if (load.Scope == SaveScope.Global && error?.Code == "SAVE_MISSING") { data = new MissingSaveData(); error = null; }
                    break;
                case DatabasePayload db: data = await database.ExecuteAsync(db, ct); break;
                case LoadTextPayload text when textFiles is not null:
                    data = new BuiltinValueData(new(await textFiles.LoadAsync(text, ct))); break;
                case SaveTextPayload text when textFiles is not null:
                    data = new BuiltinValueData(new(await textFiles.SaveAsync(text, ct))); break;
                case SoundExistsPayload sound when textFiles is not null:
                    data = new BuiltinValueData(new(await textFiles.SoundExistsAsync(sound.Path, ct))); break;
                case MediaPayload playback when media is not null:
                    await media.ExecuteAsync(playback, ct); data = new MediaCompletedData(); break;
                case MediaInfoPayload info when media is not null:
                    data = await media.InfoAsync(info.Channel, ct); break;
                case GraphicsPayload drawing when graphics is not null:
                    data = await graphics.ExecuteAsync(drawing, ct); break;
                case HtmlResourcePayload html when graphics is not null:
                    data = await graphics.HtmlAsync(html, ct); break;
                case CheckSavePayload check:
                    var checkedSave = await saves.ReadAsync(new(gameId, check.Slot), ct);
                    data = checkedSave.Snapshot is { } snapshot
                        ? new CheckSaveData((check.Compatibility?.CanRead(snapshot) ?? snapshot.CompatibilityProfile == check.Profile) ? 0 : 3, snapshot.Description, snapshot.GameVersion)
                        : new CheckSaveData(checkedSave.Error?.Code == "SAVE_MISSING" ? 1 : 4, checkedSave.Error?.Code == "SAVE_MISSING" ? "----" : "存档损坏", 0);
                    break;
                case ScriptDatabasePayload db when database is IScriptDatabaseService scriptDatabase:
                    data = new BuiltinValueData(await scriptDatabase.ExecuteScriptAsync(db, ct)); break;
                default: error = new("SERVICE_UNSUPPORTED", "此服务尚未实现: " + request.Kind); break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ScriptException or Microsoft.Data.Sqlite.SqliteException)
        { error = e is ScriptException script ? script.Diagnostic : new("SERVICE_IO", e.Message); }
        return new(request.Epoch, request.Id, data, error);
    }
}
