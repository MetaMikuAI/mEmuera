using mEmuera.Contracts;
using mEmuera.Infrastructure;
using mEmuera.Infrastructure.FileSystem;
using mEmuera.Infrastructure.Graphics;
using mEmuera.Infrastructure.Media;
using mEmuera.Infrastructure.Plugins;
using mEmuera.Infrastructure.Saves;
using mEmuera.Infrastructure.Sqlite;

namespace mEmuera.Host;

/// <summary>一个游戏的外围服务：存档、数据库、音频、图像、文本文件和插件，以及把它们分发给引擎的 <see cref="Dispatcher"/>。</summary>
public sealed class GameServices : IAsyncDisposable
{
    private readonly HttpClient http = new();
    public GameServices(GameDefinition game, ICompilationService compiler, IGameSource source, SourceManifest manifest, CompileOptions options,
        GamePathResolver paths, IMediaService? media)
    {
        Saves = new JsonSaveStore(paths.DataRoot);
        Database = new SqliteDatabaseService(paths);
        Media = media ?? new SilentMediaService();
        Graphics = new RasterService(paths, source, manifest);
        Dispatcher = new ServiceDispatcher(game.GameId, compiler, Saves, Database, Media, Graphics, new TextFileService(paths, options), new ChatPluginService(http));
    }
    public ISaveStore Saves { get; }
    public IDatabaseService Database { get; }
    public IMediaService Media { get; }
    public IGraphicsService Graphics { get; }
    public IServiceDispatcher Dispatcher { get; }

    public async ValueTask DisposeAsync()
    {
        await Media.DisposeAsync();
        await Graphics.DisposeAsync();
        await Database.DisposeAsync();
        http.Dispose();
    }
}

/// <summary>静音的 <see cref="IMediaService"/>；无头运行和没有音频的平台使用。</summary>
public sealed class SilentMediaService : IMediaService
{
    public ValueTask ExecuteAsync(MediaPayload request, CancellationToken cancellation) => ValueTask.CompletedTask;
    public ValueTask<MediaInfoData> InfoAsync(int channel, CancellationToken cancellation) => throw new ScriptException("MEDIA_INFO", "无音频");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class MediaFactory
{
    /// <summary>桌面默认：通过外部播放器进程（ffplay）播放。</summary>
    public static Func<GamePathResolver, IMediaService> Process(string player = "ffplay", string? audioDriver = null)
        => paths => new AudioProcessService(paths, player, audioDriver);
}
