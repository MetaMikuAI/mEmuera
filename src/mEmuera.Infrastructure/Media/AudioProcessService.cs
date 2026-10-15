using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using mEmuera.Contracts;
using mEmuera.Infrastructure.FileSystem;

namespace mEmuera.Infrastructure.Media;

public sealed class AudioProcessService(GamePathResolver paths, string player = "ffplay", string? audioDriver = null) : IMediaService
{
    private sealed class Channel : AudioChannelState
    {
        public Process? Process;
        public string Path = "";
        public long Repeat = 1;
        public int Volume = 100;
        public bool Playing => Process is { HasExited: false };
    }
    private readonly SemaphoreSlim gate = new(1);
    private readonly Channel[] sounds = Enumerable.Range(0, 10).Select(_ => new Channel()).ToArray();
    private readonly Channel background = new();
    private bool disposed;
    public int ActivePlayers => (background.Playing ? 1 : 0) + sounds.Count(c => c.Playing);
    public async ValueTask<MediaInfoData> InfoAsync(int channel, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            var target = channel == -1 ? background : channel is >= 0 and < 10 ? sounds[channel] : null;
            return target is null ? new(false, 0, 0, false, 0) : new(true, target.Duration,
                target.Process is { HasExited: true, ExitCode: 0 } ? target.Duration : Math.Clamp(Volatile.Read(ref target.Position), 0, target.Duration), target.Playing, target.Volume);
        }
        finally { gate.Release(); }
    }
    public async ValueTask ExecuteAsync(MediaPayload request, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (request.Operation is MediaOperation.StopBgm or MediaOperation.StopSound)
            {
                bool stoppingBackground = request.Operation == MediaOperation.StopBgm;
                foreach (var channel in stoppingBackground ? [background] : sounds)
                    if (channel.StopRequested(stoppingBackground, channel.Playing)) await Stop(channel, clearDuration: false);
                return;
            }
            if (request.Operation is MediaOperation.SoundVolume or MediaOperation.BgmVolume)
            {
                foreach (var channel in request.Operation == MediaOperation.BgmVolume ? [background] : sounds)
                {
                    int volume = (int)Math.Clamp(request.Repeat, 0, 100);
                    if (channel.Volume == volume) continue;
                    channel.Volume = volume;
                    if (channel.Playing)
                    {
                        long position = Math.Clamp(Volatile.Read(ref channel.Position), 0, Math.Max(0, channel.Duration - 1));
                        await Stop(channel, clearDuration: false);
                        await Start(channel, position, cancellation);
                    }
                }
                return;
            }
            if (request.Operation is not (MediaOperation.PlayBgm or MediaOperation.PlaySound)) throw new ScriptException("MEDIA_OPERATION", "未知媒体操作");
            string path = paths.Source("sound/" + request.File.Replace('\\', '/'));
            if (!File.Exists(path)) return;
            long duration = await Probe(path, cancellation);
            var target = request.Operation == MediaOperation.PlayBgm ? background : sounds.FirstOrDefault(c => !c.Playing) ?? sounds[0];
            await Stop(target);
            target.Path = path; target.Duration = duration;
            target.Repeat = request.Operation == MediaOperation.PlayBgm ? 0 : Math.Max(1, request.Repeat);
            await Start(target, 0, cancellation);
        }
        finally { gate.Release(); }
    }
    private async Task<long> Probe(string path, CancellationToken cancellation)
    {
        string probe = Path.Combine(Path.GetDirectoryName(player) ?? "", OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        var start = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path }) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new ScriptException("MEDIA_PROBE", "无法启动音频时长查询"); }
        catch (Win32Exception) { throw new ScriptException("MEDIA_PROBE", "找不到 ffprobe，请与 ffplay 放在同一目录或 PATH 中"); }
        using (process)
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellation);
                var errors = process.StandardError.ReadToEndAsync(cancellation);
                await process.WaitForExitAsync(cancellation);
                string value = await output; await errors;
                if (process.ExitCode != 0 || !double.TryParse(value.Trim(), CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0 || seconds > long.MaxValue / 1000d)
                    throw new ScriptException("MEDIA_PROBE", "无法读取音频时长");
                return (long)(seconds * 1000);
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
    }
    private async Task Start(Channel channel, long position, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (audioDriver is not null) start.Environment["SDL_AUDIODRIVER"] = audioDriver;
        foreach (string argument in new[] { "-nodisp", "-autoexit", "-stats", "-loglevel", "info", "-volume", channel.Volume.ToString(CultureInfo.InvariantCulture),
            "-ss", (position / 1000d).ToString(CultureInfo.InvariantCulture), "-loop", channel.Repeat.ToString(CultureInfo.InvariantCulture), channel.Path }) start.ArgumentList.Add(argument);
        Process active;
        try { active = Process.Start(start) ?? throw new ScriptException("MEDIA_START", "无法启动音频播放器"); }
        catch (Win32Exception) { throw new ScriptException("MEDIA_PLAYER", "找不到可用的 ffplay；请配置音频播放器路径"); }
        channel.Process = active; Volatile.Write(ref channel.Position, position);
        active.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { } text) return;
            string value = text.TrimStart().Split(' ', 2)[0];
            if (double.TryParse(value, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds >= 0 && seconds < long.MaxValue / 1000d)
                Volatile.Write(ref channel.Position, (long)(seconds * 1000));
        };
        active.BeginOutputReadLine(); active.BeginErrorReadLine();
        try
        {
            await Task.Delay(100, cancellation);
            if (active.HasExited && active.ExitCode != 0) throw new ScriptException("MEDIA_PLAY", "播放器无法读取文件或打开音频设备");
        }
        catch { await Stop(channel); throw; }
    }
    private static async Task Stop(Channel channel, bool clearDuration = true)
    {
        if (clearDuration) channel.Release();
        Volatile.Write(ref channel.Position, 0);
        if (channel.Process is not { } process) return;
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        finally { process.Dispose(); channel.Process = null; Volatile.Write(ref channel.Position, 0); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true; await Stop(background);
            foreach (var sound in sounds) await Stop(sound);
        }
        finally { gate.Release(); }
    }
}
