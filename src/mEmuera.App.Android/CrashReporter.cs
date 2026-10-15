using System.Text;
using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.OS;

namespace mEmuera.App.Android;

/// <summary>
/// 没有 adb 也能知道为什么退出：托管层未处理的异常写成 crash-*.txt；游戏运行时每 2 秒把现场（内存、引擎状态、最近执行的脚本位置）
/// 覆盖写进 session.txt，进程被系统直接结束时它就是最后的记录；下次启动时用 ApplicationExitInfo（Android 11+）取系统记录的退出原因，
/// 再读本应用自己能看到的 logcat，与上次的现场合成 crash-report-*.txt。
/// </summary>
internal sealed partial class CrashReporter : IDisposable
{
    private const string Running = "状态: 运行中";
    private readonly Context context;
    private readonly string version;
    private readonly PerfMonitor monitor;
    private readonly Timer timer;
    private Func<string?>? scene;

    public CrashReporter(Context context, string directory, string version)
    {
        this.context = context; this.version = version; Directory = directory;
        monitor = new PerfMonitor(context);
        try { System.IO.Directory.CreateDirectory(directory); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        timer = new Timer(_ => WriteSession(Running), null, Timeout.Infinite, Timeout.Infinite);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash("托管异常（未处理）", e.ExceptionObject as Exception);
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => WriteCrash("界面线程异常", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => WriteCrash("后台任务异常（未观察）", e.Exception);
    }

    public string Directory { get; }
    private string SessionPath => Path.Combine(Directory, "session.txt");

    /// <summary>开始记录现场；scene 在计时线程上调用，只能读不可变数据。</summary>
    public void Begin(Func<string?> scene)
    {
        this.scene = scene;
        WriteSession(Running);
        timer.Change(2000, 2000);
    }

    public void End()
    {
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        if (scene is null) return;
        WriteSession("状态: 正常结束");
        scene = null;
    }

    private string Header(string status) => $"{status}\n时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n版本: {version}\n"
        + $"设备: {Build.Manufacturer} {Build.Model}，Android {Build.VERSION.Release}（API {(int)Build.VERSION.SdkInt}），{System.Environment.ProcessorCount} 核\n进程: {Process.MyPid()}\n\n";

    private string Scene()
    {
        string sample;
        lock (monitor) sample = monitor.Sample();
        string? detail;
        try { detail = scene?.Invoke(); }
        catch (Exception e) when (e is not OutOfMemoryException) { detail = "现场读取失败：" + e.Message; }
        return sample + "\n\n" + detail;
    }

    private void WriteSession(string status)
    {
        try { File.WriteAllText(SessionPath, Header(status) + Scene()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void WriteCrash(string kind, Exception? error)
    {
        try { File.WriteAllText(Path.Combine(Directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), Header("崩溃: " + kind) + Scene() + "\n\n" + error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 在后台检查上次运行是否异常退出，是则合成报告并回调摘要与路径（在后台线程调用）。诊断本身不能把应用弄崩：
    /// 开始前同步写下“正在收集”，若收集途中进程没了（原生崩溃捕获不到），下次启动直接跳过；其余异常都兜住并写进报告。
    /// </summary>
    public void CollectPreviousExitInBackground(ISharedPreferences preferences, Action<string, string> report)
    {
        if (preferences.GetBoolean("collectingExit", false))
        {
            preferences.Edit()!.PutBoolean("collectingExit", false)!.Commit();
            report("上次启动时收集诊断信息失败，已跳过。", "");
            return;
        }
        preferences.Edit()!.PutBoolean("collectingExit", true)!.Commit();
        Task.Run(() =>
        {
            (string Summary, string Path)? result;
            try { result = CollectPreviousExit(preferences); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                string path = Path.Combine(Directory, $"crash-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                try { File.WriteAllText(path, $"收集诊断信息时出错（版本 {version}）\n\n{e}"); } catch (Exception) { path = ""; }
                result = ("收集诊断信息时出错：" + e.GetType().Name + "：" + e.Message, path);
            }
            finally { preferences.Edit()!.PutBoolean("collectingExit", false)!.Commit(); }
            if (result is { } found) report(found.Summary, found.Path);
        });
    }

    private (string Summary, string Path)? CollectPreviousExit(ISharedPreferences preferences)
    {
        string? session = null;
        try { if (File.Exists(SessionPath)) session = File.ReadAllText(SessionPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        bool interrupted = session?.StartsWith(Running, StringComparison.Ordinal) == true;
        string? summary = interrupted ? "上次运行没有正常结束，系统未提供原因" : null;
        var report = new StringBuilder();
        int? pid = null;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && SystemExit(preferences, interrupted, report) is { } exit)
        { summary = exit.Summary; pid = exit.Pid; }
        if (summary is null) return null;
        report.Insert(0, $"mEmuera 异常退出报告（生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}，当前版本 {version}）\n摘要: {summary}\n\n");
        report.AppendLine("== 上次运行的最后现场（session.txt）==").AppendLine(session ?? "（没有记录）").AppendLine();
        report.AppendLine("== logcat（本应用可见部分）==").AppendLine(Logcat(pid));
        foreach (string crash in SafeFiles("crash-2*.txt"))
        {
            try { if (File.GetLastWriteTime(crash) > DateTime.Now.AddDays(-1)) report.AppendLine($"== {Path.GetFileName(crash)} ==").AppendLine(File.ReadAllText(crash)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        string path = Path.Combine(Directory, $"crash-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            File.WriteAllText(path, report.ToString());
            if (File.Exists(SessionPath)) File.Move(SessionPath, Path.Combine(Directory, "session-previous.txt"), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (summary + "（报告写入失败：" + e.Message + "）", ""); }
        return (summary, path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private (string Summary, int Pid)? SystemExit(ISharedPreferences preferences, bool interrupted, StringBuilder report)
    {
        if ((context.GetSystemService(Context.ActivityService) as ActivityManager)?.GetHistoricalProcessExitReasons(context.PackageName, 0, 10) is not { } exits) return null;
        // 记录跨升级保留；升级本身也算一次退出，所以多取几条，挑最近一次异常的。
        long seen = preferences.GetLong("lastExitSeen", 0);
        var fresh = exits.Where(e => e.Timestamp > seen).OrderByDescending(e => e.Timestamp).ToList();
        // 同步写入：后面若出错退出，下次不会再处理同一条记录。
        if (fresh.Count > 0) preferences.Edit()!.PutLong("lastExitSeen", fresh[0].Timestamp)!.Commit();
        // 自己退出、用户划掉、权限变更（授予文件权限会重启进程）、应用更新都不算异常。
        var exit = fresh.FirstOrDefault(e => (int)e.Reason is not (1 or 8 or 10 or 11 or 15 or 16)) ?? (interrupted ? fresh.FirstOrDefault() : null);
        if (exit is null) return null;
        int reason = (int)exit.Reason;
        string summary = Describe(reason, exit.Status);
        report.AppendLine("== 系统记录的退出原因 ==")
            .AppendLine($"原因: {summary}（reason={reason}, status={exit.Status}）")
            .AppendLine($"说明: {exit.Description}")
            .AppendLine($"时间: {DateTimeOffset.FromUnixTimeMilliseconds(exit.Timestamp).ToLocalTime():yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"退出时内存: PSS {exit.Pss / 1024} MB，RSS {exit.Rss / 1024} MB；前后台: {exit.Importance}")
            .AppendLine($"进程: {exit.Pid} {exit.ProcessName}").AppendLine();
        if (Trace(exit) is { Length: > 0 } trace) report.AppendLine("== 系统保存的堆栈 ==").AppendLine(trace).AppendLine();
        report.AppendLine("== 近期全部退出记录 ==");
        foreach (var e in fresh)
            report.AppendLine($"{DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp).ToLocalTime():MM-dd HH:mm:ss} reason={(int)e.Reason} status={e.Status} PSS {e.Pss / 1024} MB {e.Description}");
        report.AppendLine();
        return (summary, exit.Pid);
    }

    private static string Describe(int reason, int status) => reason switch
    {
        2 => $"被信号 {status} 结束" + (status == 9 ? "（多为系统回收内存）" : ""),
        3 => "内存不足，被系统结束",
        4 => "Java 层崩溃",
        5 => $"原生代码崩溃（信号 {status}{(status == 6 ? "，常见于 .NET 运行时中止，如栈溢出" : status == 11 ? "，内存访问错误" : "")}）",
        6 => "应用无响应（ANR）",
        7 => "初始化失败",
        9 => "资源占用过高，被系统结束",
        14 => "被系统冻结后结束",
        _ => $"其他原因（{reason}）"
    };

    // ANR 的堆栈是文本；原生崩溃（Android 12+）是 protobuf 格式的 tombstone，取出其中可读的字符串即可看出调用栈。
    [System.Runtime.Versioning.SupportedOSPlatform("android30.0")]
    private static string? Trace(ApplicationExitInfo exit)
    {
        try
        {
            using var stream = exit.TraceInputStream;
            if (stream is null) return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            string text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)Math.Min(buffer.Length, 4 << 20));
            if ((int)exit.Reason == 6) return text.Length > 200_000 ? text[..200_000] : text;
            return string.Join('\n', Printable().Matches(text).Select(m => m.Value).Take(600));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return "读取失败：" + e; }
    }

    [GeneratedRegex(@"[\x20-\x7e]{6,}")]
    private static partial Regex Printable();

    // 应用无需权限就能读到同一用户 ID 的日志，包括上一个进程的；按上次进程号筛选，没有进程号时取运行时相关的标签。
    private static string Logcat(int? pid)
    {
        try
        {
            var process = Java.Lang.Runtime.GetRuntime()!.Exec(["logcat", "-d", "-v", "time", "-t", "5000"])!;
            using var reader = new StreamReader(process.InputStream!);
            var lines = new List<string>();
            var mine = pid is { } id ? new Regex($@"\(\s*{id}\)") : null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
                if (mine?.IsMatch(line) ?? Regex.IsMatch(line, @"mono|monodroid|DOTNET|libc|DEBUG|AndroidRuntime|lowmemorykiller|mEmuera", RegexOptions.IgnoreCase))
                    lines.Add(line);
            process.WaitFor();
            return lines.Count == 0 ? "（没有读到相关日志）" : string.Join('\n', lines.TakeLast(800));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return "读取 logcat 失败：" + e; }
    }

    private IEnumerable<string> SafeFiles(string pattern)
    {
        try { return System.IO.Directory.GetFiles(Directory, pattern); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public void Dispose() => timer.Dispose();
}
