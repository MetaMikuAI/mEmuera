using System.Text;
using Android.App;
using Android.Text;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using mEmuera.Cells;
using mEmuera.Contracts;
using mEmuera.EraCompat.Builtins;
using mEmuera.Host;
using mEmuera.Runtime.Scheduling;

namespace mEmuera.App.Android;

/// <summary>
/// 运行中的游戏画面：上方是字符格正文，下方是当前输入的按钮条和输入行（菜单、输入框、确认）。
/// 引擎在后台线程运行，快照经 <see cref="LatestUpdate{T}"/> 合并后回到界面线程。规则与 TUI 的 Program.cs 相同。
/// </summary>
internal sealed class GamePage : IAsyncDisposable
{
    private readonly Activity activity;
    private readonly string version;
    private readonly TaskScheduler engine;
    private readonly LoadedGame loaded;
    private readonly GameRuntime runtime;
    private readonly SessionHost host;
    private readonly CellScreen screen;
    private readonly CellPainter painter;
    private readonly TerminalView terminal;
    private readonly HorizontalScrollView actionsBar;
    private readonly LinearLayout actions;
    private readonly TextView status;
    private readonly EditText input;
    private readonly Button send, menu;
    private readonly LatestUpdate<SessionSnapshot> updates;
    // 计时线程会读它写崩溃现场；快照本身不可变，只需保证引用可见。
    private volatile SessionSnapshot? current;
    private long currentAt;
    private long renderedRequest = -1;
    private (long Epoch, long Revision) renderedTextBox = (-1, -1);
    private bool changingTextBox, started, disposed, ticking;
    private InputKind? fieldKind;
    private long statsInstructions = -1, statsAt;
    private double lastApplyMs;
    private long runningSince = -1;
    private bool seenInput;
    private string? notice;

