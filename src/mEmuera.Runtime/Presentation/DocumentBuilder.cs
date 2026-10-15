using System.Collections.Immutable;
using System.Xml;
using mEmuera.Contracts;

namespace mEmuera.Runtime.Presentation;

internal sealed partial class DocumentBuilder
{
    private ImmutableList<LogicalLine> lines = [new([])];
    private long revision;
    private long button;
    private long tooltipTarget;
    private long activeButtonStart = 1, nextButtonStart = 1;
    private SourceSpan? lastValueInput;
    private InputKind? lastInputKind;
    public Func<string, IReadOnlyList<NumberedTextPart>>? NumberedText { get; set; }
    private int pendingStart = -1;
    private int bufferStart;
    private int columnPosition;
    public int AnimationInterval { get; private set; }
    public bool CachePrintedLines { get; set; }
    public bool LastLineTemporary => bufferStart > 0 && lines[bufferStart - 1].Temporary;
    public void SetAnimationTimer(long interval)
    {
        if (interval < int.MinValue || interval > short.MaxValue) throw new ScriptException("ANIMATION_INTERVAL", "动画刷新间隔越界");
        AnimationInterval = interval <= 0 ? 0 : (int)Math.Max(10, interval); revision++;
    }
    public void UpdateAnimation(IReadOnlyDictionary<string, RasterImage> images)
    {
        InlineRaster? Refresh(InlineRaster? image) => image is null ? null : image with
        {
            Image = image.Source is { } source && images.TryGetValue(source, out var frame) ? frame : image.Image,
            SelectedImage = image.SelectedSource is { } selected && images.TryGetValue(selected, out var selectedFrame) ? selectedFrame : image.SelectedImage
        };
        ImmutableList<LogicalLine> Update(ImmutableList<LogicalLine> source) => source.Select(line => line with
        {
            Runs = line.Runs.Select(run => run with
            {
                Image = Refresh(run.Image),
                Panel = run.Panel is { } panel ? panel with { Lines = Update(panel.Lines) } : null
            }).ToImmutableArray()
        }).ToImmutableList();
        lines = Update(lines);
        imageLayers = imageLayers.Select(layer => layer.Source is { } name && images.TryGetValue(name, out var frame) ? layer.WithFrame(frame) : layer).ToImmutableArray();
        backgroundLayers = backgroundLayers.Select(layer =>
        {
            var sizing = new ScriptImageLayer(layer.Z, layer.X, layer.Y, 0, new(layer.Width, layer.Height, [])) { Opacity = layer.Opacity, Matrix = layer.Matrix };
            return layer with
            {
                Pixels = layer.Source is { } name && images.TryGetValue(name, out var frame) ? sizing.WithFrame(frame).Image.Pixels : layer.Pixels,
                SelectedImage = layer.SelectedSource is { } selected && images.TryGetValue(selected, out var selectedFrame) ? sizing.WithFrame(selectedFrame).Image : layer.SelectedImage
            };
        }).ToImmutableArray();
        revision++;
    }
    private void ReplaceTemporary()
    {
        if (bufferStart == 0 || !lines[bufferStart - 1].Temporary) return;
        int start = bufferStart - 1;
        while (start > 0 && lines[start].ContinuesPrevious) start--;
        int removed = bufferStart - start;
        lines = lines.RemoveRange(start, removed); bufferStart = start;
        imageLayers = imageLayers.Where(l => l.BottomAnchored || l.AnchorLine < start || l.AnchorLine >= start + removed)
            .Select(l => !l.BottomAnchored && l.AnchorLine >= start + removed ? l with { AnchorLine = l.AnchorLine - removed } : l).ToImmutableArray();
    }
    private void NewLine(bool force = false)
    {
        FlushNumbered();
        if (force && IsPrintingBufferEmpty) LiteralText(" ");
        if (lines.Count > bufferStart + 1 && lines[^1].ContinuesPrevious && lines[^1].Runs.IsEmpty) lines = lines.RemoveAt(lines.Count - 1);
        ReplaceTemporary();
        for (int i = bufferStart; i < lines.Count; i++) lines = lines.SetItem(i, lines[i] with { CacheLayout = CachePrintedLines });
        lines = lines.Add(new([])); bufferStart = lines.Count - 1; columnPosition = 0; revision++;
    }
    private void HtmlBreak(bool noWrap = false)
    {
        FlushNumbered();
        lines = lines.SetItem(lines.Count - 1, lines[^1] with { Alignment = Alignment, NoWrap = noWrap || lines[^1].NoWrap });
        lines = lines.Add(new([]) { ContinuesPrevious = true, Alignment = Alignment, NoWrap = noWrap }); columnPosition = 0; revision++;
    }
    public void Temporary(string text)
    {
        if (text.Length == 0) return;
        if (!IsPrintingBufferEmpty) NewLine();
        LiteralText(text, noWrap: true, numbered: true); NewLine();
        lines = lines.SetItem(lines.Count - 2, lines[^2] with { Temporary = true });
    }
    public void Single(string text)
    {
        if (text.Length == 0) return;
        if (!IsPrintingBufferEmpty) NewLine();
        LiteralText(text, noWrap: true, numbered: true); NewLine();
    }
    public void Plain(string text) { if (text.Length == 0) return; LiteralText(text); revision++; }
    public void Column(string text, bool right, int width, int viewport, ITextMetrics metrics, EraValue? buttonValue = null)
    {
        if (text.Length == 0) return;
        FlushNumbered();
        int content = metrics.Columns(text);
        if (columnPosition > 0 && columnPosition + content > viewport) Text("", true);
        bool full = columnPosition + width <= viewport;
        int padding = full ? Math.Max(0, width - content) : 0;
        if (right && padding > 0) Text(new string(' ', padding));
        if (buttonValue is { } value) Button(text, value); else Text(text, numbered: true);
        FlushNumbered();
        if (!right && padding > 0) Text(new string(' ', padding));
        columnPosition = full ? columnPosition + width : viewport;
    }
    private void FlushNumbered()
    {
        if (pendingStart < 0) return;
        var runs = lines[^1].Runs; int start = pendingStart; pendingStart = -1;
        string text = string.Concat(runs.Skip(start).Select(r => r.Text));
        var result = runs.Take(start).ToImmutableArray().ToBuilder(); int runIndex = start, runOffset = 0;
        foreach (var part in NumberedText!(text))
        {
            long? id = part.Value is null ? null : ++button;
            int remaining = part.Length;
            while (remaining > 0)
            {
                var run = runs[runIndex]; int size = Math.Min(remaining, run.Text.Length - runOffset);
                result.Add(run with { Text = run.Text.Substring(runOffset, size), ButtonId = id, ButtonValue = part.Value is { } value ? new EraValue(value) : null });
                remaining -= size; runOffset += size;
                if (runOffset == run.Text.Length) { runIndex++; runOffset = 0; }
            }
        }
        lines = lines.SetItem(lines.Count - 1, lines[^1] with { Runs = result.ToImmutable() });
    }
    private TooltipSettings tooltip = new();
    public TooltipSettings Tooltip { get => tooltip; set { tooltip = value; revision++; } }
    public int? DefaultForeground { get; set; }
    public string? DefaultFont { get; set; }
    public int FocusColor { get; set; } = 0xffff00;
    public int? DefaultBackground { get; set; }
    private int? color;
    public int? Color { get => color ?? DefaultForeground; set => color = value; }
    public int Style { get; set; }
    public string? Font { get; set; }
    public int CurrentBackground => backgroundColor ?? DefaultBackground ?? 0;
    public uint? TextBackground { get; set; }
    private int? backgroundColor;
    private ImmutableArray<RasterLayer> backgroundLayers = [];
    private ImmutableArray<ScriptImageLayer> imageLayers = [];
    public void AddImageLayer(ScriptImageLayer layer) { imageLayers = imageLayers.Add(layer); revision++; }
    public void ClearImageLayers(long? depth) { imageLayers = depth is null ? [] : imageLayers.Where(l => l.Depth != depth).ToImmutableArray(); revision++; }
    public bool HasImageLayer(long depth) => imageLayers.Any(l => l.Depth == depth);
    public void BackgroundLayers(ImmutableArray<RasterLayer> layers) { backgroundLayers = layers; revision++; }
    public void Background(int? color) { backgroundColor = color; revision++; }
    public TextAlignment Alignment { get; set; }
    private PresentationDocument? frozen;
    public long Revision => frozen?.Revision ?? revision;
    public bool RedrawEnabled => frozen is null;
    public void Redraw(bool enabled) { if (enabled) { frozen = null; revision++; } else frozen ??= Snapshot(); }
    public void Refresh()
    {
        FlushNumbered();
        if (lines[^1].CacheLayout != CachePrintedLines) lines = lines.SetItem(lines.Count - 1, lines[^1] with { CacheLayout = CachePrintedLines });
        if (!IsPrintingBufferEmpty) NewLine();
        bool suspended = frozen is not null;
        frozen = null;
        revision++;
        if (suspended) frozen = Snapshot();
    }
    public SourceSpan? Source { get; set; }
    public int LineCount => lines.Take(bufferStart).Count(line => !line.ContinuesPrevious);
    public int PrintingLineIndex => bufferStart;
    public bool LineIsEmpty => IsPrintingBufferEmpty;
    public bool IsPrintingBufferEmpty => lines.Count == bufferStart + 1 && lines[^1].Runs.IsEmpty;
    public void DrawLine(string text)
    {
        int previous = Style; Style = 0;
        try { Text(text, true); }
        finally { Style = previous; }
    }
    public string DisplayLine(long index)
    {
        long position = index >= 0 ? index : bufferStart + index;
        return position >= 0 && position < bufferStart ? string.Concat(lines[(int)position].Runs.Select(r => r.Text)) : "";
    }
    public PresentationDocument Snapshot() => frozen ?? new(revision, lines) { BackgroundColor = backgroundColor ?? DefaultBackground, BackgroundLayers = backgroundLayers, ImageLayers = imageLayers, Tooltip = tooltip, AnimationInterval = AnimationInterval, FocusColor = FocusColor };
    public void Text(string text, bool newline = false, long? id = null, EraValue? value = null, bool noWrap = false, string? title = null, bool numbered = false, long? tooltipId = null, bool? colorSpecified = null, string? rawInput = null, string? exportHtml = null)
    {
        if (text.Length == 0 && !newline) return;
        numbered &= NumberedText is not null;
        if (!numbered) FlushNumbered();
        string[] pieces = text.Split('\n');
        for (int i = 0; i < pieces.Length; i++)
        {
            if (numbered && pendingStart < 0) pendingStart = lines[^1].Runs.Length;
            if (pieces[i].Length > 0) lines = lines.SetItem(lines.Count - 1, lines[^1] with { Runs = lines[^1].Runs.Add(new(pieces[i], Color, id, value, Source) { Background = TextBackground, Style = Style, Font = Font, Tooltip = title, TooltipId = tooltipId, ColorSpecified = colorSpecified ?? (Color is not null && Color != DefaultForeground), RawInput = rawInput, ExportHtml = exportHtml }), Alignment = Alignment, NoWrap = noWrap || lines[^1].NoWrap });
            if (i < pieces.Length - 1) NewLine(force: true);
        }
        if (newline) NewLine(force: true);
        revision++;
    }
    private void LiteralText(string text, bool noWrap = false, bool numbered = false)
    {
        numbered &= NumberedText is not null;
        if (!numbered) FlushNumbered();
        if (numbered && pendingStart < 0) pendingStart = lines[^1].Runs.Length;
        lines = lines.SetItem(lines.Count - 1, lines[^1] with
        {
            Runs = lines[^1].Runs.Add(new(text, Color, Source: Source) { Background = TextBackground, Style = Style, Font = Font, ColorSpecified = Color is not null && Color != DefaultForeground }),
            Alignment = Alignment, NoWrap = noWrap || lines[^1].NoWrap
        });
    }
    public void Button(string text, EraValue value) => Text(text, id: ++button, value: value);
    private static IEnumerable<StyledRun> Runs(IEnumerable<LogicalLine> source)
    {
        foreach (var run in source.SelectMany(l => l.Runs))
        {
            yield return run;
            if (run.Panel is { } panel) foreach (var child in Runs(panel.Lines)) yield return child;
        }
    }
    public ImmutableDictionary<long, EraValue> Buttons(InputKind? kind = null, SourceSpan? source = null, bool rawStrings = false)
    {
        if (kind is not null and not InputKind.Wait)
        {
            // 同一输入语句重试且没有新按钮时保留上一批；不同输入点废弃旧按钮。
            if (button >= nextButtonStart || source != lastValueInput || kind != lastInputKind)
                activeButtonStart = nextButtonStart;
            nextButtonStart = button + 1; lastValueInput = source; lastInputKind = kind;
        }
        var result = Runs(lines).Where(r => r.ButtonId is { } id && (kind is null || id >= activeButtonStart))
            .GroupBy(r => r.ButtonId!.Value).ToImmutableDictionary(g => g.Key, g => rawStrings
                ? new EraValue(g.First().RawInput ?? g.First().ButtonValue!.Value.ToString()) : g.First().ButtonValue!.Value);
        foreach (uint color in backgroundLayers.Where(l => l.ButtonMap).SelectMany(l => l.Pixels).Where(p => p >> 24 == 255).Distinct())
        {
            long value = color & 0xffffff;
            result = result.SetItem(-value - 1, rawStrings ? new EraValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture)) : new EraValue(value));
        }
        return result;
    }
    public void Clear(long count)
    {
        FlushNumbered();
        if (count < 0) throw new ScriptException("CLEARLINE", "清行数量不能为负");
        int start = bufferStart; long groups = 0;
        while (start > 0 && groups < count) { start--; if (!lines[start].ContinuesPrevious) groups++; }
        int remove = bufferStart - start;
        imageLayers = imageLayers.Where(l => l.BottomAnchored || l.AnchorLine < start || l.AnchorLine >= bufferStart)
            .Select(l => !l.BottomAnchored && l.AnchorLine >= bufferStart ? l with { AnchorLine = l.AnchorLine - remove } : l).ToImmutableArray();
        lines = lines.RemoveRange(start, remove); bufferStart = start; revision++;
    }
    public void Reset(bool keepBackgrounds = false) { lines = [new([])]; bufferStart = 0; pendingStart = -1; columnPosition = 0; activeButtonStart = nextButtonStart = button + 1; lastValueInput = null; lastInputKind = null; frozen = null; Alignment = TextAlignment.Left; Color = null; Style = 0; Font = null; backgroundColor = null; if (!keepBackgrounds) backgroundLayers = []; imageLayers = []; TextBackground = null; revision++; }
}
