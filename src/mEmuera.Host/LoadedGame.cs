using System.Collections.Immutable;
using mEmuera.Compiler;
using mEmuera.Contracts;
using mEmuera.EraCompat.Builtins;
using mEmuera.EraCompat.Loading;
using mEmuera.Infrastructure.FileSystem;
using mEmuera.Infrastructure.Saves.Legacy;
using mEmuera.Runtime.Execution;
using mEmuera.Runtime.Scheduling;

namespace mEmuera.Host;

public enum LoadStage { ReadingFiles, Indexing, Bootstrapping }

/// <param name="GamePath">游戏目录。</param>
/// <param name="DataRoot">存档与数据目录，默认必须在游戏目录之外。</param>
public sealed record GameLoadOptions(string GamePath, string DataRoot)
{
    public string Profile { get; init; } = "era-v1";
    public string Encoding { get; init; } = "utf-8";
    public string? GameId { get; init; }
    /// <summary>允许游戏与数据共用根目录，排除 saves 和 diagnostics 的索引。</summary>
    public bool AllowSharedRoot { get; init; }
}

public sealed record GameLoadResult(LoadedGame? Game, ImmutableArray<Diagnostic> Diagnostics);

/// <summary>会话可选的宿主输入；前端按自身能力提供，未提供的用无头默认值。</summary>
public sealed record SessionOptions
{
    public ITextMetrics? Metrics { get; init; }
    public Func<long>? Clock { get; init; }
    public string? Entry { get; init; }
    public Func<int, bool?>? KeyState { get; init; }
    public Func<bool>? InputActive { get; init; }
    public Func<PresentationDocument, long, PointerMessage?>? PointerQuery { get; init; }
}

public sealed record RuntimeOptions
{
    public SessionOptions Session { get; init; } = new();
    /// <summary>音频实现；省略时静音。</summary>
    public Func<GamePathResolver, IMediaService>? Media { get; init; }
}

/// <summary>已编译索引并载入定义的游戏。各前端共用这一步，再按各自的显示与输入方式创建会话。</summary>
public sealed class LoadedGame : IDisposable
{
    private LoadedGame(DirectoryGameSource source, CompilationWorkspace workspace, ProgramIndex index, GameDefinition game, GamePathResolver paths)
    {
        Source = source; Workspace = workspace; Index = index; Game = game; Paths = paths;
        LegacySaves = LegacySaveImporter.Discover(Path.Combine(paths.SourceRoot, index.Options.UseSaveFolder ? "sav" : ""));
    }
    public DirectoryGameSource Source { get; }
    public CompilationWorkspace Workspace { get; }
    public ProgramIndex Index { get; }
    public CompileOptions Options => Index.Options;
    public GameDefinition Game { get; }
    public GamePathResolver Paths { get; }
    /// <summary>游戏目录里发现的旧模拟器存档。</summary>
    public ImmutableArray<LegacySaveFile> LegacySaves { get; }

    /// <summary>索引失败时返回诊断信息而不抛出；取消和 IO 错误仍然抛出。</summary>
    /// <param name="scripts">索引阶段每读完一个脚本回调：已完成数、总数。在加载线程上调用，界面应自行节流。</param>
    public static async Task<GameLoadResult> TryLoadAsync(GameLoadOptions options, Action<LoadStage>? progress = null, CancellationToken ct = default, Action<int, int>? scripts = null)
    {
        var paths = new GamePathResolver(options.GamePath, options.DataRoot, options.AllowSharedRoot);
        var source = new DirectoryGameSource(options.GamePath, options.Encoding, scriptSnapshot: options.Profile != "minimal-v1",
            excludeRuntimeDirectories: options.AllowSharedRoot);
        var workspace = new CompilationWorkspace(source, EraBuiltins.Library.Catalog,
            cacheDirectory: paths.Data("cache/index"),
            cacheVersion: $"{typeof(LoadedGame).Module.ModuleVersionId}:{typeof(EraBuiltins).Module.ModuleVersionId}:{typeof(DirectoryGameSource).Module.ModuleVersionId}");
        try
        {
            progress?.Invoke(LoadStage.ReadingFiles);
            var compileOptions = await SystemVariableLoader.PrepareAsync(source, new(Profile: options.Profile, Encoding: options.Encoding), ct);
            progress?.Invoke(LoadStage.Indexing);
            var indexed = await workspace.IndexAsync(source, compileOptions, scripts, ct);
            if (!indexed.Success) { workspace.Dispose(); return new(null, indexed.Diagnostics); }
            progress?.Invoke(LoadStage.Bootstrapping);
            var game = await GameBootstrapLoader.LoadAsync(source, indexed.Index, options.GameId, ct);
            return new(new LoadedGame(source, workspace, indexed.Index, game, paths), []);
        }
        catch { workspace.Dispose(); throw; }
    }

    /// <summary>索引失败时抛出 <see cref="ScriptException"/>（第一条诊断）。</summary>
    public static async Task<LoadedGame> LoadAsync(GameLoadOptions options, Action<LoadStage>? progress = null, CancellationToken ct = default, Action<int, int>? scripts = null)
    {
        var result = await TryLoadAsync(options, progress, ct, scripts);
        if (result.Game is { } game) return game;
        var first = result.Diagnostics.FirstOrDefault() ?? new Diagnostic("LOAD", "游戏加载失败");
        throw new ScriptException(first.Code, first.Message, first.Span);
    }

    public GameServices CreateServices(Func<GamePathResolver, IMediaService>? media = null)
        => new(Game, Workspace, Source, Index.Manifest, Options, Paths, media?.Invoke(Paths));

    public GameSession CreateSession(SessionOptions? options = null)
    {
        options ??= new();
        return new GameSession(options.Entry is { } entry ? Game with { Entry = entry } : Game, EraBuiltins.Library,
            options.Clock ?? (() => SessionHost.Now(TimeProvider.System)), options.Metrics, options.KeyState, options.InputActive, options.PointerQuery);
    }

    /// <summary>服务、会话和后台宿主一起创建；调用 <see cref="GameRuntime.Host"/> 的 Start 开始运行。</summary>
    public GameRuntime CreateRuntime(RuntimeOptions? options = null)
    {
        options ??= new();
        var session = CreateSession(options.Session);
        var services = CreateServices(options.Media);
        return new GameRuntime(session, new SessionHost(session, services.Dispatcher), services);
    }

    /// <summary>把发现的旧档转换进新存档；原文件只读，已有槽位不覆盖。</summary>
    public Task<ImmutableArray<LegacyImportResult>> ImportLegacySavesAsync(ISaveStore saves)
        => LegacySaveImporter.ImportAllAsync(LegacySaves, Game, saves, new GameSession(Game, EraBuiltins.Library).ValidateSave);

    public void Dispose() => Workspace.Dispose();
}