    public GamePage(Activity activity, LoadedGame loaded, float fontSize, string version, TaskScheduler engine)
    {
        this.activity = activity; this.loaded = loaded; this.version = version; this.engine = engine;
        var pointerQuery = new PointerQueryState();
        runtime = loaded.CreateRuntime(new() { Session = new() { Metrics = new TerminalTextMetrics(), PointerQuery = pointerQuery.Query } });
        host = runtime.Host;
        screen = new CellScreen(pointerQuery);
        painter = new CellPainter(fontSize: fontSize);
        terminal = new TerminalView(activity, screen, painter);

        // 状态行浮在正文左下角、按钮条始终占位：两者出现或消失都不改变正文的格子数，免得引擎反复重排。
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Ui.Background);
        var body = new FrameLayout(activity);
        body.AddView(terminal, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        status = Ui.Label(activity, "正在启动游戏…", 12, Ui.Muted);
        status.SetSingleLine(true); status.Ellipsize = TextUtils.TruncateAt.End;
        status.SetBackgroundColor(Ui.Translucent(Ui.SurfaceVariant, 0xE0));
        status.SetPadding(Ui.Dp(activity, 8), Ui.Dp(activity, 2), Ui.Dp(activity, 8), Ui.Dp(activity, 2));
        body.AddView(status, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.Start));
        root.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        actions = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.CenterVertical);
        actions.SetMinimumHeight(Ui.Dp(activity, 44));
        actionsBar = new HorizontalScrollView(activity) { HorizontalScrollBarEnabled = false };
        actionsBar.AddView(actions);
        root.AddView(actionsBar);
        var row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        menu = Ui.Button(activity, "≡");
        input = Ui.Field(activity, hint: "输入");
        input.ImeOptions = (ImeAction)((int)ImeAction.Go | (int)ImeFlags.NoExtractUi);
        send = Ui.Button(activity, "确认", filled: true);
        row.AddView(menu, Ui.Wrap(activity));
        row.AddView(input, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.AddView(send, Ui.Wrap(activity));
        root.AddView(row);
        View = root;

        updates = new(action => activity.RunOnUiThread(action), Apply);
        host.Changed += updates.Post;
        host.MessageApplied += applied => { if (!applied.Accepted) activity.RunOnUiThread(() => Notice(applied.Reason)); };
        screen.Clicked += (request, button, pointer) => host.Reply(request, button: button, pointer: pointer);
        screen.Advanced += request => host.Reply(request, "");
        screen.PointerChanged += host.Post;
        screen.KeyStateChanged += host.Post;
        // 第一次知道格子数后再启动，标题画面按实际宽度排版。
        terminal.GridChanged += (columns, rows) =>
        {
            host.Post(new ViewportMessage(columns, rows));
            if (!started) { started = true; host.Start(engine); }
        };
        terminal.FontSizeChanged += size => FontSizeChanged?.Invoke(size);
        input.TextChanged += (_, _) =>
        {
            if (changingTextBox || current is not { } snapshot) return;
            // 单字符输入和 TUI 一样，输入一个字就提交。
            if (snapshot.Input is { SingleCharacter: true, IgnoreInput: false } request && input.Text is { Length: > 0 } text)
            {
                changingTextBox = true; input.Text = ""; changingTextBox = false;
                host.Reply(request, System.Globalization.StringInfo.GetNextTextElement(text));
                return;
            }
            host.Post(new TextBoxMessage(snapshot.Epoch, snapshot.TextBoxRevision, input.Text ?? ""));
        };
        input.EditorAction += (_, e) =>
        {
            bool enter = e.Event is { KeyCode: Keycode.Enter, Action: KeyEventActions.Down };
            e.Handled = e.ActionId is ImeAction.Go or ImeAction.Done or ImeAction.Send || enter;
            if (e.Handled) Submit();
        };
        send.Click += (_, _) => Submit();
        menu.Click += (_, _) => ShowMenu();
        Ui.SetEnabled(input, false); Ui.SetEnabled(send, false);
    }

    public View View { get; }
    /// <summary>游戏结束或玩家选择退出。</summary>
    public event Action? Exited;
    public event Action<float>? FontSizeChanged;
    /// <summary>菜单里切换了性能小窗；勾选状态由 <see cref="PerfVisible"/> 提供。</summary>
    public event Action? PerfToggled;
    public bool PerfVisible { get; set; }

    public void ConfirmExit()
    {
        new AlertDialog.Builder(activity).SetTitle("退出游戏")!.SetMessage("未保存的进度会丢失。")!
            .SetPositiveButton("退出", (_, _) => Exited?.Invoke())!.SetNegativeButton("取消", (_, _) => { })!.Show();
    }

    private void Submit()
    {
        if (current?.Input is { IgnoreInput: false } request) { host.Reply(request, input.Text ?? ""); input.Text = ""; }
    }

    private void Notice(string text) { notice = text; UpdateStatus(); }

    private void Apply(SessionSnapshot snapshot)
    {
        if (disposed) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { ApplySnapshot(snapshot); }
        finally { lastApplyMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    private void ApplySnapshot(SessionSnapshot snapshot)
    {
        bool requestChanged = snapshot.Input?.Id != current?.Input?.Id || snapshot.Epoch != current?.Epoch;
        current = snapshot; Volatile.Write(ref currentAt, System.Environment.TickCount64);
        if (requestChanged) notice = null;
        screen.Update(snapshot); terminal.Redraw();
        if (renderedTextBox != (snapshot.Epoch, snapshot.TextBoxRevision))
        {
            renderedTextBox = (snapshot.Epoch, snapshot.TextBoxRevision);
            changingTextBox = true; input.Text = snapshot.TextBox; changingTextBox = false;
        }
        bool accepting = snapshot.Input is { IgnoreInput: false };
        Ui.SetEnabled(input, accepting); Ui.SetEnabled(send, accepting);
        if (snapshot.Input is { } request)
        {
            send.Text = request.Kind == InputKind.Wait ? "继续" : "确认";
            if (fieldKind != request.Kind)
            {
                fieldKind = request.Kind;
                input.InputType = request.Kind == InputKind.Integer ? InputTypes.ClassNumber | InputTypes.NumberFlagSigned : InputTypes.ClassText;
                input.SetSingleLine(true);
                (activity.GetSystemService(global::Android.Content.Context.InputMethodService) as InputMethodManager)?.RestartInput(input);
            }
        }
        if (snapshot.Input?.Id != renderedRequest) { renderedRequest = snapshot.Input?.Id ?? -1; BuildActions(snapshot); }
        UpdateStatus();
        if (snapshot.State == StopReason.Completed) Exited?.Invoke();
    }

    /// <summary>性能小窗里的游戏部分：绘制与更新耗时、格子数、引擎状态与指令速度。</summary>
    public string Stats()
    {
        long now = System.Environment.TickCount64;
        string speed = "—";
        if (current is { } snapshot)
        {
            if (statsInstructions >= 0 && now > statsAt) speed = Rate((snapshot.Instructions - statsInstructions) * 1000d / (now - statsAt));
            statsInstructions = snapshot.Instructions; statsAt = now;
        }
        string state = current?.State switch
        {
            StopReason.WaitingInput => "等待输入", StopReason.Yielded => "运行中", StopReason.WaitingService => "等待服务",
            StopReason.Completed => "已结束", StopReason.Faulted => "出错", StopReason.Cancelled => "已取消", _ => "启动中"
        };
        return $"绘制 {terminal.LastPaintMs:F1} ms（最慢 {terminal.TakeSlowestPaintMs():F1}） 更新 {lastApplyMs:F1} ms 画面 {screen.Columns}×{screen.Rows}\n"
            + $"引擎 {state} {speed} 指令/秒 文档 {current?.Document.Lines.Count ?? 0} 行";
    }

    /// <summary>崩溃现场：引擎状态、诊断和最近的执行事件（含脚本位置）。可在任意线程调用。</summary>
    public string Breadcrumb()
    {
        var snapshot = current;
        if (snapshot is null) return $"游戏 {loaded.Game.Title}：已加载，尚未收到画面";
        var text = new StringBuilder();
        text.AppendLine($"最后一次收到引擎画面：{(System.Environment.TickCount64 - Volatile.Read(ref currentAt)) / 1000d:F1} 秒前");
        text.AppendLine($"游戏 {loaded.Game.Title}：状态 {snapshot.State}，已执行 {snapshot.Instructions} 条指令，输入 {snapshot.Input?.Kind.ToString() ?? "无"}，文档 {snapshot.Document.Lines.Count} 行，画面 {screen.Columns}×{screen.Rows}");
        foreach (var d in snapshot.Diagnostics.TakeLast(5)) text.AppendLine($"诊断 {d.Code} {d.Span?.Source.Path}:{d.Span?.Line} {d.Message}");
        if (!snapshot.Events.IsDefaultOrEmpty)
        {
            text.AppendLine("最近的执行事件：");
            foreach (var e in snapshot.Events.TakeLast(60))
                text.AppendLine($"  {e.Instructions,10} {e.Kind} {e.Detail} {e.Span?.Source.Path}:{e.Span?.Line}");
        }
        return text.ToString();
    }

    private static string Rate(double perSecond) => perSecond >= 1e6 ? $"{perSecond / 1e6:F1}M" : perSecond >= 1e3 ? $"{perSecond / 1e3:F0}K" : $"{perSecond:F0}";

    private void UpdateStatus()
    {
        if (current is not { } snapshot) return;
        string? text = null; var color = Ui.Muted;
        if (snapshot.Diagnostics.LastOrDefault() is { } diagnostic)
        { text = $"{diagnostic.Code} {diagnostic.Span?.Source.Path}:{diagnostic.Span?.Line} {diagnostic.Message}"; color = Ui.Error; }
        else if (notice is not null) text = notice;
        else if (!snapshot.Events.IsDefaultOrEmpty && snapshot.Events[^1] is { Kind: TraceKind.ServiceCompleted, Detail: "Save" }) text = "保存完成";
        else if (snapshot.Input is null && snapshot.State is StopReason.Yielded or StopReason.WaitingService)
        {
            // 两次输入之间的短暂运行不提示，超过半秒才显示，避免状态行闪烁。
            long now = System.Environment.TickCount64;
            if (runningSince < 0) runningSince = now;
            if (now - runningSince >= 500 || !seenInput) text = $"运行中… 已执行 {Rate(snapshot.Instructions)} 条指令";
        }
        if (snapshot.Input is not null || snapshot.State is not (StopReason.Yielded or StopReason.WaitingService)) runningSince = -1;
        if (snapshot.Input is not null) seenInput = true;
        if (snapshot.Input is { DisplayTime: true, Deadline: { } deadline })
        {
            text = $"等待输入：{Math.Max(0, (deadline - SessionHost.Now(TimeProvider.System) + 999) / 1000)} 秒";
            if (!ticking) { ticking = true; status.PostDelayed(() => { ticking = false; if (!disposed) UpdateStatus(); }, 500); }
        }
        status.Text = text ?? ""; status.SetTextColor(color);
        status.Visibility = text is null ? ViewStates.Gone : ViewStates.Visible;
    }

    // 当前输入的按钮放到底部按钮条，文字取正文里该按钮最近一次出现的内容。
    private void BuildActions(SessionSnapshot snapshot)
    {
        actions.RemoveAllViews();
        if (snapshot.Input is not { IgnoreInput: false } request || request.Buttons.IsEmpty)
        {
            string hint = snapshot.Input is { IgnoreInput: false, Kind: InputKind.Wait } or { IgnoreInput: false, AnyKey: true } ? "点按正文继续" : "";
            actions.AddView(Ui.Label(activity, hint, 12, Ui.Muted), Ui.Wrap(activity, 8));
            return;
        }
        var labels = ButtonLabels(snapshot.Document, request.Buttons.Keys);
        foreach (var (id, value) in request.Buttons.OrderBy(p => p.Key))
        {
            var button = Ui.Button(activity, labels.TryGetValue(id, out var label) ? label : value.ToString());
            button.Click += (_, _) => { if (current?.Input?.Id == request.Id) host.Reply(request, button: id); };
            actions.AddView(button, Ui.Wrap(activity, 3));
        }
        actionsBar.ScrollTo(0, 0);
    }

    private static Dictionary<long, string> ButtonLabels(PresentationDocument document, IEnumerable<long> ids)
    {
        var wanted = ids.ToHashSet(); var result = new Dictionary<long, string>();
        for (int i = document.Lines.Count - 1; i >= Math.Max(0, document.Lines.Count - 400) && wanted.Count > 0; i--)
        {
            var line = new Dictionary<long, StringBuilder>();
            foreach (var run in document.Lines[i].Runs)
                if (run.ButtonId is { } id && wanted.Contains(id))
                    (line.TryGetValue(id, out var text) ? text : line[id] = new()).Append(run.Text);
            foreach (var (id, text) in line)
            {
                string label = string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (label.Length == 0) continue;
                result[id] = label.Length > 24 ? label[..23] + "…" : label;
                wanted.Remove(id);
            }
        }
        return result;
    }

    private void ShowMenu()
    {
        var popup = new PopupMenu(activity, menu);
        var items = popup.Menu!;
        bool waiting = current?.State == StopReason.WaitingInput;
        items.Add(0, 1, 1, "保存…")!.SetEnabled(current?.CanSave == true);
        items.Add(0, 2, 2, "读取…")!.SetEnabled(current?.CanLoad == true);
        items.Add(0, 3, 3, "导入旧档")!.SetEnabled(loaded.LegacySaves.Length > 0 && waiting);
        items.Add(0, 4, 4, "错误详情")!.SetEnabled(current?.Diagnostics.Length > 0);
        items.Add(0, 5, 5, "回到最新");
        items.Add(0, 7, 6, "性能监测")!.SetCheckable(true)!.SetChecked(PerfVisible);
        items.Add(0, 6, 7, "退出游戏");
        popup.MenuItemClick += (_, e) =>
        {
            switch (e.Item?.ItemId)
            {
                case 1: SlotDialog(false); break;
                case 2: SlotDialog(true); break;
                case 3: _ = ImportLegacyAsync(); break;
                case 4: ShowDiagnostics(); break;
                case 5: screen.Scroll(int.MaxValue / 2); terminal.Redraw(); break;
                case 6: ConfirmExit(); break;
                case 7: PerfToggled?.Invoke(); break;
            }
        };
        popup.Show();
    }

    private void SlotDialog(bool reading)
    {
        var slot = Ui.Field(activity, "0");
        slot.InputType = InputTypes.ClassNumber;
        var box = new FrameLayout(activity);
        box.SetPadding(Ui.Dp(activity, 20), Ui.Dp(activity, 8), Ui.Dp(activity, 20), 0);
        box.AddView(slot);
        new AlertDialog.Builder(activity).SetTitle(reading ? "读取存档" : "保存存档")!.SetMessage("槽位编号")!.SetView(box)!
            .SetPositiveButton("确认", (_, _) => { if (int.TryParse(slot.Text, out int n) && n >= 0) host.Post(new SaveCommand(n, reading)); })!
            .SetNegativeButton("取消", (_, _) => { })!.Show();
    }

    private async Task ImportLegacyAsync()
    {
        string text;
        try
        {
            var results = await Task.Factory.StartNew(() => loaded.ImportLegacySavesAsync(runtime.Services.Saves), CancellationToken.None, TaskCreationOptions.None, engine).Unwrap();
            text = $"已导入 {results.Count(r => r.Imported)} 个，共 {results.Length} 个旧档（只读，原文件未改动）\n\n"
                + string.Join("\n", results.Select(r => $"{Path.GetFileName(r.File.Path)} → 槽位 {r.File.Slot}：{r.Message}"));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { text = "导入失败：" + error.Message; }
        if (!disposed) TextDialog("导入旧档", text);
    }

    private void ShowDiagnostics()
    {
        string text = string.Join("\n\n", (current?.Diagnostics ?? []).Select(d =>
            $"{d.Code} {d.Span?.Source.Path}:{d.Span?.Line}:{d.Span?.Column}\n{d.Message}\n{(d.Stack.IsDefault ? "" : string.Join(" → ", d.Stack))}"));
        TextDialog("错误详情", $"版本 {version}\n\n{text}");
    }

    private void TextDialog(string title, string text)
    {
        var label = Ui.Label(activity, text, 12);
        label.SetTextIsSelectable(true);
        label.SetPadding(Ui.Dp(activity, 20), Ui.Dp(activity, 8), Ui.Dp(activity, 20), 0);
        var scroll = new ScrollView(activity);
        scroll.AddView(label);
        new AlertDialog.Builder(activity).SetTitle(title)!.SetView(scroll)!.SetPositiveButton("关闭", (_, _) => { })!.Show();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        updates.Dispose();
        host.Post(new CancelMessage());
        await runtime.DisposeAsync();
        loaded.Dispose();
        painter.Dispose();
    }
}
