using System.Threading.Channels;
using mEmuera.Contracts;

namespace mEmuera.Runtime.Scheduling;

public sealed class SessionHost(IGameSession session, IServiceDispatcher services, TimeProvider? time = null) : IAsyncDisposable
{
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly Channel<SessionMessage> queue = Channel.CreateUnbounded<SessionMessage>(new() { SingleReader = true });
    private readonly CancellationTokenSource cancellation = new();
    private Task? worker;
    private Task? pending;
    private Task? animation;
    private long nextAnimation;
    private int animationInterval;
    private long dispatched;
    private bool disposed;
    private readonly object inbox = new();
    public event Action<SessionSnapshot>? Changed;
    public event Action<ApplyResult>? MessageApplied;
    public static long Now(TimeProvider provider) => (long)(provider.GetTimestamp() * (1000.0 / provider.TimestampFrequency));
    /// <summary>启动宿主循环。给出 scheduler 时循环在它上面运行，循环里的 await（引擎没有 ConfigureAwait(false)）也回到它上面。</summary>
    public void Start(TaskScheduler? scheduler = null) => worker = scheduler is null ? Task.Run(RunAsync)
        : Task.Factory.StartNew(RunAsync, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler).Unwrap();
    public void Reply(InputRequest request, string text = "", long? button = null, InputPointerInfo? pointer = null)
        => Post(new InputReply(request.Epoch, request.Id, text, button, Now(time), button is null && pointer is null ? "keyboard" : "mouse") { Pointer = pointer });
    public void Post(SessionMessage message)
    {
        lock (inbox)
        {
            if (message is InputReply reply) message = reply with { ReceivedAt = Now(time) };
            queue.Writer.TryWrite(message);
        }
    }
    private async Task RunAsync()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                while (queue.Reader.TryRead(out var message))
                { var applied = session.Apply(message); MessageApplied?.Invoke(applied); }
                session.Apply(new TimeoutMessage(Now(time)));
                var step = session.Step(new(Cancellation: cancellation.Token));
                var snapshot = session.InspectPresentation(); Changed?.Invoke(snapshot);
                if (step.Reason is StopReason.Completed or StopReason.Cancelled or StopReason.Faulted) break;
                if (step.Service is { } request && request.Id != dispatched)
                { dispatched = request.Id; pending = DispatchAsync(request); }
                if (step.Reason == StopReason.Yielded) { await Task.Yield(); continue; }
                string[] sources = step.Reason == StopReason.WaitingInput && snapshot.Document.AnimationInterval > 0
                    ? AnimatedSources(snapshot.Document.Lines).Concat(snapshot.Document.ImageLayers.Where(l => l.Image.Animated && l.Source is not null).Select(l => l.Source!))
                        .Concat(snapshot.Document.BackgroundLayers.Where(l => l.Animated).SelectMany(l => new[] { l.Source, l.SelectedSource }).OfType<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                int interval = sources.Length > 0 && step.Reason == StopReason.WaitingInput ? snapshot.Document.AnimationInterval : 0;
                if (interval != animationInterval) { animationInterval = interval; nextAnimation = Now(time) + interval; }
                if (interval > 0 && Now(time) >= nextAnimation && (animation is null || animation.IsCompleted))
                {
                    nextAnimation = Now(time) + interval;
                    animation = RefreshAnimationAsync(snapshot, sources);
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                Task ready = queue.Reader.WaitToReadAsync(wait.Token).AsTask();
                long delay = snapshot.Input?.Deadline is { } at ? Math.Max(0, Math.Min(at - Now(time), snapshot.Input.DisplayTime ? 1000 : long.MaxValue)) : long.MaxValue;
                if (interval > 0) delay = Math.Min(delay, animation is { IsCompleted: false } ? interval : Math.Max(0, nextAnimation - Now(time)));
                Task deadline = delay == long.MaxValue ? Task.Delay(Timeout.Infinite, wait.Token) : Task.Delay(TimeSpan.FromMilliseconds(delay), time, wait.Token);
                await Task.WhenAny(ready, deadline); await wait.CancelAsync();
            }
        }
        catch (OperationCanceledException) { }
        finally { session.Apply(new CancelMessage()); Changed?.Invoke(session.InspectPresentation()); }
    }
    private static IEnumerable<string> AnimatedSources(IEnumerable<LogicalLine> lines)
    {
        foreach (var run in lines.SelectMany(l => l.Runs))
        {
            if (run.Image is { Image.Animated: true, Source: { } source }) yield return source;
            if (run.Image is { SelectedImage.Animated: true, SelectedSource: { } selected }) yield return selected;
            if (run.Panel is { } panel) foreach (string nested in AnimatedSources(panel.Lines)) yield return nested;
        }
    }
    private async Task RefreshAnimationAsync(SessionSnapshot snapshot, string[] sources)
    {
        try
        {
            string html = string.Concat(sources.Select(source => new System.Xml.Linq.XElement("img", new System.Xml.Linq.XAttribute("src", source)).ToString()));
            var result = await services.ExecuteAsync(new(snapshot.Epoch, -1, ServiceKind.Resource, new HtmlResourcePayload(html, true)), cancellation.Token);
            var error = result.Error ?? (result.Data is HtmlResourceData ? null : new Diagnostic("ANIMATION_SERVICE", "动画服务返回了错误类型"));
            queue.Writer.TryWrite(new AnimationFrames(snapshot.Epoch, snapshot.Document.Revision, result.Data as HtmlResourceData, error) { InputRequestId = snapshot.Input!.Id });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        { queue.Writer.TryWrite(new AnimationFrames(snapshot.Epoch, snapshot.Document.Revision, null, new("ANIMATION_SERVICE", e.Message)) { InputRequestId = snapshot.Input!.Id }); }
    }
    private async Task DispatchAsync(ServiceRequest request)
    {
        ServiceResult result;
        try { result = await services.ExecuteAsync(request, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception e) when (e is IOException or ScriptException or InvalidOperationException)
        { result = new(request.Epoch, request.Id, null, new("SERVICE", e.Message)); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { result = new(request.Epoch, request.Id, null, new("SERVICE_EXCEPTION", e.GetType().Name + ": " + e.Message)); }
        queue.Writer.TryWrite(result);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await cancellation.CancelAsync(); queue.Writer.TryComplete();
        if (worker is not null) await worker;
        if (pending is not null) await pending;
        if (animation is not null) await animation;
        cancellation.Dispose();
    }
}
