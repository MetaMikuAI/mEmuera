using System.Collections.Concurrent;

namespace mEmuera.Host;

/// <summary>
/// 在固定几条大栈线程上执行任务。编译器和常量求值有较深的递归，桌面线程池（Linux 默认 8MB 栈）够用，
/// 但 Android 上 Mono 的线程池线程只有约 1MB 栈且 JIT 栈帧更大，递归到深处时一旦触发 GC 就会越过栈底保护页而原生崩溃。
/// 加载与宿主循环放在这里运行即可；栈只是预留的地址空间，用到多少才占多少内存。
/// </summary>
public sealed class LargeStackScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> queue = [];
    private readonly Thread[] threads;

    public LargeStackScheduler(int threadCount = 2, int stackBytes = 64 << 20, string name = "mEmuera engine")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);
        threads = Enumerable.Range(0, threadCount).Select(i => new Thread(Run, stackBytes) { IsBackground = true, Name = $"{name} {i + 1}" }).ToArray();
        foreach (var thread in threads) thread.Start();
    }

    public override int MaximumConcurrencyLevel => threads.Length;
    /// <summary>当前线程是否属于这个调度器。</summary>
    public bool IsCurrentThread => threads.Contains(Thread.CurrentThread);

    private void Run()
    {
        foreach (var task in queue.GetConsumingEnumerable()) TryExecuteTask(task);
    }

    protected override void QueueTask(Task task) => queue.Add(task);
    // 只在自己的线程上内联，免得把任务拉回小栈线程。
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => IsCurrentThread && TryExecuteTask(task);
    protected override IEnumerable<Task> GetScheduledTasks() => queue.ToArray();

    public void Dispose() => queue.CompleteAdding();
}
