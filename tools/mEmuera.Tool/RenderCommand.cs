using mEmuera.Contracts;
using mEmuera.Host;
using mEmuera.Rendering;
using mEmuera.Runtime.Execution;
using mEmuera.Runtime.Scheduling;
using SkiaSharp;

/// <summary>无头运行游戏，按 emuera.config 的像素参数把每个等待输入的画面渲染成 PNG。
/// --script 给出依次送入的输入（逗号分隔，空项表示按任意键/空输入），每送一次再渲染一张；--dump 同时输出文档结构。</summary>
internal static class RenderCommand
{
    public static async Task<int> RunAsync(string gamePath, string dataRoot, string output, string[] script, bool dump, int menuHeight)
    {
        var load = await LoadedGame.TryLoadAsync(new(gamePath, dataRoot));
        if (load.Game is null) { foreach (var d in load.Diagnostics.Take(5)) Console.Error.WriteLine(d.Code + " " + d.Message); return 3; }
        using var loaded = load.Game;
        var display = DisplayMetrics.From(loaded.Options, menuHeight);
        Console.WriteLine($"canvas {display.WindowWidth}x{display.WindowHeight} (menu={menuHeight}) font={display.FontName} {display.FontSize}px line={display.LineHeight}px cell={display.CellWidth} columns={display.Columns} rows={display.Rows}");
        using var fonts = new FontCatalog();
        Console.WriteLine($"fonts registered={fonts.AddDirectory(Path.Combine(gamePath, "font"))}");
        var metrics = new FontTextMetrics(fonts, display);
        await using var runtime = loaded.CreateRuntime(new() { Session = new() { Metrics = metrics } });
        var host = runtime.Host;
        host.Post(new ViewportMessage(display.Columns, display.Rows));
        using var painter = new DocumentPainter(display, fonts);

        SessionSnapshot? last = null; (long Epoch, long Id)? handled = null;
        TaskCompletionSource<SessionSnapshot> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Changed += state =>
        {
            last = state;
            if (state.State == StopReason.WaitingInput && state.Input is { } input && handled != (input.Epoch, input.Id)) waiting.TrySetResult(state);
        };
        host.Start();
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
            string path = step == 0 ? output : Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileNameWithoutExtension(output) + $"-step{step}" + Path.GetExtension(output));
            Describe(step, snapshot, dump);
            Render(painter, snapshot.Document, display, metrics, path);
            if (step == script.Length) break;
            handled = (snapshot.Input!.Epoch, snapshot.Input.Id);
            waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.WriteLine($"-> input \"{script[step]}\"");
            host.Reply(snapshot.Input, script[step]);
        }
        return 0;
    }

    private static void Describe(int step, SessionSnapshot snapshot, bool dump)
    {
        var doc = snapshot.Document;
        int inline = 0, panels = 0, buttonImages = 0;
        void Count(IEnumerable<LogicalLine> lines)
        {
            foreach (var run in lines.SelectMany(l => l.Runs))
            {
                if (run.Image is { } image) { inline++; if (image.SelectedImage is not null) buttonImages++; }
                if (run.Panel is { } panel) { panels++; Count(panel.Lines); }
            }
        }
        Count(doc.Lines);
        Console.WriteLine($"step {step}: lines={doc.Lines.Count} inlineImages={inline} (withHoverImage={buttonImages}) panels={panels} imageLayers={doc.ImageLayers.Length} " +
            $"bgLayers={doc.BackgroundLayers.Length} (bgImage={doc.BackgroundLayers.Count(l => l.BackgroundName is not null)} buttonMap={doc.BackgroundLayers.Count(l => l.ButtonMap)} animated={doc.BackgroundLayers.Count(l => l.Animated)}) " +
            $"input={snapshot.Input?.Kind} buttons={snapshot.Input?.Buttons.Count}");
        if (!dump) return;
        int n = 0;
        foreach (var line in doc.Lines)
        {
            string runs = string.Join("|", line.Runs.Select(r => (r.Image is not null ? $"[img {r.Image.Width}x{r.Image.Height}+{r.Image.Y}]" : r.Panel is not null ? $"[panel {r.Panel.X},{r.Panel.Y} {r.Panel.Width}x{r.Panel.Height}]" : r.Text)
                + (r.ButtonId is { } b ? $"#{b}" : "") + (r.Color is { } c ? $"@{c:x6}" : "") + (r.Style != 0 ? $"s{r.Style}" : "") + (r.Font is { } f ? $"f:{f}" : "")));
            Console.WriteLine($"{n++,3} {(line.Alignment != TextAlignment.Left ? line.Alignment.ToString()[0] + " " : "  ")}{(runs.Length > 200 ? runs[..200] + "…" : runs)}");
        }
    }

    private static void Render(DocumentPainter painter, PresentationDocument doc, DisplayMetrics display, ITextMetrics metrics, string path)
    {
        var layout = DocumentLayout.Build(doc.Lines, display.WindowWidth, display, metrics);
        using var bitmap = painter.Render(doc, layout);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path); data.SaveTo(file);
        Console.WriteLine($"   layout rows={layout.Rows.Length} height={layout.Height}px -> {path}");
    }
}
