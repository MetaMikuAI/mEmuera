using System.Globalization;
using Android.App;
using Android.Content;

namespace mEmuera.App.Android;

/// <summary>
/// 性能小窗的数据：进程实际内存（/proc/self/status 的 VmRSS 与峰值 VmHWM）、.NET 托管堆、Java 堆、原生堆、
/// 系统可用内存、进程 CPU 占用（/proc/self/stat，按单核计，可超过 100%）与线程数。每次取样都很便宜，适合每秒一次。
/// </summary>
internal sealed class PerfMonitor(Context context)
{
    private readonly ActivityManager? activities = context.GetSystemService(Context.ActivityService) as ActivityManager;
    private long lastCpuTicks = -1, lastSampleAt;

    public string Sample()
    {
        var status = ReadStatus();
        long now = System.Environment.TickCount64;
        string cpu = "—";
        if (ReadCpuTicks() is { } ticks)
        {
            // Android 的时钟节拍固定为每秒 100。
            if (lastCpuTicks >= 0 && now > lastSampleAt) cpu = $"{(ticks - lastCpuTicks) * 1000d / (now - lastSampleAt):F0}%";
            lastCpuTicks = ticks; lastSampleAt = now;
        }
        var system = new ActivityManager.MemoryInfo();
        activities?.GetMemoryInfo(system);
        var java = Java.Lang.Runtime.GetRuntime()!;
        return string.Join('\n',
            $"内存 {Mb(status.Rss)}（峰值 {Mb(status.Peak)}） 系统可用 {Gb(system.AvailMem)}/{Gb(system.TotalMem)}{(system.LowMemory ? " 偏低" : "")}",
            $"托管 {Mb(GC.GetTotalMemory(false))} GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} Java {Mb(java.TotalMemory() - java.FreeMemory())} 原生 {Mb(global::Android.OS.Debug.NativeHeapAllocatedSize)}",
            $"CPU {cpu}（{System.Environment.ProcessorCount} 核） 线程 {status.Threads}");
    }

    private static string Mb(long bytes) => bytes < 0 ? "—" : $"{bytes / 1048576d:F0} MB";
    private static string Gb(long bytes) => $"{bytes / 1073741824d:F1} GB";

    private static (long Rss, long Peak, string Threads) ReadStatus()
    {
        long rss = -1, peak = -1; string threads = "—";
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal)) rss = Kilobytes(line) * 1024;
                else if (line.StartsWith("VmHWM:", StringComparison.Ordinal)) peak = Kilobytes(line) * 1024;
                else if (line.StartsWith("Threads:", StringComparison.Ordinal)) threads = line[8..].Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (rss, peak, threads);
    }

    private static long Kilobytes(string line)
        => long.TryParse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb) ? kb : -1;

    // 第 14、15 个字段是用户态与内核态节拍；进程名在括号里且可能含空格，从最后一个右括号之后数。
    private static long? ReadCpuTicks()
    {
        try
        {
            string stat = File.ReadAllText("/proc/self/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            return long.Parse(fields[11], CultureInfo.InvariantCulture) + long.Parse(fields[12], CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { return null; }
    }
}
