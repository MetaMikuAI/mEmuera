namespace mEmuera.Cells;

public sealed class LatestUpdate<T>(Action<Action> schedule, Action<T> consume) : IDisposable where T : class
{
    private readonly object gate = new();
    private T? latest;
    private bool queued, disposed;
    public void Post(T value)
    {
        lock (gate)
        {
            if (disposed) return;
            latest = value;
            if (queued) return;
            queued = true;
        }
        schedule(Drain);
    }
    private void Drain()
    {
        T? value;
        lock (gate) { value = latest; latest = null; queued = false; if (disposed) return; }
        if (value is not null) consume(value);
    }
    public void Dispose() { lock (gate) { disposed = true; latest = null; } }
}
