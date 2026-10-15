using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

public static class EraBuiltins
{
    private readonly struct ArgumentValues : IEnumerable<EraValue>
    {
        // 运行时直接读取已打包的参数；常量求值沿用编译器提供的值数组。
        private readonly ImmutableArray<BuiltinArgument> arguments;
        private readonly ImmutableArray<EraValue> constants;
        public ArgumentValues(ImmutableArray<BuiltinArgument> arguments) { this.arguments = arguments; constants = default; }
        public ArgumentValues(ImmutableArray<EraValue> constants) { this.constants = constants; arguments = default; }
        public int Length => arguments.IsDefault ? constants.Length : arguments.Length;
        public EraValue this[int index] => arguments.IsDefault ? constants[index] : arguments[index].Value;
        public bool Any(Func<EraValue, bool> predicate)
        {
            for (int i = 0; i < Length; i++) if (predicate(this[i])) return true;
            return false;
        }
        public IEnumerator<EraValue> GetEnumerator()
        {
            for (int i = 0; i < Length; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private static EraValue Unicode(long value, IBuiltinContext? context = null)
    {
        if (value < 0 || value > 0xFFFF) throw new ScriptException("UNICODE", "UTF-16 码元越界");
        if (value < 0x1f && value is not (0x0a or 0x0d) || value is >= 0x7f and <= 0x9f)
        { context?.WarnOnce("UNICODE_CONTROL_" + value, "UNICODE 控制字符返回空串"); return new(""); }
        return new(((char)value).ToString());
    }
    private static int ColorValue(ArgumentValues arguments)
    {
        if (arguments.Length is not (1 or 3)) throw new ScriptException("ARGUMENT_COUNT", "颜色需要一个整数或三个 RGB 分量");
        if (arguments.Length == 1) return (int)(arguments[0].AsInt() & 0xffffff);
        if (arguments.Any(v => v.AsInt() < 0 || v.AsInt() > 255)) throw new ScriptException("COLOR", "颜色值越界");
        return (int)(arguments[0].AsInt() << 16 | arguments[1].AsInt() << 8 | arguments[2].AsInt());
    }
    private sealed class FillHandler : IBuiltinHandler
    {
        public string Id => "era.VARSET";
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
        { context.Fill(arguments[0].Reference!, arguments[1].Value, arguments[2].Value.AsInt(), arguments[3].Value.AsInt()); return null; }
    }
    private sealed record Handler(string Id, Func<IBuiltinContext, ArgumentValues, EraValue?> Run) : IBuiltinHandler
    { public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments) => Run(context, new(arguments)); }
    public static BuiltinLibrary Library { get; } = Create();
    private static BuiltinLibrary Create()
    {
        var signatures = new List<BuiltinSignature>(); var handlers = new List<IBuiltinHandler>();
        signatures.Add(new("CALLSHARP", "era.CALLSHARP", [new(ValueKind.String)], null, BuiltinEffect.Flow,
            Variadic: new(null, CaptureReference: true)));
        handlers.Add(new PluginHandler());
        void Add(string name, ValueKind? result, BuiltinEffect effect, ValueKind?[] parameters,
            Func<IBuiltinContext, ArgumentValues, EraValue?> action)
        {
            string id = "era." + name;
            signatures.Add(new(name, id, parameters.Select(k => new BuiltinParameter(k)).ToImmutableArray(), result, effect));
            handlers.Add(new Handler(id, action));
        }
        Add("PRINT", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.Emit(a[0].Text, false); return null; });
        Add("SKIPDISP", ValueKind.Integer, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.SkipDisplay = a[0].AsInt() != 0; return new(c.SkipDisplay ? 1L : 0L); });
        Add("ISSKIP", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.SkipDisplay ? 1L : 0L));
        Add("$DISPLAY_ENABLED", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.SkipDisplay ? 0L : 1L));
        Add("$INPUT_ALLOWED", null, BuiltinEffect.Query, [], (c, _) => { if (c.SkipDisplay) throw new ScriptException("SKIPDISP_INPUT", "SKIPDISP 期间不能请求输入"); return null; });
        Add("$SKIP_CONTROL", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.SkipDisplay = a[0].AsInt() != 0; return null; });
        Add("PRINTPLAIN", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.EmitPlain(a[0].Text); return null; });
        Add("REUSELASTLINE", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.EmitTemporary(a[0].Text); return null; });
        Add("$PRINT_COLUMN", null, BuiltinEffect.Output, [ValueKind.String, ValueKind.Integer, ValueKind.Integer], (c, a) => { c.EmitColumn(a[0].Text, a[1].AsInt() != 0, a[2].AsInt() != 0); return null; });
        Add("PRINTCLENGTH", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.PrintColumnWidth));
        Add("PRINTCPERLINE", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.PrintColumnsPerLine));
        Add("THROW", null, BuiltinEffect.Flow, [ValueKind.String], (_, a) => throw new ScriptException("SCRIPT_THROW", a[0].AsString()));
        Add("ARRAYCOPY", null, BuiltinEffect.Query, [ValueKind.String, ValueKind.String], (c, a) => { c.CopyArray(a[0].AsString(), a[1].AsString()); return null; });
        Add("PRINTL", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.Emit(a[0].Text, true); return null; });
        Add("PRINTD", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.EmitDefault(a[0].Text, false); return null; });
        Add("PRINTDL", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.EmitDefault(a[0].Text, true); return null; });
        Add("ALIGNMENT", null, BuiltinEffect.Output, [ValueKind.String], (c, a) =>
        { if (!Enum.TryParse<TextAlignment>(a[0].AsString(), true, out var alignment)) throw new ScriptException("ALIGNMENT", "对齐方式无效"); c.Align(alignment); return null; });
        Add("REDRAW", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.Redraw(a[0].AsInt()); return null; });
        Add("CURRENTREDRAW", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.RedrawEnabled ? 1L : 0L));
        Add("MOUSEB", ValueKind.String, BuiltinEffect.Query, [], (c, _) => new(c.HoveredButton));
        Add("CLIENTWIDTH", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.ClientWidth));
        Add("CLIENTHEIGHT", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.ClientHeight));
        Add("GETCONFIG", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) => new(a[0].AsString().ToUpperInvariant() switch
        {
            "FONT SIZE" or "フォントサイズ" or "FONTSIZE" or "LINE HEIGHT" or "一行の高さ" or "LINEHEIGHT" => c.FontSize,
            "FPS" or "フレーム毎秒" or "FRAMESPERSECOND" => c.FramesPerSecond,
            _ => c.ConfigValue(a[0].AsString()).AsInt()
        }));
        Add("HTML_STRINGLEN", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) => c.HtmlLength(a[0].AsString(), a[1].AsInt() != 0));
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("$HTML_MEASURE_PIXELS", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) => c.HtmlLength(a[0].AsString(), true));
        Add("$HTML_MEASURE_UNITS", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer, ValueKind.Integer], (c, a) =>
        {
            long width = a[0].AsInt(); if (a[1].AsInt() != 0) return new(width);
            int cell = c.FontSize / 2; if (cell <= 0) throw new ScriptException("DISPLAY_METRICS", "宿主未提供有效文本测量");
            long quotient = width / cell; return new(width % cell == 0 ? quotient : quotient + (width > 0 ? 1 : -1));
        });
        Add("HTML_POPPRINTINGSTR", ValueKind.String, BuiltinEffect.Output, [], (c, _) => new(c.PopPrintingHtml()));
        Add("GETDISPLAYLINE", ValueKind.String, BuiltinEffect.Query, [ValueKind.Integer], (c, a) => new(c.DisplayLine(a[0].AsInt())));
        Add("SETANIMETIMER", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.SetAnimationTimer(a[0].AsInt()); return null; });
        Add("GETANIMETIMER", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.AnimationInterval));
        Add("GETDOINGFUNCTION", ValueKind.String, BuiltinEffect.Query, [], (c, _) => new(c.CurrentFunction));
        Add("LINEISEMPTY", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.LineIsEmpty ? 1L : 0L));
        Add("COLOR_FROMRGB", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer, ValueKind.Integer, ValueKind.Integer], (_, a) => new((long)ColorValue(a)));
        Add("$RGB_COMPONENT", null, BuiltinEffect.Query, [ValueKind.Integer], (_, a) =>
        { if (a[0].AsInt() is < 0 or > 255) throw new ScriptException("COLOR", "颜色值越界"); return null; });
        Add("$CONVERT_BASE", null, BuiltinEffect.Query, [ValueKind.Integer], (_, a) =>
        { if (a[0].AsInt() is not (2 or 8 or 10 or 16)) throw new ScriptException("CONVERT_BASE", "CONVERT 进制必须为 2、8、10 或 16"); return null; });
        Add("$BIT_INDEX", null, BuiltinEffect.Query, [ValueKind.Integer], (_, a) =>
        { if (a[0].AsInt() is < 0 or > 63) throw new ScriptException("BIT_INDEX", "位下标越界"); return null; });
        Add("$GRAPHICS_INT", null, BuiltinEffect.Query, [ValueKind.Integer, ValueKind.Integer], (_, a) =>
        { if (a[0].AsInt() < int.MinValue || a[0].AsInt() > int.MaxValue || a[1].AsInt() != 0 && a[0].AsInt() == 0) throw new ScriptException("GRAPHICS_RANGE", "图形坐标或尺寸越界"); return null; });
        Add("$GRAPHICS_COLOR", null, BuiltinEffect.Query, [ValueKind.Integer], (_, a) =>
        { if (a[0].AsInt() < 0 || a[0].AsInt() > uint.MaxValue) throw new ScriptException("GRAPHICS_COLOR", "图形颜色越界"); return null; });
        Add("$GRAPHICS_ID", null, BuiltinEffect.Query, [ValueKind.Integer], (_, a) =>
        { if (a[0].AsInt() < 0 || a[0].AsInt() > int.MaxValue) throw new ScriptException("GRAPHICS_ID", "图形编号越界"); return null; });
        Add("$HTML_LINE_SPLIT", ValueKind.String, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) => c.HtmlLineRemainder(a[0].AsString(), a[1].AsInt()));
        Add("$HTML_LINE_PROGRESS", null, BuiltinEffect.Query, [ValueKind.String, ValueKind.String, ValueKind.Integer], (_, a) =>
        { if (a[0].AsString() == a[1].AsString() || a[2].AsInt() >= 1_000_000) throw new ScriptException("HTML_WIDTH", "HTML 无法在指定宽度内继续截取"); return null; });
        Add("CUSTOMDRAWLINE", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.DrawLine(a[0].AsString()); return null; });
        Add("DRAWLINE", null, BuiltinEffect.Output, [], (c, _) => { c.DrawLine(c.DefaultLine); return null; });
        Add("DRAWLINESTR", ValueKind.String, BuiltinEffect.Query, [], (c, _) => new(c.DefaultLine));
        Add("GETCOLOR", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.ForegroundColor));
        Add("GETDEFCOLOR", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.DefaultForegroundColor));
        Add("GETFOCUSCOLOR", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.FocusColor));
        Add("HTML_GETPRINTEDSTR", ValueKind.String, BuiltinEffect.Query, [ValueKind.Integer], (c, a) => new(c.PrintedHtml(a[0].AsInt())));
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("HTML_SUBSTRING", ValueKind.String, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) => c.SplitHtml(a[0].AsString(), a[1].AsInt(), false));
        Add("HTML_STRINGLINES", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) => c.SplitHtml(a[0].AsString(), a[1].AsInt(), true));
        Add("MOUSEX", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.MouseX));
        Add("MOUSEY", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.MouseY));
        Add("GETDEFBGCOLOR", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.DefaultBackgroundColor));
        Add("GETBGCOLOR", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.CurrentBackgroundColor));
        Add("GETSTYLE", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.TextStyle));
        Add("FONTBOLD", null, BuiltinEffect.Output, [], (c, _) => { c.TextStyle |= 1; return null; });
        Add("FONTITALIC", null, BuiltinEffect.Output, [], (c, _) => { c.TextStyle |= 2; return null; });
        Add("FONTREGULAR", null, BuiltinEffect.Output, [], (c, _) => { c.TextStyle = 0; return null; });
        foreach (string name in new[] { "TOOLTIP_SETDELAY", "TOOLTIP_SETDURATION" })
            Add(name, null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) =>
            {
                long value = a[0].AsInt(); if (value < 0 || value > int.MaxValue) throw new ScriptException("TOOLTIP_RANGE", "悬浮说明时间越界");
                c.Tooltip = name == "TOOLTIP_SETDELAY" ? c.Tooltip with { Delay = (int)value } : c.Tooltip with { Duration = (int)Math.Min(short.MaxValue, value) }; return null;
            });
        Add("TOOLTIP_SETCOLOR", null, BuiltinEffect.Output, [ValueKind.Integer, ValueKind.Integer], (c, a) =>
        {
            if (a.Any(v => v.AsInt() is < 0 or > 0xffffff)) throw new ScriptException("TOOLTIP_COLOR", "悬浮说明颜色越界");
            c.Tooltip = c.Tooltip with { Foreground = (int)a[0].AsInt(), Background = (int)a[1].AsInt() }; return null;
        });
        Add("SETFONT", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.FontName = a[0].AsString(); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String, Default: new EraValue(""))] };
        Add("GETFONT", ValueKind.String, BuiltinEffect.Query, [], (c, _) => new(c.FontName));
        Add("CURRENTALIGN", ValueKind.String, BuiltinEffect.Query, [], (c, _) => new(c.CurrentAlignment.ToString().ToUpperInvariant()));
        Add("FONTSTYLE", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) =>
        { c.TextStyle = (int)(a[0].AsInt() & 15); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("HTML_PRINT", null, BuiltinEffect.Output, [ValueKind.String, ValueKind.Integer], (c, a) => { c.Html(a[0].Text, a[1].AsInt() != 0); return null; });
        foreach (string name in new[] { "HTML_PRINTC", "HTML_PRINTLC" })
        {
            Add(name, null, BuiltinEffect.Output, [ValueKind.String, ValueKind.Integer], (c, a) => { c.HtmlColumn(a[0].AsString(), a[1].AsInt(), name == "HTML_PRINTC"); return null; });
            signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))] };
        }
        Add("PRINTBUTTON", null, BuiltinEffect.Output, [ValueKind.String, null], (c, a) => { c.Button(a[0].Text, a[1]); return null; });
        foreach (string name in new[] { "PRINTBUTTONC", "PRINTBUTTONLC" })
            Add(name, null, BuiltinEffect.Output, [ValueKind.String, null], (c, a) => { c.ButtonColumn(a[0].AsString(), a[1], name == "PRINTBUTTONC"); return null; });
        InputBuiltins.Register(signatures, handlers);
        Add("WAIT", null, BuiltinEffect.Input, [], (c, _) => { c.Input(InputKind.Wait); return null; });
        Add("FORCEWAIT", null, BuiltinEffect.Input, [], (c, _) => { c.MessageSkip = false; c.Input(InputKind.Wait); return null; });
        Add("$DEBUG_CLEAR", null, BuiltinEffect.Output, [], (c, _) => { c.DebugClear(); return null; });
        Add("CLEARLINE", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.Clear(a[0].Integer); return null; });
        Add("LINECOUNT", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new((long)c.LineCount));
        Add("SETCOLOR", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) =>
        { c.Color(ColorValue(a)); return null; });
        signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.Integer) };
        Add("RESETCOLOR", null, BuiltinEffect.Output, [], (c, _) => { c.Color(null); return null; });
        foreach (string name in new[] { "SETCOLORBYNAME", "SETBGCOLORBYNAME" })
            Add(name, null, BuiltinEffect.Output, [ValueKind.String], (c, a) =>
            {
                var color = System.Drawing.Color.FromName(a[0].AsString());
                if (color.A == 0) throw new ScriptException("COLOR", "颜色名无效或为透明色");
                int rgb = color.R << 16 | color.G << 8 | color.B;
                if (name == "SETCOLORBYNAME") c.Color(rgb); else c.BackgroundColor(rgb);
                return null;
            });
        Add("$PRINT_SINGLE", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.EmitSingle(a[0].AsString()); return null; });
        Add("FORCEKANA", null, BuiltinEffect.Flow, [ValueKind.Integer], (c, a) =>
        {
            long mode = a[0].AsInt();
            if (mode is < 0 or > 3) throw new ScriptException("KANA_MODE", "假名模式必须为 0 到 3");
            c.KanaMode = (int)mode; return null;
        });
        Add("SETBGCOLOR", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) =>
        { c.BackgroundColor(ColorValue(a)); return null; });
        signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.Integer) };
        Add("RESETBGCOLOR", null, BuiltinEffect.Output, [], (c, _) => { c.BackgroundColor(null); return null; });
        Add("TEXT_BGC_OFF", null, BuiltinEffect.Output, [], (c, _) => { c.TextBackground(null); return null; });
        Add("TEXT_BGC_ON", null, BuiltinEffect.Output, [ValueKind.Integer, ValueKind.Integer], (c, a) =>
        {
            long color = a[0].AsInt(), alpha = a[1].AsInt();
            if (color is < 0 or > 0xffffff || alpha is < 0 or > 100) throw new ScriptException("COLOR", "文字背景颜色或透明度越界");
            c.TextBackground((uint)(color | alpha * 255 / 100 << 24)); return null;
        });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(100L))] };
        foreach (var (name, operation) in new[] { ("STOPBGM", MediaOperation.StopBgm), ("STOPSOUND", MediaOperation.StopSound) })
            Add(name, null, BuiltinEffect.Output, [], (c, _) => { c.Defer(ServiceKind.Media, new MediaPayload(operation)); return null; });
        Add("PLAYBGM", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.Defer(ServiceKind.Media, new MediaPayload(MediaOperation.PlayBgm, a[0].AsString())); return null; });
        foreach (var (name, operation) in new[] { ("SETSOUNDVOLUME", MediaOperation.SoundVolume), ("SETBGMVOLUME", MediaOperation.BgmVolume) })
            Add(name, null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.Defer(ServiceKind.Media, new MediaPayload(operation, Repeat: unchecked((int)a[0].AsInt()))); return null; });
        signatures.Add(new("GETSOUNDORBGMINFO", "era.GETSOUNDORBGMINFO", [new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new MediaInfoHandler());
        Add("$MEDIA_CHANNEL", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer], (_, a) =>
        { int channel = unchecked((int)a[0].AsInt()); return new(channel is >= -1 and < 10 ? 1L : 0L); });
        Add("PLAYSOUND", null, BuiltinEffect.Output, [ValueKind.String, ValueKind.Integer], (c, a) => { c.Defer(ServiceKind.Media, new MediaPayload(MediaOperation.PlaySound, a[0].AsString(), a[1].AsInt())); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(1L))] };
        Add("SAVEDATA", null, BuiltinEffect.Persistence, [ValueKind.Integer, ValueKind.String], (c, a) => { c.Save(Slot(a[0]), a[1].Text); return null; });
        Add("LOADDATA", null, BuiltinEffect.Persistence, [ValueKind.Integer], (c, a) => { c.Load(Slot(a[0])); return null; });
        Add("DELDATA", null, BuiltinEffect.Persistence, [ValueKind.Integer], (c, a) => { c.Defer(ServiceKind.Save, new DeleteSavePayload(Slot(a[0]))); return null; });
        Add("SAVECHARA", null, BuiltinEffect.Persistence, [ValueKind.String, ValueKind.String, ValueKind.Integer],
            (c, a) => { c.CharacterFile(CharacterFileOperation.Save, a[0].AsString(), a[1].AsString(), a.Skip(2).Select(v => v.AsInt()).ToArray()); return null; });
        signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.Integer) };
        Add("LOADCHARA", null, BuiltinEffect.Persistence, [ValueKind.String], (c, a) => { c.CharacterFile(CharacterFileOperation.Load, a[0].AsString()); return null; });
        Add("CHKCHARADATA", ValueKind.Integer, BuiltinEffect.Persistence, [ValueKind.String], (c, a) => { c.CharacterFile(CharacterFileOperation.Check, a[0].AsString()); return null; });
        Add("FIND_CHARADATA", ValueKind.Integer, BuiltinEffect.Persistence, [ValueKind.String], (c, a) => { c.CharacterFile(CharacterFileOperation.Find, a[0].AsString()); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String, Default: new EraValue("*"))] };
        Add("CHKDATA", ValueKind.Integer, BuiltinEffect.Persistence, [ValueKind.Integer], (c, a) => { c.CheckSave(Slot(a[0])); return null; });
        Add("LOADGLOBAL", null, BuiltinEffect.Persistence, [], (c, _) => { c.GlobalData(true); return null; });
        Add("SAVEGLOBAL", null, BuiltinEffect.Persistence, [], (c, _) => { c.GlobalData(false); return null; });
        Add("BEGIN_TITLE", null, BuiltinEffect.Flow, [], (c, _) => { c.Restart(); return null; });
        foreach (var mode in Enum.GetValues<SystemMode>().Where(m => m != SystemMode.Title))
            Add("BEGIN_" + mode.ToString().ToUpperInvariant(), null, BuiltinEffect.Flow, [], (c, _) => { c.Begin(mode); return null; });
        Add("QUIT", null, BuiltinEffect.Flow, [], (c, _) => { c.Quit(); return null; });
        Add("DOTRAIN", null, BuiltinEffect.Flow, [ValueKind.Integer], (c, a) => { c.DoTrain(a[0].AsInt()); return null; });
        Add("GETTEXTBOX", ValueKind.String, BuiltinEffect.Query, [], (c, _) => new(c.TextBox));
        Add("BITMAP_CACHE_ENABLE", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.CachePrintedLines = a[0].AsInt() != 0; return null; });
        Add("CLEARIMAGELAYER", null, BuiltinEffect.Output, [ValueKind.Integer], (c, a) => { c.ClearImageLayers(a[0].AsInt()); return null; });
        Add("CLEARIMAGELAYER_ALL", null, BuiltinEffect.Output, [], (c, _) => { c.ClearImageLayers(null); return null; });
        Add("EXISTSIMAGELAYER", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer], (c, a) => new(c.HasImageLayer(a[0].AsInt()) ? 1L : 0L));
        Add("GETKEY", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer], (c, a) => new(c.KeyState(a[0].AsInt(), false) ? 1L : 0L));
        Add("$KEY_INPUT_ACTIVE", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.KeyInputActive ? 1L : 0L));
        Add("GETKEYTRIGGERED", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer], (c, a) => new(c.KeyState(a[0].AsInt(), true) ? 1L : 0L));
        Add("SETTEXTBOX", ValueKind.Integer, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.TextBox = a[0].AsString(); return new(1L); });
        Add("CLEARTEXTBOX", null, BuiltinEffect.Output, [], (c, _) => { c.TextBox = ""; return null; });
        Add("SEQUENCEINPUT", ValueKind.Integer, BuiltinEffect.Flow, [ValueKind.String], (c, a) => { c.SequenceInput(a[0].AsString()); return new(0L); });
        Add("DISABLE_INPUT_MACRO", ValueKind.Integer, BuiltinEffect.Flow, [], (c, _) => { c.InputMacroEnabled = false; return new(0L); });
        Add("ENABLE_INPUT_MACRO", ValueKind.Integer, BuiltinEffect.Flow, [], (c, _) => { c.InputMacroEnabled = true; return new(0L); });
        Add("MESSKIP", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.MessageSkip ? 1L : 0L));
        signatures.Add(new("RAND", "era.RAND", [new(null), new(null, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query, NumericResult: true));
        handlers.Add(new RandomHandler());
        signatures.Add(new("TIMES", "era.TIMES", [new(null, Reference: true), new(null)], null, BuiltinEffect.Flow, NumericResult: true));
        handlers.Add(new TimesHandler());
        Add("CHARANUM", ValueKind.Integer, BuiltinEffect.Query, [], (c, _) => new(c.CharacterCount));
        Add("GETMILLISECOND", ValueKind.Integer, BuiltinEffect.Query, [], (_, _) => new(DateTime.Now.Ticks / 10000));
        Add("GETTIME", ValueKind.Integer, BuiltinEffect.Query, [], (_, _) => new(long.Parse(DateTime.Now.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)));
        Add("GETTIMES", ValueKind.String, BuiltinEffect.Query, [], (_, _) => new(DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        Add("CLEARMEMORY", ValueKind.Integer, BuiltinEffect.Query, [], (_, _) =>
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess(); long before = process.WorkingSet64;
            GC.Collect(); process.Refresh(); return new(before - process.WorkingSet64);
        });
        Add("GETPLATFORM", ValueKind.Integer, BuiltinEffect.Query, [], (_, _) => new(OperatingSystem.IsWindows() ? 0L : OperatingSystem.IsMacOS() ? 3L : OperatingSystem.IsLinux() ? 4L : 5L));
        foreach (var (name, operation) in new[] { ("ADDCHARA", CharacterOperation.Add), ("ADDCHARACOPY", CharacterOperation.AddCopy), ("ADDCOPYCHARA", CharacterOperation.AddCopy) })
        {
            Add(name, null, BuiltinEffect.Flow, [ValueKind.Integer], (c, a) =>
            { foreach (var value in a) c.Characters(operation, value.AsInt()); return null; });
            signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.Integer) };
        }
        Add("DELCHARA", null, BuiltinEffect.Flow, [ValueKind.Integer], (c, a) =>
        {
            var indices = a.Select(v => v.AsInt()).ToArray();
            if (indices.Any(i => i < 0 || i >= c.CharacterCount) || indices.Distinct().Count() != indices.Length)
                throw new ScriptException("CHARACTER_BOUNDS", "删除角色名单越界或重复");
            foreach (long index in indices.OrderDescending()) c.Characters(CharacterOperation.Delete, index);
            return null;
        });
        signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.Integer) };
        foreach (var (name, operation) in new[] { ("COPYCHARA", CharacterOperation.Copy), ("SWAPCHARA", CharacterOperation.Swap) })
            Add(name, null, BuiltinEffect.Flow, [ValueKind.Integer, ValueKind.Integer], (c, a) => { c.Characters(operation, a[0].AsInt(), a[1].AsInt()); return null; });
        Add("DELALLCHARA", null, BuiltinEffect.Flow, [], (c, _) => { c.Characters(CharacterOperation.Clear); return null; });
        Add("RESETDATA", null, BuiltinEffect.Flow, [], (c, _) => { c.ResetData(); return null; });
        Add("RESET_STAIN", null, BuiltinEffect.Flow, [ValueKind.Integer], (c, a) => { c.ResetStain(a[0].AsInt()); return null; });
        Add("STRFORM", ValueKind.String, BuiltinEffect.Query, [ValueKind.String], (c, a) => { c.DynamicFormat(a[0].AsString()); return null; });
        Add("RANDOMIZE", null, BuiltinEffect.Flow, [ValueKind.Integer], (c, a) => { c.SeedRandom(a[0].AsInt()); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("DUMPRAND", null, BuiltinEffect.Flow, [], (c, _) => { c.RandomData(false); return null; });
        Add("INITRAND", null, BuiltinEffect.Flow, [], (c, _) => { c.RandomData(true); return null; });
        Add("EXISTFUNCTION", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) => new(c.FunctionKind(a[0].AsString(), a[1].AsInt() != 0)));
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("EXISTVAR", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) => c.ExistsVariable(a[0].AsString(), a[1].AsInt()));
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))] };
        signatures.Add(new("VARSET", "era.VARSET", [new(null, Reference: true), new(null), new(ValueKind.Integer), new(ValueKind.Integer)], null, BuiltinEffect.Flow));
        handlers.Add(new FillHandler());
        SqlBuiltins.Register(signatures, handlers);
        var constants = new Dictionary<string, Func<ImmutableArray<EraValue>, EraValue>>(StringComparer.OrdinalIgnoreCase);
        void Pure(string name, ValueKind result, ValueKind?[] parameters, Func<ArgumentValues, EraValue> run)
        { constants.Add(name, values => run(new(values))); Add(name, result, BuiltinEffect.Query, parameters, (_, a) => run(a)); }
        Pure("SQL_ESCAPE", ValueKind.String, [ValueKind.String], a => new(a[0].AsString().Replace("'", "''", StringComparison.Ordinal)));
        Pure("HTML_ESCAPE", ValueKind.String, [ValueKind.String], a => new(System.Security.SecurityElement.Escape(a[0].AsString())!));
        Pure("TOLOWER", ValueKind.String, [ValueKind.String], a => new(a[0].AsString().ToLowerInvariant()));
        Pure("TOUPPER", ValueKind.String, [ValueKind.String], a => new(a[0].AsString().ToUpperInvariant()));
        Pure("STRCOUNT", ValueKind.Integer, [ValueKind.String, ValueKind.String], a =>
        {
            try { return new((long)System.Text.RegularExpressions.Regex.Matches(a[0].AsString(), a[1].AsString(),
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Count); }
            catch (ArgumentException) { throw new ScriptException("REGEX", "STRCOUNT 正则表达式无效"); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { throw new ScriptException("REGEX_TIMEOUT", "STRCOUNT 正则表达式执行超时"); }
        });
        Pure("GETBIT", ValueKind.Integer, [ValueKind.Integer, ValueKind.Integer], a =>
        {
            long bit = a[1].AsInt(); if (bit is < 0 or > 63) throw new ScriptException("BIT_INDEX", "位下标越界");
            return new((a[0].AsInt() >> (int)bit) & 1);
        });
        Pure("GROUPMATCH", ValueKind.Integer, [null, null], a => new((long)a.Skip(1).Count(v => v == a[0])));
        signatures[^1] = signatures[^1] with { Variadic = new(null), SameType = true };
        foreach (string name in new[] { "LOG", "LOG10", "CBRT", "SIGN" })
        {
            Pure(name, ValueKind.Integer, [null], a =>
            {
                double value = a[0].AsFloat();
                if ((name is "LOG" or "LOG10") && value <= 0 || name == "CBRT" && value < 0)
                    throw new ScriptException("MATH_RANGE", name + " 参数超出定义域");
                double result = name switch { "LOG" => Math.Log(value), "LOG10" => Math.Log10(value), "CBRT" => Math.Pow(value, 1.0 / 3.0), _ => Math.Sign(value) };
                if (!double.IsFinite(result)) throw new ScriptException("MATH_RANGE", name + " 结果不是有限数");
                return a[0].Kind == ValueKind.Float ? new EraValue(result) : new EraValue((long)result);
            });
            signatures[^1] = signatures[^1] with { NumericResult = true };
        }
        Pure("UNCHECKED_ADD", ValueKind.Integer, [ValueKind.Integer, ValueKind.Integer], a => new(unchecked(a[0].AsInt() + a[1].AsInt())));
        Pure("UNCHECKED_MUL", ValueKind.Integer, [ValueKind.Integer, ValueKind.Integer], a => new(unchecked(a[0].AsInt() * a[1].AsInt())));
        Pure("UNCHECKED_SUB", ValueKind.Integer, [ValueKind.Integer, ValueKind.Integer], a => new(unchecked(a[0].AsInt() - a[1].AsInt())));
        Pure("UNCHECKED_NEG", ValueKind.Integer, [ValueKind.Integer], a => new(unchecked(-a[0].AsInt())));
        Pure("CONVERT", ValueKind.String, [ValueKind.Integer, ValueKind.Integer], a =>
        {
            long radix = a[1].AsInt();
            if (radix is not (2 or 8 or 10 or 16)) throw new ScriptException("CONVERT_BASE", "CONVERT 进制必须为 2、8、10 或 16");
            return new(Convert.ToString(a[0].AsInt(), (int)radix));
        });
        Pure("CHARATU", ValueKind.String, [ValueKind.String, ValueKind.Integer], a =>
        {
            string value = a[0].AsString(); long position = a[1].AsInt();
            return new(position < 0 || position >= value.Length ? "" : value[(int)position].ToString());
        });
        Pure("ENCODETOUNI", ValueKind.Integer, [ValueKind.String, ValueKind.Integer], a =>
        {
            string value = a[0].AsString(); long position = a[1].AsInt();
            if (value.Length == 0) return new(-1L);
            if (position < 0 || position >= value.Length) throw new ScriptException("BOUNDS", "ENCODETOUNI 下标越界");
            try { return new((long)char.ConvertToUtf32(value, (int)position)); }
            catch (ArgumentException) { throw new ScriptException("UNICODE", "ENCODETOUNI 遇到无效代理字符"); }
        });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))] };
        Pure("ALLSAMES", ValueKind.Integer, [null, null], a => new(a.Skip(1).All(v => v == a[0]) ? 1L : 0L));
        signatures[^1] = signatures[^1] with { Variadic = new(null), SameType = true };
        Pure("NOSAMES", ValueKind.Integer, [null, null], a => new(a.Distinct().Count() == a.Length ? 1L : 0L));
        signatures[^1] = signatures[^1] with { Variadic = new(null), SameType = true };
        Pure("ABS", ValueKind.Integer, [null], a =>
        {
            if (a[0].Kind == ValueKind.Float) return new(Math.Abs(a[0].AsFloat()));
            long value = a[0].AsInt();
            if (value == long.MinValue) throw new ScriptException("ABS_RANGE", "Int64 最小值不能取绝对值");
            return new(Math.Abs(value));
        });
        signatures[^1] = signatures[^1] with { NumericResult = true };
        Pure("SQRT", ValueKind.Integer, [null], a =>
        {
            double value = a[0].AsFloat();
            if (value < 0) throw new ScriptException("SQRT_RANGE", "SQRT 参数不能为负数");
            double result = Math.Sqrt(value);
            return a[0].Kind == ValueKind.Float ? new EraValue(result) : new EraValue((long)result);
        });
        signatures[^1] = signatures[^1] with { NumericResult = true };
        foreach (string name in new[] { "MAX", "MIN" })
        {
            Pure(name, ValueKind.Integer, [null], a =>
            {
                double result = a[0].AsFloat();
                foreach (var value in a.Skip(1)) result = name == "MAX" ? Math.Max(result, value.AsFloat()) : Math.Min(result, value.AsFloat());
                return a.Any(v => v.Kind == ValueKind.Float) ? new EraValue(result) : new EraValue(unchecked((long)result));
            });
            signatures[^1] = signatures[^1] with { Variadic = new(null), NumericResult = true };
        }
        Add("UNICODE", ValueKind.String, BuiltinEffect.Query, [ValueKind.Integer], (c, a) => Unicode(a[0].AsInt(), c));
        constants.Add("UNICODE", a => Unicode(a[0].AsInt()));
        Pure("POWER", ValueKind.Integer, [null, null], a =>
        {
            bool floating = a.Any(v => v.Kind == ValueKind.Float);
            double value = Math.Pow(a[0].AsFloat(), a[1].AsFloat());
            if (!double.IsFinite(value) || !floating && (value >= long.MaxValue || value <= long.MinValue))
                throw new ScriptException("POWER_RANGE", "POWER 结果非有限数或超出整数范围");
            return floating ? new EraValue(value) : new EraValue((long)value);
        });
        signatures[^1] = signatures[^1] with { NumericResult = true };
        Pure("LIMIT", ValueKind.Integer, [null, null, null], a =>
            a.Any(v => v.Kind == ValueKind.Float) ? new EraValue(a[0].AsFloat() < a[1].AsFloat() ? a[1].AsFloat() : a[0].AsFloat() > a[2].AsFloat() ? a[2].AsFloat() : a[0].AsFloat()) :
            new EraValue(a[0].AsInt() < a[1].AsInt() ? a[1].AsInt() : a[0].AsInt() > a[2].AsInt() ? a[2].AsInt() : a[0].AsInt()));
        signatures[^1] = signatures[^1] with { NumericResult = true };
        Pure("INRANGE", ValueKind.Integer, [ValueKind.Integer, ValueKind.Integer, ValueKind.Integer], a =>
            new(a[0].AsInt() >= a[1].AsInt() && a[0].AsInt() <= a[2].AsInt() ? 1L : 0L));
        Pure("ISNUMERIC", ValueKind.Integer, [ValueKind.String], a => new(NumericText.Parse(a[0].AsString(), true, out _) ? 1L : 0L));
        Pure("TOINT", ValueKind.Integer, [ValueKind.String], a => NumericText.ToInt(a[0]));
        signatures[^1] = signatures[^1] with { Parameters = [new(null)] };
        Pure("TOSTR", ValueKind.String, [ValueKind.Integer, ValueKind.String], a =>
        {
            try { return new(a[0].AsInt().ToString(a[1].AsString(), System.Globalization.CultureInfo.InvariantCulture)); }
            catch (FormatException) { throw new ScriptException("FORMAT", "TOSTR 格式无效"); }
        });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.Integer), new(ValueKind.String, Default: new EraValue(""))] };
        Pure("COLOR_FROMNAME", ValueKind.Integer, [ValueKind.String], a =>
        {
            string name = a[0].AsString();
            if (name.Equals("transparent", StringComparison.OrdinalIgnoreCase)) throw new ScriptException("COLOR", "不支持透明色");
            var color = System.Drawing.Color.FromName(name);
            return new(color.A == 0 ? -1L : (color.R << 16) + (color.G << 8) + color.B);
        });
        GraphicsBuiltins.Register(signatures, handlers);
        MapBuiltins.Register(signatures, handlers);
        Add("$METHOD_EXISTS", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) => new(c.MethodExists(a[0].AsString()) ? 1L : 0L));
        Add("$METHOD_RETURN", null, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) =>
        { if (c.FunctionKind(a[0].AsString(), false) != a[1].AsInt()) throw new ScriptException("FUNCTION_TYPE", "动态函数返回类型不符"); return null; });
        Add("$INT32_MODE", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.Integer], (_, a) => new((long)unchecked((int)a[0].AsInt())));
        Add("$CALL_ARITY", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) => new((long)c.CallableArity(a[0].AsString())));
        Add("$CALL_REFERENCE", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer],
            (c, a) => new(c.CallableArrayReference(a[0].AsString(), checked((int)a[1].AsInt())) ? 1L : 0L));
        Add("$CALL_TARGET", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) =>
        {
            long kind = c.FunctionKind(a[0].AsString(), false);
            if (kind != 0 && (kind != 1) != (a[1].AsInt() != 0))
                throw new ScriptException("FUNCTION_TYPE", a[1].AsInt() != 0 ? "表达式调用需要 FUNCTION 属性" : "普通调用不能调用表达式函数");
            return new(kind == 0 ? 0L : 1L);
        });
        Add("EXISTMETH", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) =>
        {
            string name = a[0].AsString();
            return new(c.CanCall(name, []) ? c.FunctionKind(name, false) switch { 2 => 1L, 3 => 2L, _ => 0L } : 0L);
        });
        Add("$CALL_ACCEPTS", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) => new(c.CanCall(a[0].AsString(), a.Skip(1).Select(v => (int)v.AsInt()).ToArray()) ? 1L : 0L));
        signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.Integer) };
        TableBuiltins.Register(signatures, handlers);
        TemplateBuiltins.Register(signatures, handlers);
        StringBuiltins.Register(signatures, handlers);
        ArrayBuiltins.Register(signatures, handlers);
        MultiSortBuiltins.Register(signatures, handlers);
        DynamicFillBuiltins.Register(signatures, handlers);
        HtmlTextBuiltins.Register(signatures, handlers);
        TableBuiltins.RegisterXml(signatures, handlers);
        foreach (string operation in new[] { "SETBIT", "CLEARBIT", "INVERTBIT" })
        {
            string id = "era." + operation;
            signatures.Add(new(operation, id, [new(ValueKind.Integer, Reference: true), new(ValueKind.Integer)], null, BuiltinEffect.Query));
            handlers.Add(new BitHandler(id, operation));
        }
        signatures.Add(new("CVARSET", "era.CVARSET", [new(ValueKind.String, VariableName: true), new(null, Default: new EraValue(0L)),
            new(null, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], null, BuiltinEffect.Query));
        handlers.Add(new CharacterRangeHandler("era.CVARSET"));
        Add("LOADTEXT", ValueKind.String, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer, ValueKind.Integer], (c, a) =>
        { c.Defer(ServiceKind.Resource, new LoadTextPayload(a[0], a[1].AsInt() != 0, a[2].AsInt() != 0)); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(null), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("SAVETEXT", ValueKind.Integer, BuiltinEffect.Persistence, [ValueKind.String, null, ValueKind.Integer, ValueKind.Integer], (c, a) =>
        { c.Defer(ServiceKind.Resource, new SaveTextPayload(a[0].AsString(), a[1], a[2].AsInt() != 0, a[3].AsInt() != 0)); return null; });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(null), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))] };
        Add("EXISTSOUND", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String], (c, a) =>
        { c.Defer(ServiceKind.Resource, new SoundExistsPayload(a[0].AsString())); return null; });
        Add("$DEBUG_PRINT", null, BuiltinEffect.Output, [ValueKind.String], (c, a) => { c.DebugPrint(a[0].AsString()); return null; });
        Add("VARSIZE", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer], (c, a) =>
        {
            var shape = c.VariableInfo(a[0].AsString()).Shape; long dimension = a[1].AsInt();
            if (dimension < 0 || dimension >= shape.Length) throw new ScriptException("RANK", "VARSIZE 维数越界");
            return new((long)shape[(int)dimension]);
        });
        signatures[^1] = signatures[^1] with { Parameters = [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))] };
        signatures.Add(new("GETNUM", "era.GETNUM", [new(ValueKind.String, VariableName: true), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new NameIndexHandler());
        signatures.Add(new("ERDNAME", "era.ERDNAME", [new(ValueKind.String, VariableName: true), new(ValueKind.Integer), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new ErdNameHandler());
        Add("$NAME_INDEX", ValueKind.Integer, BuiltinEffect.Query, [ValueKind.String, ValueKind.String, ValueKind.Integer], (c, a) =>
        {
            var names = c.VariableInfo(a[0].AsString()).NamesAt(checked((int)a[2].AsInt()));
            if (!names.TryGetValue(a[1].AsString(), out int index)) throw new ScriptException("NAME_INDEX", "名称下标不存在");
            return new((long)index);
        });
        Add("$FORMAT_FIELD", ValueKind.String, BuiltinEffect.Query, [ValueKind.String, ValueKind.Integer, ValueKind.Integer],
            (c, a) => new(c.FormatField(a[0].AsString(), a[1].AsInt(), a[2].AsInt() != 0)));
        constants.Add("$FORMAT_FIELD", a => new(FormatFieldSupport.Pad(a[0].AsString(), a[1].AsInt(), a[2].AsInt() != 0, new TerminalTextMetrics())));
        return new(new SignatureCatalog(signatures, constants), handlers) { CreateExtensions = () => new TableBuiltins(), NumberedText = NumberedText.Split };
    }
    private static int Slot(EraValue value) => value.Integer is >= 0 and <= int.MaxValue ? (int)value.Integer : throw new ScriptException("SAVE_SLOT", "存档槽位越界");
    private sealed class ErdNameHandler : IBuiltinHandler
    {
        public string Id => "era.ERDNAME";
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a) =>
            new(context.ErdName(a[0].Value.AsString(), a[1].Value.AsInt(), a[2].IsOmitted ? null : a[2].Value.AsInt()));
    }
    private sealed class NameIndexHandler : IBuiltinHandler
    {
        public string Id => "era.GETNUM";
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a) =>
            new(context.NameIndex(a[0].Value.AsString(), a[1].Value.AsString(), a[2].IsOmitted ? null : a[2].Value.AsInt()));
    }
}
