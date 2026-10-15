using mEmuera.Contracts;
using mEmuera.Infrastructure.FileSystem;
using mEmuera.Infrastructure.Media;

internal static class MediaProbe
{
    public static async Task<int> RunAsync(string root, string player)
    {
        root = Path.GetFullPath(root);
        string source = Path.Combine(root, "source"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(source, "sound"));
        using (var file = new BinaryWriter(File.Create(Path.Combine(source, "sound", "silence.wav"))))
        {
            file.Write("RIFF"u8); file.Write(16036); file.Write("WAVEfmt "u8); file.Write(16);
            file.Write((short)1); file.Write((short)1); file.Write(8000); file.Write(16000);
            file.Write((short)2); file.Write((short)16); file.Write("data"u8); file.Write(16000); file.Write(new byte[16000]);
        }
        await using var media = new AudioProcessService(new(source, data), player, "dummy");
        await media.ExecuteAsync(new(MediaOperation.PlayBgm, "silence.wav"), default);
        await media.ExecuteAsync(new(MediaOperation.PlaySound, "silence.wav", 100), default);
        if (media.ActivePlayers != 2) throw new ScriptException("MEDIA_PROBE", "未启动两个真实播放进程");
        var info = await media.InfoAsync(0, default);
        if (info.Duration != 1000 || !info.Playing || info.Volume != 100) throw new ScriptException("MEDIA_PROBE", "音频元数据与通道状态不符");
        for (int i = 0; i < 60 && (await media.InfoAsync(0, default)).Position <= 0; i++) await Task.Delay(50);
        if ((await media.InfoAsync(0, default)).Position <= 0) throw new ScriptException("MEDIA_PROBE", "没有收到播放器进度");
        await media.ExecuteAsync(new(MediaOperation.SoundVolume, Repeat: 37), default);
        info = await media.InfoAsync(0, default);
        if (info.Volume != 37 || !info.Playing || media.ActivePlayers != 2) throw new ScriptException("MEDIA_PROBE", "调整音量后通道状态错误");
        await media.ExecuteAsync(new(MediaOperation.StopBgm), default);
        if (media.ActivePlayers != 1) throw new ScriptException("MEDIA_PROBE", "BGM 停止范围错误");
        await media.ExecuteAsync(new(MediaOperation.StopSound), default);
        if (media.ActivePlayers != 0) throw new ScriptException("MEDIA_PROBE", "播放进程未释放");
        Console.WriteLine("media: real ffprobe duration and ffplay progress/volume restart/stop passed; SDL dummy output; physical audio unverified");
        return 0;
    }
}
