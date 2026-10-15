using mEmuera.EraCompat.Builtins;
using System.Collections.Immutable;
using mEmuera.Cells;
using mEmuera.App.Tui.Views;
using mEmuera.Contracts;
using mEmuera.Host;
using mEmuera.Infrastructure.FileSystem;
using mEmuera.Runtime.Execution;
using mEmuera.Runtime.Scheduling;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Terminal.Gui.Input;
using mEmuera.App.Tui.Layout;

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{ Console.Error.WriteLine("需要交互终端。命令行回放请使用 mEmuera.Tool replay。"); return 2; }
string? Option(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
using IApplication app = Application.Create();
try
{
    app.Init();
    var locations = GameLocations.Desktop();
    string? gamePath = Option("--game");
    var candidates = gamePath is null ? GameLocator.Candidates(locations) : [];
    if (candidates.Length == 1) gamePath = candidates[0];
    if (gamePath is null)
    {
        using var picker = new Window { Title = "打开游戏目录（Esc 取消）" };
        var field = new TextField { X = 1, Y = 2, Width = Dim.Fill(2), Text = candidates.FirstOrDefault() ?? "" };
        var open = new Button { X = 1, Y = 4, Text = "打开" };
        open.Accepting += (_, e) => { e.Handled = true; gamePath = field.Text; app.RequestStop(); };
        picker.Add(new Label { X = 1, Y = 1, Text = candidates.Length > 1 ? $"找到 {candidates.Length} 个游戏目录，已填入第一个" : "游戏目录" }, field, open); app.Run(picker);
        if (string.IsNullOrWhiteSpace(gamePath)) return 130;
    }
    var loadOptions = new GameLoadOptions(gamePath, Option("--data-dir") ?? GameLocator.DataDirectory(locations, gamePath))
    { Profile = Option("--profile") ?? "era-v1", Encoding = Option("--encoding") ?? "utf-8", GameId = Option("--game-id") };
    using var loadingCancellation = new CancellationTokenSource();
    using var loadingWindow = new Window { Title = "正在加载游戏（Esc 取消）" };
    var loadingStatus = new Label { X = 1, Y = 1, Width = Dim.Fill(2), Text = "读取文件与 CSV" };
    var cancelLoading = new Button { X = 1, Y = 3, Text = "取消" };
    cancelLoading.Accepting += (_, e) => { e.Handled = true; loadingCancellation.Cancel(); app.RequestStop(); };
    loadingWindow.Add(loadingStatus, cancelLoading);
    var loadingTask = Task.Run(() => LoadedGame.LoadAsync(loadOptions, stage =>
    {
        if (stage != LoadStage.ReadingFiles)
            app.Invoke(() => loadingStatus.Text = stage == LoadStage.Indexing ? "索引脚本与声明" : "初始化游戏");
    }, loadingCancellation.Token), loadingCancellation.Token);
    _ = loadingTask.ContinueWith(_ => app.Invoke(() => app.RequestStop()), TaskScheduler.Default);
    app.Run(loadingWindow);
    if (!loadingTask.IsCompleted) loadingCancellation.Cancel();
    using var loaded = await loadingTask;
    var game = loaded.Game;
    var legacySaves = loaded.LegacySaves;
    var scriptKeys = new ScriptKeys();
    var pointerQuery = new PointerQueryState();
    await using var runtime = loaded.CreateRuntime(new()
    {
        Media = MediaFactory.Process(Option("--audio-player") ?? "ffplay"),
        Session = new() { Metrics = new TerminalTextMetrics(), KeyState = scriptKeys.IsDown, InputActive = scriptKeys.IsActive, PointerQuery = pointerQuery.Query }
    });
    var session = runtime.Session;
    var host = runtime.Host;
    var reportSelection = (Option("--report-variables") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => new StateSelection(name)).ToList();
    if (Option("--report-selection") is { } selectionPath)
        reportSelection.AddRange(System.Text.Json.JsonSerializer.Deserialize<StateSelection[]>(await File.ReadAllTextAsync(selectionPath)) ?? []);
    using var stateCheckpoints = Option("--state-checkpoints") is { } checkpointPath ? new StreamWriter(checkpointPath, false) { AutoFlush = true } : null;
    using var window = new Window { Title = game.Title + " — Esc 退出" };
    var document = new DocumentView(pointerQuery) { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(5) };
    string graphicsMode = Option("--graphics") ?? "auto";
    if (graphicsMode is not ("auto" or "cells" or "kitty")) throw new ArgumentException("--graphics 需要 auto、cells 或 kitty");
    document.KittyEnabled = graphicsMode == "kitty" || graphicsMode == "auto" && KittyGraphics.Detect(Environment.GetEnvironmentVariable);
    byte[]? lastKittyBackground = null, lastKittyForeground = null;
    (int X, int Y, int Width, int Height) lastKittyPosition = default;
    void ClearKitty()
    {
        if (lastKittyBackground is null) return;
        app.Driver?.WriteRaw(KittyGraphics.Delete(19771) + KittyGraphics.Delete(19772));
        lastKittyBackground = lastKittyForeground = null;
    }
    app.LayoutAndDrawComplete += (_, _) =>
    {
        if (app.TopRunnableView != window || document.KittyFrame() is not { } frame) { ClearKitty(); return; }
        var origin = document.ViewportToScreen(new System.Drawing.Point(0, 0));
        var position = (origin.X, origin.Y, document.Viewport.Width, document.Viewport.Height);
        if (position == lastKittyPosition && lastKittyBackground is not null
            && lastKittyBackground.AsSpan().SequenceEqual(frame.Background) && lastKittyForeground!.AsSpan().SequenceEqual(frame.Foreground)) return;
        app.Driver?.WriteRaw(KittyGraphics.Transmit(frame.Background, 19771, origin.X, origin.Y, position.Width, position.Height, -1)
            + KittyGraphics.Transmit(frame.Foreground, 19772, origin.X, origin.Y, position.Width, position.Height, 1));
        lastKittyPosition = position; lastKittyBackground = frame.Background; lastKittyForeground = frame.Foreground;
    };
    var input = new TextField { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(12), Height = 1 };
    var send = new Button { X = Pos.AnchorEnd(11), Y = Pos.AnchorEnd(4), Text = "确认" };
    var actions = new View { X = 0, Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
    var save = new Button { X = 0, Y = Pos.AnchorEnd(2), Text = "保存" };
    var load = new Button { X = 12, Y = Pos.AnchorEnd(2), Text = "读取" };
    var exit = new Button { X = 24, Y = Pos.AnchorEnd(2), Text = "退出" };
    var details = new Button { X = 36, Y = Pos.AnchorEnd(2), Text = "错误详情", Enabled = false };
    var importLegacy = new Button { X = 52, Y = Pos.AnchorEnd(2), Text = "导入旧档", Enabled = false };
    var status = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "准备中", HotKeySpecifier = (System.Text.Rune)0xffff };
    SessionSnapshot? current = null; long renderedRequest = -1;
    void PublishKey(Key key, bool down)
    {
        if (current is null) return;
        int code = ScriptKeys.Code(key);
        if (code >= 0) host.Post(new KeyStateMessage(current.Epoch, code, down));
        host.Post(new KeyStateMessage(current.Epoch, 16, down && key.IsShift));
        host.Post(new KeyStateMessage(current.Epoch, 17, down && key.IsCtrl));
        host.Post(new KeyStateMessage(current.Epoch, 18, down && key.IsAlt));
    }
    app.Keyboard.KeyDown += (_, key) => PublishKey(key, true);
    app.Keyboard.KeyUp += (_, key) => PublishKey(key, false);
    (long Epoch, long Revision) renderedTextBox = (-1, -1);
    bool changingTextBox = false;
    input.TextChanged += (_, _) =>
    {
        if (!changingTextBox && current is { } snapshot)
            host.Post(new TextBoxMessage(snapshot.Epoch, snapshot.TextBoxRevision, input.Text));
    };
    void Submit() { if (current?.Input is { } request) { host.Reply(request, input.Text); input.Text = ""; } }
    void SaveDialog(bool reading)
    {
        using var dialog = new Window { Title = reading ? "读取存档" : "保存存档", X = Pos.Center(), Y = Pos.Center(), Width = 40, Height = 7 };
        var slot = new TextField { X = 1, Y = 1, Width = 20, Text = "0" };
        var ok = new Button { X = 1, Y = 3, Text = "确认槽位" };
        ok.Accepting += (_, e) => { e.Handled = true; if (int.TryParse(slot.Text, out int n) && n >= 0) { host.Post(new SaveCommand(n, reading)); app.RequestStop(); } };
        dialog.Add(slot, ok); app.Run(dialog);
    }
    document.Clicked += (request, button, pointer) => host.Reply(request, button: button, pointer: pointer);
    document.PointerChanged += host.Post;
    document.KeyStateChanged += host.Post;
    send.Accepting += (_, e) => { e.Handled = true; Submit(); };
    input.Accepting += (_, e) => { e.Handled = true; Submit(); };
    save.Accepting += (_, e) => { e.Handled = true; SaveDialog(false); };
    load.Accepting += (_, e) => { e.Handled = true; SaveDialog(true); };
    exit.Accepting += (_, e) => { e.Handled = true; app.RequestStop(); };
    importLegacy.Accepting += (_, e) =>
    {
        e.Handled = true; importLegacy.Enabled = false;
        string text;
        try
        {
            var results = Task.Run(() => loaded.ImportLegacySavesAsync(runtime.Services.Saves)).GetAwaiter().GetResult();
            text = $"已导入 {results.Count(r => r.Imported)} 个，共 {results.Length} 个旧档（只读，原文件未改动）\n\n"
                + string.Join("\n", results.Select(r => $"{Path.GetFileName(r.File.Path)} → 槽位 {r.File.Slot}：{r.Message}"));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { text = "导入失败：" + error.Message; }
        using var dialog = new Window { Title = "导入旧档 — Esc 返回", X = Pos.Center(), Y = Pos.Center(), Width = Dim.Percent(80), Height = Dim.Percent(60) };
        dialog.Add(new Label { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(1), Text = text });
        app.Run(dialog);
    };
    details.Accepting += (_, e) =>
    {
        e.Handled = true;
        using var dialog = new Window { Title = "错误详情 — Esc 返回" };
        var content = new DocumentView { Width = Dim.Fill(), Height = Dim.Fill() };
        string text = string.Join("\n\n", (current?.Diagnostics ?? []).Select(d => $"{d.Code} {d.Span?.Source.Path}:{d.Span?.Line}:{d.Span?.Column}\n{d.Message}\n{(d.Stack.IsDefault ? "" : string.Join(" → ", d.Stack))}"));
        content.Update(current! with { Document = new(1, text.Split('\n').Select(line => new LogicalLine([new StyledRun(line)])).ToImmutableList()), Input = null });
        dialog.Add(content);
        app.Run(dialog);
    };
    int result = 0;
    void HandleScriptKey(object? sender, Key key)
    {
        if (key == Key.C.WithCtrl) return;
        if (current?.Input is { IgnoreInput: false } request && (request.AnyKey || request.SingleCharacter && !key.IsCtrl && !key.IsAlt && key.AsRune.Value >= 32))
        { key.Handled = true; host.Reply(request, request.AnyKey ? "" : key.AsRune.ToString()); }
    }
    input.KeyDown += HandleScriptKey;
    document.KeyDown += HandleScriptKey;
    window.KeyDown += (_, key) =>
    {
        if (key == Key.C.WithCtrl)
        { key.Handled = true; result = 130; host.Post(new CancelMessage()); app.RequestStop(); return; }
        HandleScriptKey(window, key);
    };
    (long Epoch, long Request, long Accepted, StopReason State, int Diagnostics)? lastLiveState = null;
    long lastLiveWrite = 0;
    using var updates = new LatestUpdate<SessionSnapshot>(action => app.Invoke(action), snapshot =>
    {
        current = snapshot; document.Update(snapshot); save.Enabled = snapshot.CanSave; load.Enabled = snapshot.CanLoad;
        if (renderedTextBox != (snapshot.Epoch, snapshot.TextBoxRevision))
        {
            renderedTextBox = (snapshot.Epoch, snapshot.TextBoxRevision);
            changingTextBox = true;
            input.Text = snapshot.TextBox;
            changingTextBox = false;
        }
        details.Enabled = snapshot.Diagnostics.Length > 0;
        importLegacy.Enabled = legacySaves.Length > 0 && snapshot.State == StopReason.WaitingInput;
        input.Enabled = snapshot.Input is { IgnoreInput: false }; send.Enabled = input.Enabled;
        status.Text = snapshot.Diagnostics.LastOrDefault() is { } diagnostic
            ? $"{diagnostic.Code} {diagnostic.Span?.Source.Path}:{diagnostic.Span?.Line} {diagnostic.Message}"
            : snapshot.Events.LastOrDefault() is { Kind: TraceKind.ServiceCompleted, Detail: "Save" }
                ? "保存完成" : snapshot.State.ToString();
        if (snapshot.Input is { DisplayTime: true, Deadline: { } deadline })
            status.Text = $"等待输入：{Math.Max(0, (deadline - SessionHost.Now(TimeProvider.System) + 999) / 1000)} 秒";
        if (snapshot.Input?.Id != renderedRequest)
        {
            renderedRequest = snapshot.Input?.Id ?? -1;
            foreach (var view in actions.SubViews.ToArray()) { actions.Remove(view); view.Dispose(); }
            int x = 0;
            if (snapshot.Input is { } request)
            {
                foreach (var pair in request.Buttons.Where(_ => !request.IgnoreInput).Take(8))
                {
                    var button = new Button { X = x, Y = 0, Text = pair.Value.ToString() }; x += 8;
                    long id = pair.Key;
                    button.Accepting += (_, e) => { e.Handled = true; host.Reply(request, button: id); }; actions.Add(button);
                }
                send.Text = request.Kind == InputKind.Wait ? "继续" : "确认";
                if (!request.NoFocus) input.SetFocus();
            }
        }
        if (snapshot.State == StopReason.Completed) app.RequestStop();
        if (snapshot.State == StopReason.Faulted) result = 4;
        if (Option("--live-report") is { } liveReport)
        {
            var identity = (snapshot.Epoch, snapshot.Input?.Id ?? -1, snapshot.LastAcceptedInputId,
                snapshot.State is StopReason.Yielded or StopReason.WaitingService ? StopReason.Yielded : snapshot.State, snapshot.Diagnostics.Length);
            long now = Environment.TickCount64;
            if (lastLiveState == identity && now - lastLiveWrite < 1000) return;
            var path = Path.GetFullPath(liveReport);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
            {
                snapshot.Epoch, snapshot.State, snapshot.Mode, snapshot.Input, snapshot.Instructions,
                snapshot.CanSave, snapshot.CanLoad, snapshot.Diagnostics, snapshot.Events,
                snapshot.LastAcceptedInputId, snapshot.LastAcceptedInputEpoch
            }));
            lastLiveState = identity; lastLiveWrite = now;
        }
    });
    long checkpointEpoch = -1, checkpointRequest = -1;
    host.Changed += snapshot =>
    {
        if (stateCheckpoints is not null && snapshot.State == StopReason.WaitingInput && snapshot.Input is { } request
            && (request.Epoch != checkpointEpoch || request.Id != checkpointRequest))
        {
            int characterCount = session.InspectSelected([]).Characters.Length;
            var unavailable = reportSelection.Where(s => s.Character is { } i && (i < 0 || i >= characterCount)).ToArray();
            var selected = session.InspectSelected(reportSelection.Except(unavailable));
            stateCheckpoints.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                loaded.Index.Identity, selected.Epoch, selected.Mode, selected.Instructions, selected.Input,
                selected.Variables, selected.Shapes, selected.Characters, selected.Diagnostics,
                selected.LastAcceptedInputId, selected.LastAcceptedInputEpoch,
                UnavailableSelections = unavailable
            }));
            checkpointEpoch = request.Epoch; checkpointRequest = request.Id;
        }
        updates.Post(snapshot);
    };
    host.MessageApplied += applied => { if (!applied.Accepted) app.Invoke(() => status.Text = applied.Reason); };
    window.Add(document, input, send, actions, save, load, exit, details, importLegacy, status);
    app.AddTimeout(TimeSpan.FromMilliseconds(50), () => { document.TickTooltip(); return true; });
    window.ViewportChanged += (_, _) =>
    {
        host.Post(new ViewportMessage(Math.Max(1, document.Viewport.Width), Math.Max(1, document.Viewport.Height)));
        bool small = window.Viewport.Width < 38 || window.Viewport.Height < 10;
        foreach (var view in new View[] { document, input, send, actions, save, load, details, importLegacy }) view.Visible = !small;
        exit.X = small ? 0 : 24; exit.Y = small ? 1 : Pos.AnchorEnd(2);
        status.Y = small ? 0 : Pos.AnchorEnd(1);
        if (small) status.Text = "窗口过小，Esc 退出";
    };
    window.Initialized += (_, _) => { host.Post(new ViewportMessage(Math.Max(1, document.Viewport.Width), Math.Max(1, document.Viewport.Height))); host.Start(); };
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; result = 130; host.Post(new CancelMessage()); app.Invoke(() => app.RequestStop()); };
    Console.CancelKeyPress += cancel;
    try { app.Run(window); }
    finally { ClearKitty(); Console.CancelKeyPress -= cancel; }
    await host.DisposeAsync();
    if (Option("--session-report") is { } report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        var snapshot = args.Contains("--full-state") ? session.Inspect() : session.InspectSelected(reportSelection);
        await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    return result;
}
catch (OperationCanceledException) { return 130; }
catch (Exception e) when (e is ScriptException or IOException or UnauthorizedAccessException)
{
    using var error = new Window { Title = "无法启动 — Esc 返回" };
    error.Add(new Label { Text = e is ScriptException s ? $"{s.Diagnostic.Code} {s.Diagnostic.Span?.Source.Path}:{s.Diagnostic.Span?.Line}\n{e.Message}" : e.Message, Width = Dim.Fill(), Height = Dim.Fill() });
    app.Run(error); return 4;
}
