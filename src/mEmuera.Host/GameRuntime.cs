using mEmuera.Runtime.Execution;
using mEmuera.Runtime.Scheduling;

namespace mEmuera.Host;

/// <summary>一次运行中的游戏：会话、后台宿主和它们用到的服务。释放时先停宿主，再释放服务。</summary>
public sealed class GameRuntime(GameSession session, SessionHost host, GameServices services) : IAsyncDisposable
{
    public GameSession Session { get; } = session;
    public SessionHost Host { get; } = host;
    public GameServices Services { get; } = services;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Services.DisposeAsync();
    }
}
