namespace mEmuera.Contracts;

public enum MediaOperation { PlayBgm, PlaySound, StopBgm, StopSound, SoundVolume, BgmVolume }
public sealed record MediaPayload(MediaOperation Operation, string File = "", long Repeat = 1) : ServicePayload;
public sealed record MediaInfoPayload(int Channel, int? Field) : ServicePayload;
public sealed record MediaInfoData(bool Valid, long Duration, long Position, bool Playing, int Volume, int Speed = 100) : ServiceData;
public sealed record MediaCompletedData : ServiceData;
public interface IMediaService : IAsyncDisposable
{
    ValueTask ExecuteAsync(MediaPayload request, CancellationToken cancellation);
    ValueTask<MediaInfoData> InfoAsync(int channel, CancellationToken cancellation) => throw new ScriptException("MEDIA_INFO", "音频状态查询不可用");
}
