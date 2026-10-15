using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using ColorStateList = Android.Content.Res.ColorStateList;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using mEmuera.Contracts;
using mEmuera.Host;
using mEmuera.Infrastructure.FileSystem;
using AndroidEnvironment = Android.OS.Environment;
using Uri = Android.Net.Uri;
using OperationCanceledException = System.OperationCanceledException;

namespace mEmuera.App.Android;

/// <summary>
/// 唯一的界面：首页 → 加载 → 游戏。游戏和存档放在共享存储的 emuera/，卸载后保留。
/// 旋转、键盘与尺寸变化不重建 Activity，否则正在运行的游戏会丢失。
/// </summary>
[Activity(Label = "mEmuera", MainLauncher = true, Theme = "@android:style/Theme.Material.NoActionBar",
    LaunchMode = LaunchMode.SingleTask, WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density)]
public sealed class MainActivity : Activity
{
    // 加载与引擎都在这里跑：Mono 线程池线程只有约 1MB 栈，深递归时遇上 GC 会原生崩溃（SIGSEGV）。
    private static readonly LargeStackScheduler Engine = new();
    private GamePage? game;
    private CancellationTokenSource? loading;
    private FrameLayout root = null!, content = null!;
    private TextView perfView = null!;
    private LinearLayout perfPanel = null!;
    private FrameTimeGraph frameGraph = null!;
    private PerfMonitor perf = null!;
    private CrashReporter reporter = null!;
    private bool perfTicking;
    private string? loadingInfo;

    private sealed class LoadProgress { public int Stage, Done, Total; }

    private ISharedPreferences Preferences => GetSharedPreferences("memuera", FileCreationMode.Private)!;
    private float FontSize => Preferences.GetFloat("fontSize", Ui.Sp(this, 13));
    private bool PerfEnabled
    {
        get => Preferences.GetBoolean("perf", false);
        set => Preferences.Edit()!.PutBoolean("perf", value)!.Apply();
    }

#pragma warning disable CS0618, CA1422 // 共享存储根目录没有替代接口；有“所有文件访问权限”时直接按路径读写。
    private static string StorageRoot => AndroidEnvironment.ExternalStorageDirectory?.AbsolutePath ?? "/storage/emulated/0";
#pragma warning restore CS0618, CA1422
#pragma warning disable CS0618, CA1422 // GetPackageInfo(string, int) 在 Android 13 起有新重载，旧的仍可用。
    private string Version => PackageManager?.GetPackageInfo(PackageName!, 0)?.VersionName ?? "?";
#pragma warning restore CS0618, CA1422
    private static string GameRoot => Path.Combine(StorageRoot, "emuera");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // 页面换在 content 里；性能小窗浮在右上角，不拦截触摸，切换页面时保留。
        perf = new PerfMonitor(this);
        root = new FrameLayout(this);
        root.SetBackgroundColor(Ui.Background);
        content = new FrameLayout(this);
        root.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        perfView = Ui.Label(this, "", 10);
        perfPanel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        perfPanel.SetBackgroundColor(Ui.Translucent(Ui.SurfaceVariant, 0xD0));
        perfPanel.AddView(perfView);
        frameGraph = new FrameTimeGraph(this) { Visibility = ViewStates.Gone };
        perfPanel.AddView(frameGraph, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 80)));
        perfView.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 4), Ui.Dp(this, 6), Ui.Dp(this, 4));
        var corner = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.End);
        corner.SetMargins(0, Ui.Dp(this, 4), Ui.Dp(this, 4), 0);
        root.AddView(perfPanel, corner);
        root.SetOnApplyWindowInsetsListener(new Insets());
        SetContentView(root);
        SetPerf(PerfEnabled);
        string diagnostics = HasStorageAccess() ? Path.Combine(GameRoot, "diagnostics")
            : Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath, "diagnostics");
        reporter = new CrashReporter(this, diagnostics, Version);
        try { reporter.CollectPreviousExitInBackground(Preferences, (summary, path) => RunOnUiThread(() => ShowExitReport(summary, path))); }
        catch (Exception e) when (e is not OutOfMemoryException) { Toast.MakeText(this, "诊断检查失败：" + e.Message, ToastLength.Long)!.Show(); }
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new BackCallback(this));
    }

    protected override void OnResume()
    {
        base.OnResume();
        // 从系统设置授权回来时刷新目录列表。
        if (game is null && loading is null) ShowPicker();
    }

    protected override void OnDestroy()
    {
        loading?.Cancel();
        reporter.End(); reporter.Dispose();
        if (game is { } running) { game = null; _ = running.DisposeAsync().AsTask(); }
        base.OnDestroy();
    }

