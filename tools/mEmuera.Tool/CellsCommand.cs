using mEmuera.Cells;
using mEmuera.Contracts;
using mEmuera.EraCompat.Builtins;
using mEmuera.Host;
using mEmuera.Runtime.Scheduling;
using SkiaSharp;

/// <summary>无头运行游戏，用触屏前端的字符格画法（与 TUI 的 Kitty 模式同一套分层）把每个等待输入的画面渲染成 PNG。
/// --script 与 render 命令相同；--columns/--rows 是格子数，--font-size 是字号像素。</summary>
internal static class CellsCommand
{
    public static async Task<int> RunAsync(string gamePath, string dataRoot, string output, string[] script, int columns, int rows, float fontSize)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // 与 Android 前端相同：加载和宿主循环都在大栈线程上，不依赖线程池的栈大小。
        using var scheduler = new LargeStackScheduler();
        var load = await Task.Factory.StartNew(() => LoadedGame.TryLoadAsync(new(gamePath, dataRoot), stage => Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s {stage}")),
            CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();
        Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s loaded");
        if (load.Game is null) { foreach (var d in load.Diagnostics.Take(5)) Console.Error.WriteLine(d.Code + " " + d.Message); return 3; }
        using var loaded = load.Game;
        var pointerQuery = new PointerQueryState();
        await using var runtime = loaded.CreateRuntime(new() { Session = new() { Metrics = new TerminalTextMetrics(), PointerQuery = pointerQuery.Query } });
        var host = runtime.Host;
        var screen = new CellScreen(pointerQuery);
        screen.Resize(columns, rows);
        host.Post(new ViewportMessage(columns, rows));
        using var painter = new CellPainter(fontSize: fontSize);
        Console.WriteLine($"grid {columns}x{rows} cell={painter.CellWidth}x{painter.CellHeight} canvas={columns * painter.CellWidth}x{rows * painter.CellHeight}");

        SessionSnapshot? last = null; (long Epoch, long Id)? handled = null;
        TaskCompletionSource<SessionSnapshot> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += state =>
        {
            last = state;
            if (state.State == StopReason.WaitingInput && state.Input is { } input && handled != (input.Epoch, input.Id)) waiting.TrySetResult(state);
        };
        host.Start(scheduler);
        for (int step = 0; step <= script.Length; step++)
        {
            SessionSnapshot snapshot;
            try { snapshot = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
            catch (TimeoutException)
            {
                Console.WriteLine($"TIMEOUT step={step} last={last?.State} instr={last?.Instructions}");
                foreach (var d in (last?.Diagnostics ?? []).TakeLast(5)) Console.WriteLine($"{d.Code} {d.Span?.Source.Path}:{d.Span?.Line} {d.Message}");
                return 5;
            }
            using (var self = System.Diagnostics.Process.GetCurrentProcess())
                Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s input {step} rss={self.WorkingSet64 / 1048576}MB peak={self.PeakWorkingSet64 / 1048576}MB managed={GC.GetTotalMemory(false) / 1048576}MB instructions={snapshot.Instructions}");
            string path = step == 0 ? output : Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileNameWithoutExtension(output) + $"-step{step}" + Path.GetExtension(output));
            screen.Update(snapshot);
            using (var bitmap = painter.Render(screen.Frame(0)!))
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var file = File.Create(path); data.SaveTo(file);
            }
            var layout = screen.Frame(0)!.Layout;
            Console.WriteLine($"step {step}: lines={snapshot.Document.Lines.Count} glyphs={layout.Glyphs.Count} images={layout.Glyphs.Count(g => g.Raster is not null)} layers={layout.ImageLayers.Count} input={snapshot.Input?.Kind} buttons={snapshot.Input?.Buttons.Count} -> {path}");
            if (step == script.Length) break;
            handled = (snapshot.Input!.Epoch, snapshot.Input.Id);
            waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.WriteLine($"-> input \"{script[step]}\"");
            host.Reply(snapshot.Input, script[step]);
        }
        return 0;
    }
}
