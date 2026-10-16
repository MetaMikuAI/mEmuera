using System.Globalization;
using System.Text;
using System.Collections.Immutable;
using System.Xml.Linq;
using mEmuera.Contracts;

namespace mEmuera.Runtime.Presentation;

internal sealed partial class DocumentBuilder
{
    public string PopPrintingHtml(int viewport, ITextMetrics metrics)
    {
        FlushNumbered();
        if (IsPrintingBufferEmpty) return "";
        var group = lines.Skip(bufferStart).ToArray();
        if (group.Length > 1 && group[^1].ContinuesPrevious && group[^1].Runs.IsEmpty) group = group[..^1];
        string html = SerializeHtml(group.SelectMany(line => PhysicalLineLayout.Wrap(line, viewport, metrics)).Select(row => row.Line).ToArray());
        lines = lines.RemoveRange(bufferStart, lines.Count - bufferStart).Add(new([])); columnPosition = 0; revision++;
        return html;
    }
    public string PrintedHtml(long index, int viewport, ITextMetrics metrics)
    {
        if (index < 0) throw new ScriptException("BOUNDS", "已输出 HTML 行号不能小于零");
        int end = bufferStart;
        for (long count = 0; end > 0; count++)
        {
            int start = end - 1;
            while (start > 0 && lines[start].ContinuesPrevious) start--;
            if (count == index) return SerializeHtml(lines.Skip(start).Take(end - start).SelectMany(line => PhysicalLineLayout.Wrap(line, viewport, metrics)).Select(row => row.Line).ToArray(), printed: true);
            end = start;
        }
        return "";
    }
    private string SerializeHtml(IReadOnlyList<LogicalLine> group, bool printed = false)
    {
        var output = new StringBuilder();
        if (printed) output.Append("<p align='").Append(group[0].Alignment.ToString().ToLowerInvariant()).Append("'><nobr>");
        for (int row = 0; row < group.Count; row++)
        {
            var line = group[row];
            if (row > 0) output.Append("<br>");
            for (int at = 0; at < line.Runs.Length; at++)
            {
                var run = line.Runs[at];
                string? tag = null;
                if (run.ButtonValue is { } value)
                {
                    tag = "button"; output.Append("<button"); Attribute(output, "value", run.RawInput ?? value.ToString());
                }
                else if (!string.IsNullOrEmpty(run.Tooltip)) { tag = "nonbutton"; output.Append("<nonbutton"); }
                if (tag is not null)
                {
                    if (!string.IsNullOrEmpty(run.Tooltip)) Attribute(output, "title", run.Tooltip);
                    output.Append('>');
                }
                output.Append(SerializeRun(run));
                if (run.ButtonId is { } id)
                    while (at + 1 < line.Runs.Length && line.Runs[at + 1].ButtonId == id) output.Append(SerializeRun(line.Runs[++at]));
                else if (tag == "nonbutton" && run.TooltipId is { } tooltip)
                    while (at + 1 < line.Runs.Length && line.Runs[at + 1].TooltipId == tooltip && line.Runs[at + 1].ButtonValue is null)
                        output.Append(SerializeRun(line.Runs[++at]));
                if (tag is not null) output.Append("</").Append(tag).Append('>');
            }
        }
        if (printed) output.Append("</nobr></p>");
        return output.ToString();
    }
    private static string Escape(string text) => System.Security.SecurityElement.Escape(text)!;
    private static void Attribute(StringBuilder output, string name, string value)
        => output.Append(' ').Append(name).Append("='").Append(HtmlFragment.EscapeAttribute(value)).Append('\'');
    private static string RasterMarkup(XElement element)
    {
        var output = new StringBuilder("<").Append(element.Name.LocalName);
        foreach (var attribute in element.Attributes()) Attribute(output, attribute.Name.LocalName, attribute.Value);
        return output.Append('>').ToString();
    }
    private string SerializeRun(StyledRun run)
    {
        if (run.VisualOnly) return "";
        if (run.ExportHtml is { } exported) return SerializeRaster(exported);
        if ((run.Image is not null && run.Image.Html is null) || run.Panel is not null || run.Background is not null)
            throw new ScriptException("HTML_SERIALIZE", "尚不能将图片、面板或文字背景转换为 HTML");
        if (run.Image?.Html is { } markup) return SerializeRaster(markup);
        var output = new StringBuilder();
        bool colorChanged = run.Color is not null && (run.ColorSpecified || run.Color != DefaultForeground);
        bool faceChanged = run.Font is not null && !string.Equals(run.Font, DefaultFont, StringComparison.Ordinal);
        bool fontChanged = colorChanged || faceChanged;
        if (fontChanged)
        {
            output.Append("<font");
            if (faceChanged) Attribute(output, "face", run.Font!);
            if (colorChanged) Attribute(output, "color", $"#{run.Color!.Value:X6}");
            output.Append('>');
        }
        foreach (var (bit, tag) in new[] { (4, "s"), (8, "u"), (2, "i"), (1, "b") })
            if ((run.Style & bit) != 0) output.Append('<').Append(tag).Append('>');
        output.Append(Escape(run.Text));
        foreach (var (bit, tag) in new[] { (1, "b"), (2, "i"), (8, "u"), (4, "s") })
            if ((run.Style & bit) != 0) output.Append("</").Append(tag).Append('>');
        if (fontChanged) output.Append("</font>");
        return output.ToString();
    }
    private static string SerializeRaster(string markup)
    {
        var output = new StringBuilder();
        foreach (var node in HtmlFragment.Parse(markup).Nodes())
        {
            if (node is XText text) { output.Append(Escape(text.Value)); continue; }
            if (node is not XElement element || element.Name.LocalName is not ("shape" or "img"))
                throw new ScriptException("HTML_SERIALIZE", "内联图形标记无效");
            string name = element.Name.LocalName; output.Append('<').Append(name);
            if (name == "shape")
            {
                Attribute(output, "type", element.Attribute("type")!.Value);
                Attribute(output, "param", string.Join(", ", element.Attribute("param")!.Value.Split(',').Select(NormalizeSize)));
                if (element.Attribute("color") is { } color) Attribute(output, "color", color.Value.ToUpperInvariant());
            }
            else
            {
                Attribute(output, "src", element.Attribute("src")!.Value);
                if (element.Attribute("srcb") is { } selected) Attribute(output, "srcb", selected.Value);
                foreach (string size in new[] { "height", "width", "ypos" })
                    if (element.Attribute(size) is { } attribute)
                    {
                        string value = NormalizeSize(attribute.Value);
                        if (value is not ("0" or "0px")) Attribute(output, size, value);
                    }
            }
            output.Append('>');
        }
        return output.ToString();
    }
    private static string NormalizeSize(string value)
    {
        value = value.Trim(); bool pixels = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
        string number = pixels ? value[..^2] : value;
        return int.Parse(number, NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + (pixels ? "px" : "");
    }
    public static long MeasureHtml(string text, ITextMetrics metrics, IReadOnlyDictionary<string, RasterImage>? images = null, int viewport = 80)
    {
        var measured = new DocumentBuilder(); measured.Html(text, false, images, metrics.CellWidth);
        return PhysicalLineLayout.Wrap(measured.lines[0], viewport, metrics)[0].Width;
    }
    private static (long Pixels, float Fraction) MeasureLine(LogicalLine line, ITextMetrics metrics)
    {
        long pixels = 0; float fraction = 0;
        foreach (var run in line.Runs)
        {
            if (run.Image is { } image && !image.WidthSteps.IsDefaultOrEmpty)
                foreach (float step in image.WidthSteps)
                {
                    float width = fraction + step; int whole = (int)width;
                    pixels += whole; fraction = width - whole;
                }
            else pixels += run.Image?.Width ?? run.Panel?.Width ?? (long)metrics.Columns(run.Text) * metrics.CellWidth;
        }
        return (pixels, fraction);
    }
    private static float ShapeFraction(LogicalLine line)
    {
        float fraction = 0;
        foreach (var image in line.Runs.Select(r => r.Image).OfType<InlineRaster>())
            if (!image.WidthSteps.IsDefaultOrEmpty)
                foreach (float step in image.WidthSteps) { float width = fraction + step; fraction = width - (int)width; }
        return fraction;
    }
    public void HtmlColumn(string text, bool right, int width, int viewport, ITextMetrics metrics, IReadOnlyDictionary<string, RasterImage>? images = null)
    {
        if (text.Length == 0) return;
        FlushNumbered();
        int Pixels(LogicalLine line) => checked((int)MeasureLine(line, metrics).Pixels);
        var measured = new DocumentBuilder(); measured.Html(text, true, images, metrics.CellWidth);
        if (measured.IsPrintingBufferEmpty) return;
        int content = measured.Snapshot().Lines.Max(Pixels);
        int current = Pixels(lines[^1]);
        if (current > 0 && (long)current + width > (long)viewport * metrics.CellWidth) Text("", true);
        int padding = Math.Max(0, width - content);
        void Pad()
        {
            lines = lines.SetItem(lines.Count - 1, lines[^1] with { Runs = lines[^1].Runs.Add(new("")
            { Image = new(new(1, 1, [0]), padding, metrics.CellWidth * 2) { WidthSteps = [padding] }, VisualOnly = true }) });
            revision++;
        }
        if (right && padding > 0) Pad();
        Html(text, true, images, metrics.CellWidth);
        if (!right && padding > 0) Pad();
        columnPosition = (Pixels(lines[^1]) + metrics.CellWidth - 1) / metrics.CellWidth;
    }
    private static int HtmlColor(string raw)
    {
        if (!raw.StartsWith('#'))
        {
            var named = System.Drawing.Color.FromName(raw);
            if (named.A != 0) return named.ToArgb() & 0xffffff;
        }
        string value = raw.StartsWith('#') ? raw[1..] : raw;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
            throw new ScriptException("HTML_COLOR", "颜色格式错误或超出 RGB 范围");
        if (raw.StartsWith('#') && value.Length > 6)
        {
            if ((color >> 24) != 0xff) throw new ScriptException("HTML_COLOR", "当前 HTML 只支持完全不透明的 ARGB 颜色");
            return (int)(color & 0xffffff);
        }
        if (color > 0xffffff) throw new ScriptException("HTML_COLOR", "颜色格式错误或超出 RGB 范围");
        return (int)color;
    }
    private sealed record Shape(float X, float Y, float Width, float Height, uint Color, string Html);
    private readonly record struct HtmlCacheKey(string Text, int? Foreground, TextAlignment Alignment, bool ToBuffer, int CellWidth);
    private readonly Dictionary<HtmlCacheKey, HtmlAtom[]> htmlFragments = new();
    private readonly Queue<HtmlCacheKey> htmlFragmentOrder = new();
    private int htmlFragmentCharacters;
    private sealed record HtmlAtom(string Text, int? Color, int Style, TextAlignment Alignment, string? Value, int Button,
        InlineRaster? Image = null, bool Flush = false, InlinePanel? Panel = null, HtmlAtom[]? Children = null, bool NoWrap = false, Shape? Shape = null, string? Tooltip = null, string? Font = null, bool ColorSpecified = false, string? ExportHtml = null);
    public void Html(string text, bool toBuffer = true, IReadOnlyDictionary<string, RasterImage>? images = null, int cellWidth = 8)
    {
        if (text.Length == 0) return;
        FlushNumbered();
        var atoms = new List<HtmlAtom>(); int next = 0;
        int Measure(string? value, int fallback, bool signed = false)
        {
            if (value is null) return fallback;
            bool pixels = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(pixels ? value[..^2] : value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) throw new ScriptException("HTML_SIZE", "HTML 尺寸无效");
            int size = pixels ? number : checked(number * cellWidth * 2 / 100);
            if (size > 8192 || size < (signed ? -8192 : 0)) throw new ScriptException("HTML_SIZE", "HTML 尺寸越界");
            return size == 0 ? fallback : size;
        }
        float ShapeMeasure(string value)
        {
            bool pixels = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(pixels ? value[..^2] : value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                throw new ScriptException("HTML_SIZE", "HTML 尺寸无效");
            float size = pixels ? number : (float)number * cellWidth * 2 / 100;
            if (size is < -8192 or > 8192) throw new ScriptException("HTML_SIZE", "HTML 尺寸越界");
            return size;
        }
        float ImageMeasure(string? value, float fallback)
        {
            if (value is null) return fallback;
            bool pixels = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
            if (!int.TryParse(pixels ? value[..^2] : value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                throw new ScriptException("HTML_SIZE", "HTML 尺寸无效");
            float size = number == 0 ? fallback : pixels ? number : (float)number * cellWidth * 2 / 100;
            if (size is < -8192 or > 8192) throw new ScriptException("HTML_SIZE", "HTML 尺寸越界");
            return size;
        }
        void Walk(XElement parent, HtmlAtom state, int depth)
        {
            if (depth > 64) throw new ScriptException("HTML_DEPTH", "HTML 嵌套过深");
            foreach (var node in parent.Nodes())
            {
                if (node is XComment) continue;
                if (node is XText content) { atoms.Add(state with { Text = content.Value }); continue; }
                if (node is not XElement element) throw new ScriptException("HTML_SYNTAX", "HTML 节点无效");
                string name = element.Name.LocalName.ToLowerInvariant();
                string allowed = name switch
                {
                    "p" => "align", "font" => "color face", "button" => "value title", "nonbutton" => "title", "img" => "src srcb height width ypos",
                    "shape" => "type param color", "br" or "b" or "i" or "u" or "s" or "nobr" => "",
                    "div" => "xpos ypos width height padding border radius color bcolor",
                    _ => throw new ScriptException("HTML_UNSUPPORTED", "未支持标签: " + name)
                };
                foreach (var attribute in element.Attributes())
                    if (!allowed.Split(' ').Contains(attribute.Name.LocalName)) throw new ScriptException("HTML_UNSUPPORTED", "未支持 HTML 属性: " + attribute.Name);
                var child = state;
                if (name is "button" or "nonbutton") child = child with { Tooltip = (string?)element.Attribute("title"), Button = ++next };
                if (name == "nobr" && !toBuffer) child = child with { NoWrap = true };
                if (name == "div")
                {
                    if (state.Panel is not null) throw new ScriptException("HTML_DIV", "不支持嵌套 div");
                    int? ColorValue(string key)
                    {
                        if ((string?)element.Attribute(key) is not { } raw) return null;
                        return HtmlColor(raw);
                    }
                    int start = atoms.Count;
                    var panel = new InlinePanel(Measure((string?)element.Attribute("xpos"), 0, true), Measure((string?)element.Attribute("ypos"), 0, true),
                        Measure((string?)element.Attribute("width"), 0), Measure((string?)element.Attribute("height"), 0),
                        Measure((string?)element.Attribute("padding"), 0), Measure((string?)element.Attribute("border"), 0), Measure((string?)element.Attribute("radius"), 0),
                        ColorValue("color"), ColorValue("bcolor"), []);
                    if (panel.Width <= 0) throw new ScriptException("HTML_SIZE", "div 需要正数宽度");
                    Walk(element, child with { Panel = panel }, depth + 1);
                    var children = atoms.Skip(start).Select(a => a with { Panel = null }).ToArray(); atoms.RemoveRange(start, atoms.Count - start);
                    atoms.Add(child with { Panel = panel, Children = children }); continue;
                }
                if (name == "p")
                {
                    if (!Enum.TryParse<TextAlignment>((string?)element.Attribute("align") ?? "left", true, out var alignment)) throw new ScriptException("HTML_ALIGN", "段落对齐无效");
                    if (!toBuffer) { atoms.Add(state with { Flush = true }); child = child with { Alignment = alignment }; }
                }
                if (name == "button") child = child with { Value = (string?)element.Attribute("value") };
                if (name == "font" && (string?)element.Attribute("color") is { } rgb)
                {
                    child = child with { Color = HtmlColor(rgb), ColorSpecified = true };
                }
                if (name == "font" && (string?)element.Attribute("face") is { } face) child = child with { Font = face };
                int style = name switch { "b" => 1, "i" => 2, "u" => 8, "s" => 4, _ => 0 };
                child = child with { Style = child.Style | style };
                if (name == "br") atoms.Add(child with { Text = "\n" });
                else if (name == "shape")
                {
                    string type = ((string?)element.Attribute("type") ?? "").ToLowerInvariant();
                    var parameters = ((string?)element.Attribute("param") ?? "").Split(',').Select(v => ShapeMeasure(v.Trim())).ToArray();
                    int color = child.Color ?? 0xffffff;
                    if ((string?)element.Attribute("color") is { } raw) color = HtmlColor(raw);
                    var tag = new XElement(element);
                    tag.SetAttributeValue("type", type);
                    if (type == "rect") tag.SetAttributeValue("color", $"#{color:x6}");
                    string markup = RasterMarkup(tag);
                    Shape shape = (type, parameters.Length) switch
                    {
                        ("space", 1) => new(0, 0, parameters[0], cellWidth * 2, 0, markup),
                        ("rect", 1) => new(0, 0, parameters[0], cellWidth * 2, 0xff000000u | (uint)color, markup),
                        ("rect", 4) => new(parameters[0], parameters[1], parameters[2], parameters[3], 0xff000000u | (uint)color, markup),
                        _ => throw new ScriptException("HTML_SHAPE", "未支持此形状或参数数量")
                    };
                    if (type == "rect" && (shape.X < 0 || shape.Width <= 0 || shape.Height <= 0)) throw new ScriptException("HTML_SHAPE", "矩形横坐标不能为负，宽高必须大于零");
                    atoms.Add(child with { Shape = shape });
                }
                else if (name == "img")
                {
                    string source = (string?)element.Attribute("src") ?? throw new ScriptException("HTML_IMAGE", "img 缺少 src");
                    if (images is null) throw new ScriptException("HTML_RESOURCE", "HTML 图像尚未解析");
                    if (!images.TryGetValue(source, out var image))
                    {
                        string markup = SerializeRaster(RasterMarkup(element));
                        atoms.Add(child with { Text = markup, ExportHtml = markup, Color = DefaultForeground,
                            Style = 0, Font = null, ColorSpecified = false });
                    }
                    else
                    {
                        int height = (int)ImageMeasure((string?)element.Attribute("height"), cellWidth * 2);
                        float width = ImageMeasure((string?)element.Attribute("width"), (float)image.Width * height / image.Height);
                        if (Math.Abs(width) > 8192) throw new ScriptException("HTML_SIZE", "HTML 尺寸越界");
                        int ypos = Measure((string?)element.Attribute("ypos"), 0, true);
                        string? selected = (string?)element.Attribute("srcb");
                        atoms.Add(child with { Image = new(image, (int)Math.Abs(width), Math.Abs(height), ypos, source)
                        { SelectedSource = selected, SelectedImage = selected is null ? null : images.GetValueOrDefault(selected), Html = RasterMarkup(element),
                            WidthSteps = [Math.Abs(width)], FlipX = width < 0, FlipY = height < 0 } });
                    }
                }
                else Walk(element, child, depth + 1);
                if (name == "p" && !toBuffer) atoms.Add(child with { Flush = true });
            }
        }
        var cacheKey = new HtmlCacheKey(text, DefaultForeground, toBuffer ? Alignment : TextAlignment.Left, toBuffer, cellWidth);
        if (images is null && htmlFragments.TryGetValue(cacheKey, out var cached)) atoms.AddRange(cached);
        else
        {
            Walk(HtmlFragment.Parse(text), new("", DefaultForeground, 0, cacheKey.Alignment, null, 0), 0);
            if (images is null && text.Length <= 16384)
            {
                while (htmlFragments.Count >= 256 || htmlFragmentCharacters + text.Length > 262144)
                {
                    var oldest = htmlFragmentOrder.Dequeue();
                    htmlFragments.Remove(oldest); htmlFragmentCharacters -= oldest.Text.Length;
                }
                htmlFragments.Add(cacheKey, atoms.ToArray()); htmlFragmentOrder.Enqueue(cacheKey); htmlFragmentCharacters += text.Length;
            }
        }
        if (!toBuffer && !IsPrintingBufferEmpty) NewLine();
        var ids = new Dictionary<int, long>(); var tooltipIds = new Dictionary<int, long>();
        int? previous = Color; int previousStyle = Style; var previousAlignment = Alignment; string? previousFont = Font;
        void Render(DocumentBuilder target, IEnumerable<HtmlAtom> source)
        {
            var sourceAtoms = source.ToArray();
            ImmutableArray<StyledRun>.Builder? pendingRuns = null;
            TextAlignment pendingAlignment = default;
            bool pendingNoWrap = false;
            void FlushText()
            {
                if (pendingRuns is null) return;
                target.lines = target.lines.SetItem(target.lines.Count - 1, target.lines[^1] with
                { Runs = pendingRuns.ToImmutable(), Alignment = pendingAlignment, NoWrap = pendingNoWrap });
                pendingRuns = null;
            }
            for (int at = 0; at < sourceAtoms.Length; at++)
            {
                var atom = sourceAtoms[at];
                if (atom.Shape is not null || atom.Image is not null || atom.Panel is not null || atom.Flush || atom.Text.Contains('\n')) FlushText();
                if (atom.Shape is not null)
                {
                    float fraction = ShapeFraction(target.lines[^1]);
                    if (atom.Shape.Width < 0)
                    {
                        atom = atom with { Image = new(new(1, 1, [0]), (int)(fraction + atom.Shape.Width), cellWidth * 2)
                        { Html = atom.Shape.Html, WidthSteps = [atom.Shape.Width] }, Shape = null };
                    }
                    else
                    {
                        var shapes = new List<(int X, int Y, int Width, int Height, uint Color, string Html)>();
                        var widths = ImmutableArray.CreateBuilder<float>(); int width = 0, top = 0, bottom = cellWidth * 2;
                        do
                        {
                            var shape = sourceAtoms[at].Shape!;
                            float step = shape.X + shape.Width, edge = fraction + step;
                            int advance = (int)edge, left = (int)(fraction + shape.X), y = (int)shape.Y, paintHeight = (int)shape.Height;
                            if (paintHeight == 0 && shape.Height >= 0.001f) paintHeight = 1;
                            shapes.Add((width + left, y, advance - left, paintHeight, shape.Color, shape.Html)); widths.Add(step);
                            width += advance; fraction = edge - advance; top = Math.Min(top, y); bottom = Math.Max(bottom, y + paintHeight);
                            if (width > 8192 || bottom - top > 8192 || (long)width * (bottom - top) > 16_000_000) throw new ScriptException("HTML_SIZE", "组合形状尺寸超限");
                            at++;
                        } while (at < sourceAtoms.Length && sourceAtoms[at].Shape is { Width: >= 0 } && sourceAtoms[at].Button == atom.Button
                            && sourceAtoms[at].NoWrap == atom.NoWrap && sourceAtoms[at].Tooltip == atom.Tooltip);
                        at--;
                        int height = bottom - top, rasterWidth = Math.Max(1, width);
                        var pixels = new uint[rasterWidth * height];
                        var selected = atom.Value is not null ? new uint[pixels.Length] : null;
                        foreach (var shape in shapes)
                            for (int y = shape.Y; y < shape.Y + shape.Height; y++) Array.Fill(pixels, shape.Color, (y - top) * rasterWidth + shape.X, shape.Width);
                        if (selected is not null)
                            foreach (var shape in shapes.Where(shape => shape.Color != 0))
                                for (int y = shape.Y; y < shape.Y + shape.Height; y++) Array.Fill(selected, 0xff000000u | (uint)target.FocusColor, (y - top) * rasterWidth + shape.X, shape.Width);
                        atom = atom with { Shape = null, Image = new(new(rasterWidth, height, pixels.ToImmutableArray()), width, height, top)
                        { Html = string.Concat(shapes.Select(s => s.Html)), WidthSteps = widths.ToImmutable(),
                            SelectedImage = selected is null ? null : new(rasterWidth, height, selected.ToImmutableArray()) } };
                    }
                }
                target.Color = atom.Color; target.Style = atom.Style; target.Alignment = atom.Alignment; target.Font = atom.Font;
                if (atom.Flush) { if (target.lines[^1].Runs.Length > 0) target.HtmlBreak(atom.NoWrap); continue; }
                if (atom.Text == "\n") { target.HtmlBreak(atom.NoWrap); continue; }
                long? id = null; EraValue? value = null;
                long? tooltipId = null;
                if (atom.Button != 0)
                {
                    if (!tooltipIds.TryGetValue(atom.Button, out long key)) tooltipIds.Add(atom.Button, key = ++tooltipTarget);
                    tooltipId = key;
                }
                if (atom.Value is not null)
                {
                    if (!ids.TryGetValue(atom.Button, out long key)) ids.Add(atom.Button, key = ++button);
                    id = key; value = long.TryParse(atom.Value, out long integer) ? new(integer) : new(atom.Value);
                }
                var panel = atom.Panel;
                if (panel is not null)
                {
                    var content = new DocumentBuilder(); Render(content, atom.Children!);
                    panel = panel with { Lines = content.lines };
                }
                if (atom.Image is not null || panel is not null)
                {
                    target.lines = target.lines.SetItem(target.lines.Count - 1, target.lines[^1] with
                    { Runs = target.lines[^1].Runs.Add(new("", atom.Color, id, value, Source)
                    { Image = atom.Image, Panel = panel, Style = atom.Style, Font = atom.Font, Background = TextBackground, Tooltip = atom.Tooltip, TooltipId = tooltipId, ColorSpecified = atom.ColorSpecified, RawInput = atom.Value }), Alignment = atom.Alignment, NoWrap = atom.NoWrap || target.lines[^1].NoWrap });
                    target.revision++;
                }
                else
                {
                    if (!atom.Text.Contains('\n'))
                    {
                        if (atom.Text.Length == 0) continue;
                        if (pendingRuns is null)
                        {
                            pendingRuns = target.lines[^1].Runs.ToBuilder();
                            pendingNoWrap = target.lines[^1].NoWrap;
                        }
                        pendingRuns.Add(new(atom.Text, target.Color, id, value, target.Source)
                        { Background = target.TextBackground, Style = target.Style, Font = target.Font, Tooltip = atom.Tooltip,
                            TooltipId = tooltipId, ColorSpecified = atom.ColorSpecified, RawInput = atom.Value, ExportHtml = atom.ExportHtml });
                        pendingAlignment = target.Alignment; pendingNoWrap |= atom.NoWrap;
                        target.revision++;
                        continue;
                    }
                    string[] pieces = atom.Text.Split('\n');
                    for (int part = 0; part < pieces.Length; part++)
                    {
                        if (part > 0) target.HtmlBreak(atom.NoWrap);
                        target.Text(pieces[part], id: id, value: value, noWrap: atom.NoWrap, title: atom.Tooltip, tooltipId: tooltipId, colorSpecified: atom.ColorSpecified, rawInput: atom.Value, exportHtml: atom.ExportHtml);
                    }
                }
            }
            FlushText();
        }
        Render(this, atoms);
        Color = previous; Style = previousStyle; Alignment = previousAlignment; Font = previousFont;
        if (!toBuffer && !IsPrintingBufferEmpty) NewLine();
    }
}