#pragma warning disable CS0618, CS0672, CA1422 // Android 13 以前的返回键；13 起走 OnBackInvokedCallback。
    public override void OnBackPressed() => Back();
#pragma warning restore CS0618, CS0672, CA1422

    private void Back()
    {
        if (game is not null) game.ConfirmExit();
        else if (loading is not null) loading.Cancel();
        else Finish();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (game is null && loading is null) ShowPicker();
    }

    private bool HasStorageAccess()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) return AndroidEnvironment.IsExternalStorageManager;
        return CheckSelfPermission(Manifest.Permission.ReadExternalStorage) == Permission.Granted
            && CheckSelfPermission(Manifest.Permission.WriteExternalStorage) == Permission.Granted;
    }

    private void RequestStorageAccess()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        { RequestPermissions([Manifest.Permission.ReadExternalStorage, Manifest.Permission.WriteExternalStorage], 1); return; }
        try { StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission, Uri.Parse("package:" + PackageName))); }
        catch (ActivityNotFoundException) { StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission)); }
    }

    private void Show(View page)
    {
        content.RemoveAllViews();
        content.AddView(page, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        root.RequestApplyInsets();
    }

    private void SetPerf(bool enabled)
    {
        PerfEnabled = enabled;
        perfPanel.Visibility = enabled ? ViewStates.Visible : ViewStates.Gone;
        frameGraph.Recording = enabled;
        frameGraph.Clear();
        if (game is not null) game.PerfVisible = enabled;
        if (enabled && !perfTicking) { perfTicking = true; PerfTick(); }
    }

    private void PerfTick()
    {
        if (!PerfEnabled || IsDestroyed) { perfTicking = false; return; }
        string? extra = game?.Stats() ?? loadingInfo;
        perfView.Text = perf.Sample() + (extra is null ? "" : "\n" + extra);
        frameGraph.Visibility = game is null ? ViewStates.Gone : ViewStates.Visible;
        frameGraph.Invalidate();
        root.PostDelayed(PerfTick, 1000);
    }

    private void ShowPicker()
    {
        frameGraph.Clear();
        frameGraph.Visibility = ViewStates.Gone;
        Window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Ui.Dp(this, 16);
        page.SetPadding(pad, pad, pad, pad);
        page.AddView(Ui.Label(this, "mEmuera", 22, Ui.Accent));
        page.AddView(Ui.Label(this, $"版本 {Version}", 12, Ui.Muted), Ui.Fill(this, 2));
        bool access = HasStorageAccess();
        if (!access)
        {
            page.AddView(Ui.Label(this, "请授权访问游戏与存档。", 14, Ui.Error), Ui.Fill(this, 8));
            var grant = Ui.Button(this, "授予文件访问权限", filled: true);
            grant.Click += (_, _) => RequestStorageAccess();
            page.AddView(grant, Ui.Wrap(this));
        }
        page.AddView(Ui.Label(this, "游戏文件放入 /sdcard/emuera/，存档也保存在其中。", 14), Ui.Fill(this, 8));
        var open = Ui.Button(this, "打开游戏", filled: true);
        open.Click += (_, _) => Open(GameRoot);
        Ui.SetEnabled(open, access);
        page.AddView(open, Ui.Wrap(this));
        var perfSwitch = Ui.Button(this, PerfEnabled ? "性能监测：开" : "性能监测：关");
        perfSwitch.Click += (_, _) => { SetPerf(!PerfEnabled); perfSwitch.Text = PerfEnabled ? "性能监测：开" : "性能监测：关"; };
        page.AddView(perfSwitch, Ui.Wrap(this));
        var github = Ui.Button(this, "GitHub");
        github.Click += (_, _) =>
        {
            try { StartActivity(new Intent(Intent.ActionView, Uri.Parse("https://github.com/MetaMikuAI/mEmuera"))); }
            catch (ActivityNotFoundException) { Toast.MakeText(this, "未找到可打开链接的浏览器", ToastLength.Short)!.Show(); }
        };
        page.AddView(github, Ui.Wrap(this));
        var scroll = new ScrollView(this) { FillViewport = true };
        scroll.SetBackgroundColor(Ui.Background);
        scroll.AddView(page);
        Show(scroll);
    }

    private async void Open(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path.Trim());
        if (!GameLocator.IsGame(path)) { Toast.MakeText(this, "不是游戏目录：需要包含 ERB 和 CSV 目录", ToastLength.Long)!.Show(); return; }
        var cancellation = loading = new CancellationTokenSource();
        var page = new LinearLayout(this) { Orientation = Orientation.Vertical };
        page.SetGravity(GravityFlags.Center);
        page.SetBackgroundColor(Ui.Background);
        page.AddView(Ui.Label(this, "正在加载 " + Path.GetFileName(path), 16, Ui.Accent), Ui.Wrap(this));
        var stage = Ui.Label(this, "第 1/3 步 读取文件与 CSV", 13);
        page.AddView(stage, Ui.Wrap(this));
        var bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000, Indeterminate = true };
        bar.ProgressTintList = bar.IndeterminateTintList = ColorStateList.ValueOf(Ui.Accent);
        var barLayout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        barLayout.SetMargins(Ui.Dp(this, 32), Ui.Dp(this, 8), Ui.Dp(this, 32), Ui.Dp(this, 4));
        page.AddView(bar, barLayout);
        var elapsed = Ui.Label(this, "已用 0 秒", 12, Ui.Muted);
        page.AddView(elapsed, Ui.Wrap(this));
        var cancel = Ui.Button(this, "取消");
        cancel.Click += (_, _) => cancellation.Cancel();
        page.AddView(cancel, Ui.Wrap(this, 12));
        Show(page);
        // 加载线程只写计数，界面每 200 毫秒读一次；逐个脚本回到界面线程会把它堵住。
        var progress = new LoadProgress();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Tick()
        {
            if (loading != cancellation) { loadingInfo = null; return; }
            int step = Volatile.Read(ref progress.Stage), done = Volatile.Read(ref progress.Done), total = Volatile.Read(ref progress.Total);
            bar.Indeterminate = step == (int)LoadStage.ReadingFiles;
            // 桌面上 EraTW 读文件约占一成半、索引约占八成，按这个比例分配进度条。
            bar.Progress = step == (int)LoadStage.Indexing ? 150 + (total > 0 ? 800 * done / total : 0) : step == (int)LoadStage.Bootstrapping ? 960 : 0;
            stage.Text = step switch
            {
                (int)LoadStage.Indexing => $"第 2/3 步 索引脚本 {done}/{total}",
                (int)LoadStage.Bootstrapping => "第 3/3 步 初始化游戏",
                _ => "第 1/3 步 读取文件与 CSV"
            };
            elapsed.Text = $"已用 {clock.Elapsed.TotalSeconds:F0} 秒";
            loadingInfo = $"加载 {stage.Text} · {elapsed.Text}";
            page.PostDelayed(Tick, 200);
        }
        Tick();
        reporter.Begin(() => loadingInfo ?? "加载中");
        var options = new GameLoadOptions(path, path) { AllowSharedRoot = true };
        try
        {
            var loaded = await Task.Factory.StartNew(() => LoadedGame.LoadAsync(options, step => Volatile.Write(ref progress.Stage, (int)step), cancellation.Token,
                (done, total) => { Volatile.Write(ref progress.Total, total); Volatile.Write(ref progress.Done, done); }), cancellation.Token, TaskCreationOptions.None, Engine).Unwrap();
            if (cancellation.IsCancellationRequested) { loaded.Dispose(); throw new OperationCanceledException(); }
            var running = game = new GamePage(this, loaded, FontSize, Version, Engine);
            running.Exited += () => _ = ExitAsync(running);
            running.FontSizeChanged += size => Preferences.Edit()!.PutFloat("fontSize", size)!.Apply();
            running.PerfVisible = PerfEnabled;
            running.FramePainted += frameGraph.AddSample;
            running.PerfToggled += () => SetPerf(!PerfEnabled);
            loadingInfo = null;
            reporter.Begin(running.Breadcrumb);
            Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
            Show(running.View);
        }
        catch (OperationCanceledException) { reporter.End(); ShowPicker(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reporter.End();
            ShowPicker();
            string message = error is ScriptException s ? $"{s.Diagnostic.Code} {s.Diagnostic.Span?.Source.Path}:{s.Diagnostic.Span?.Line}\n{error.Message}" : error.Message;
            new AlertDialog.Builder(this).SetTitle("无法启动")!.SetMessage(message + $"\n\n版本 {Version}")!.SetPositiveButton("返回", (_, _) => { })!.Show();
        }
        finally { if (loading == cancellation) loading = null; loadingInfo = null; cancellation.Dispose(); }
    }

    private async Task ExitAsync(GamePage running)
    {
        if (game != running) return;
        game = null;
        running.FramePainted -= frameGraph.AddSample;
        reporter.End();
        ShowPicker();
        await running.DisposeAsync();
    }

    private void ShowExitReport(string summary, string path)
    {
        try { ShowExitDialog(summary, path); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { Toast.MakeText(this, $"{summary}\n报告：{path}", ToastLength.Long)!.Show(); }
    }

    private void ShowExitDialog(string summary, string path)
    {
        if (IsFinishing || IsDestroyed) return;
        if (path.Length == 0)
        {
            new AlertDialog.Builder(this).SetTitle("诊断")!.SetMessage(summary)!.SetPositiveButton("关闭", (_, _) => { })!.Show();
            return;
        }
        string termux = path.Replace(StorageRoot, "~/storage/shared", StringComparison.Ordinal);
        new AlertDialog.Builder(this).SetTitle("上次运行异常退出")!
            .SetMessage($"{summary}\n\n完整报告已保存：\n{path}\n\nTermux 中可用 cat {termux} 查看。")!
            .SetPositiveButton("复制报告", (_, _) =>
            {
                try
                {
                    string text = File.ReadAllText(path);
                    if (text.Length > 400_000) text = text[..400_000];
                    (GetSystemService(ClipboardService) as ClipboardManager)!.PrimaryClip = ClipData.NewPlainText("mEmuera 报告", text);
                    Toast.MakeText(this, "已复制", ToastLength.Short)!.Show();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast.MakeText(this, "读取报告失败：" + e.Message, ToastLength.Long)!.Show(); }
            })!
            .SetNegativeButton("关闭", (_, _) => { })!.Show();
    }

    private sealed class BackCallback(MainActivity activity) : Java.Lang.Object, global::Android.Window.IOnBackInvokedCallback
    {
        public void OnBackInvoked() => activity.Back();
    }

    // Android 15 起强制全屏绘制，系统栏和输入法的占位要自己让出来。
    private sealed class Insets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime() | WindowInsets.Type.DisplayCutout());
                view.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
                return WindowInsets.Consumed!;
            }
#pragma warning disable CS0618, CA1422
            view.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
            return insets.ConsumeSystemWindowInsets()!;
#pragma warning restore CS0618, CA1422
        }
    }
}
