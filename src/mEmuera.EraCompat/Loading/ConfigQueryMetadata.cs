using System.Collections.Immutable;
using System.Globalization;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Loading;

internal static class ConfigQueryMetadata
{
    private sealed record Item(string Name, string Japanese, string English, string Kind, long Default);
    // 旧 ConfigData 的整数查询项及其日英别名；界面配置保留声明值。
    private static readonly Item[] Items =
    [
            new("IgnoreCase", "大文字小文字の違いを無視する", "Ignore case", "bool", 1L),
            new("UseRenameFile", "_Rename.csvを利用する", "Use _Rename.csv file", "bool", 0L),
            new("UseReplaceFile", "_Replace.csvを利用する", "Use _Replace.csv file", "bool", 1L),
            new("UseMouse", "マウスを使用する", "Use mouse", "bool", 1L),
            new("UseMenu", "メニューを使用する", "Show menu", "bool", 1L),
            new("UseDebugCommand", "デバッグコマンドを使用する", "Allow debug commands", "bool", 0L),
            new("AllowMultipleInstances", "多重起動を許可する", "Allow multiple instances", "bool", 1L),
            new("AutoSave", "オートセーブを行なう", "Make autosaves", "bool", 1L),
            new("UseKeyMacro", "キーボードマクロを使用する", "Use keyboard macros", "bool", 1L),
            new("SizableWindow", "ウィンドウの高さを可変にする", "Changeable window height", "bool", 1L),
            new("WindowX", "ウィンドウ幅", "Window width", "int", 760L),
            new("WindowY", "ウィンドウ高さ", "Window height", "int", 480L),
            new("WindowPosX", "ウィンドウ位置X", "Window X position", "int", 0L),
            new("WindowPosY", "ウィンドウ位置Y", "Window Y position", "int", 0L),
            new("SetWindowPos", "起動時のウィンドウ位置を指定する", "Fixed window starting position", "bool", 0L),
            new("WindowMaximixed", "起動時にウィンドウを最大化する", "Maximize window on startup", "bool", 0L),
            new("MaxLog", "履歴ログの行数", "Max history log lines", "int", 5000L),
            new("PrintCPerLine", "PRINTCを並べる数", "Items per line for PRINTC", "int", 3L),
            new("PrintCLength", "PRINTCの文字数", "Number of Item characters for PRINTC", "int", 25L),
            new("FontSize", "フォントサイズ", "Font size", "int", 18L),
            new("LineHeight", "一行の高さ", "Line height", "int", 19L),
            new("ForeColor", "文字色", "Text color", "Color", 12632256L),
            new("BackColor", "背景色", "Background color", "Color", 0L),
            new("FocusColor", "選択中文字色", "Highlight color", "Color", 16776960L),
            new("LogColor", "履歴文字色", "History log color", "Color", 12632256L),
            new("FPS", "フレーム毎秒", "FPS", "int", 5L),
            new("SkipFrame", "最大スキップフレーム数", "Skip frames", "int", 3L),
            new("ScrollHeight", "スクロール行数", "Lines per scroll", "int", 1L),
            new("InfiniteLoopAlertTime", "無限ループ警告までのミリ秒数", "Milliseconds for infinite loop warning", "int", 5000L),
            new("DisplayWarningLevel", "表示する最低警告レベル", "Minimum warning level", "int", 1L),
            new("DisplayReport", "ロード時にレポートを表示する", "Display loading report", "bool", 0L),
            new("IgnoreUncalledFunction", "呼び出されなかった関数を無視する", "Ignore uncalled functions", "bool", 1L),
            new("ChangeMasterNameIfDebug", "デバッグコマンドを使用した時にMASTERの名前を変更する", "Change MASTER mame in debug", "bool", 1L),
            new("ButtonWrap", "ボタンの途中で行を折りかえさない", "Button wrapping", "bool", 0L),
            new("SearchSubdirectory", "サブディレクトリを検索する", "Search subfolders", "bool", 0L),
            new("SortWithFilename", "読み込み順をファイル名順にソートする", "Sort filenames", "bool", 0L),
            new("LastKey", "最終更新コード", "Latest identify code", "long", 0L),
            new("SaveDataNos", "表示するセーブデータ数", "Save data count per page", "int", 20L),
            new("WarnBackCompatibility", "eramaker互換性に関する警告を表示する", "Eramaker compatibility warning", "bool", 1L),
            new("AllowFunctionOverloading", "システム関数の上書きを許可する", "Allow overriding system functions", "bool", 1L),
            new("WarnFunctionOverloading", "システム関数が上書きされたとき警告を表示する", "System function override warning", "bool", 1L),
            new("WarnNormalFunctionOverloading", "同名の非イベント関数が複数定義されたとき警告する", "Duplicated functions warning", "bool", 0L),
            new("CompatiErrorLine", "解釈不可能な行があっても実行する", "Execute error lines", "bool", 0L),
            new("CompatiCALLNAME", "CALLNAMEが空文字列の時にNAMEを代入する", "Use NAME if CALLNAME is empty", "bool", 0L),
            new("UseSaveFolder", "セーブデータをsavフォルダ内に作成する", "Use sav folder", "bool", 0L),
            new("CompatiRAND", "擬似変数RANDの仕様をeramakerに合わせる", "Imitate behavior for RAND", "bool", 0L),
            new("CompatiDRAWLINE", "DRAWLINEを常に新しい行で行う", "Always start DRAWLINE in a new line", "bool", 0L),
            new("CompatiFunctionNoignoreCase", "関数・属性については大文字小文字を無視しない", "Do not ignore case for functions and attributes", "bool", 0L),
            new("SystemAllowFullSpace", "全角スペースをホワイトスペースに含める", "Whitespace includes full-width space", "bool", 1L),
            new("CompatiLinefeedAs1739", "ver1739以前の非ボタン折り返しを再現する", "Reproduce wrapping behavior like in pre ver1739", "bool", 0L),
            new("AllowLongInputByMouse", "ONEINPUT系命令でマウスによる2文字以上の入力を許可する", "Allow long input by mouse for ONEINPUT", "bool", 0L),
            new("CompatiCallEvent", "イベント関数のCALLを許可する", "Allow CALL on event functions", "bool", 0L),
            new("CompatiSPChara", "SPキャラを使用する", "Allow SP characters", "bool", 0L),
            new("SystemSaveInBinary", "セーブデータをバイナリ形式で保存する", "Use the binary format for saving data", "bool", 0L),
            new("CompatiFuncArgOptional", "ユーザー関数の全ての引数の省略を許可する", "Allow arguments omission for user functions", "bool", 0L),
            new("CompatiFuncArgAutoConvert", "ユーザー関数の引数に自動的にTOSTRを補完する", "Auto TOSTR conversion for user function arguments", "bool", 0L),
            new("SystemIgnoreTripleSymbol", "FORM中の三連記号を展開しない", "Do not process triple symbols inside FORM", "bool", 0L),
            new("TimesNotRigorousCalculation", "TIMESの計算をeramakerにあわせる", "Imitate behavior for TIMES", "bool", 0L),
            new("SystemNoTarget", "キャラクタ変数の引数を補完しない", "Do not auto-complete arguments for character variables", "bool", 0L),
            new("SystemIgnoreStringSet", "文字列変数の代入に文字列式を強制する", "String variable assignment on valid with string expression", "bool", 0L),
            new("ForbidUpdateCheck", "UPDATECHECKを許可しない", "Disallow UPDATECHECK", "bool", 0L),
            new("UseERD", "ERD機能を利用する", "Use ERD", "bool", 1L),
            new("VarsizeDimConfig", "VARSIZEの次元指定をERD機能に合わせる", "Imitate ERD to VARSIZE dimension specification", "bool", 0L),
            new("CheckDuplicateIdentifier", "ERDで定義した識別子とローカル変数の重複を確認する", "Check duplicate ERD identifier and private variablea", "bool", 0L),
            new("PluginAvailableWarn", "外部プラグインが有効時に警告を表示する", "Plugin available warning", "bool", 1L),
            new("DisableBeforeErrorThrow", "BEFORE_ERROR/THROWイベントを無効化する", "Disable BEFORE_ERROR/THROW events", "bool", 0L),
            new("ZipSaveData", "セーブデータを圧縮して保存する", "Compress save data", "bool", 0L),
            new("EnglishConfigOutput", "CONFIGファイルの内容を英語で保存する", "Output English items in the config file", "bool", 0L),
            new("CBUseClipboard", "表示したテキストをクリップボードにコピーする", "Clipboard- Copy text to Clipboard during Game", "bool", 0L),
            new("CBIgnoreTags", "テキスト中の<>タグを無視する", "Clipboard- ignore <> tags in text", "bool", 0L),
            new("CBNewLinesOnly", "新しい行のみコピーする", "Clipboard- Show new lines only", "bool", 1L),
            new("CBClearBuffer", "画面のリフレッシュ時にクリップボードとバッファを消去する", "Clipboard- Clear Buffer when game clears screen", "bool", 0L),
            new("CBTriggerLeftClick", "左クリックをトリガーにする", "Clipboard- LeftClick Trigger", "bool", 1L),
            new("CBTriggerMiddleClick", "ホイールクリックをトリガーにする", "Clipboard- MiddleClick Trigger", "bool", 0L),
            new("CBTriggerDoubleLeftClick", "ダブルクリックをトリガーにする", "Clipboard- Double Left Click Trigger", "bool", 0L),
            new("CBTriggerAnyKeyWait", "WAITをトリガーにする", "Clipboard- AnyKey Wait Trigger ", "bool", 0L),
            new("CBTriggerInputWait", "INPUTをトリガーにする", "Clipboard- Wait for Input Trigger", "bool", 1L),
            new("CBMaxCB", "クリップボードに貼り付ける行数", "Clipboard- Length of Clipboard", "int", 25L),
            new("CBBufferSize", "総バッファサイズ", "Clipboard- Buffer Size", "int", 300L),
            new("CBScrollCount", "スクロールの行数", "Clipboard- Scrolled Lines per Key", "int", 5L),
            new("CBMinTimer", "クリップボードの更新間隔(ミリ秒)", "Clipboard- min time between pastes", "int", 800L),
            new("MemoryDiagnosticEnabled", "MemoryDiagnosticLog", "MemoryDiagnosticLog", "bool", 0L),
            new("RikaiEnabled", "Rikaichanを使用する", "Rikai- Enabled", "bool", 0L),
            new("RikaiUseSeparateBoxes", "翻訳中の語句を強調表示する", "Rikai- Use Separate Boxes", "bool", 1L),
            new("Ctrl_Z_Enabled", "Ctrl-Zで元に戻す機能を有効にする", "Enable undo with ctrl-z", "bool", 0L),
            new("DebugShowWindow", "起動時にデバッグウインドウを表示する", "Show debug window on startup", "bool", 1L),
            new("DebugWindowTopMost", "デバッグウインドウを最前面に表示する", "Debug window always on top", "bool", 1L),
            new("DebugWindowWidth", "デバッグウィンドウ幅", "Debug window width", "int", 520L),
            new("DebugWindowHeight", "デバッグウィンドウ高さ", "Debug window height", "int", 300L),
            new("DebugSetWindowPos", "デバッグウィンドウ位置を指定する", "Fixed debug window starting position", "bool", 0L),
            new("DebugWindowPosX", "デバッグウィンドウ位置X", "Debug window X position", "int", 0L),
            new("DebugWindowPosY", "デバッグウィンドウ位置Y", "Debug window Y position", "int", 0L),
            new("MoneyFirst", "単位の位置", "Currency symbol position", "bool", 1L),
            new("MaxShopItem", "販売アイテム数", "Max shop item storage", "int", 100L),
            new("ComAbleDefault", "COM_ABLE初期値", "Default COM_ABLE", "int", 1L),
            new("pbandDef", "PBANDの初期値", "Default PBAND", "long", 4L),
            new("RelationDef", "RELATIONの初期値", "Default RELATION", "long", 0L),
    ];
    private static readonly Dictionary<string, Item> Names = Items.SelectMany(item =>
        new[] { item.Name, item.Japanese, item.English }.Select(name => (name, item)))
        .DistinctBy(pair => pair.name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(pair => pair.name, pair => pair.item, StringComparer.OrdinalIgnoreCase);

    public static string ConfigurationKey(string name) => Names.TryGetValue(name, out var item) ? item.English.ToUpperInvariant() : name;

    public static CompileOptions Initialize(CompileOptions options)
    {
        var values = options.ConfigValues.ToBuilder();
        foreach (var item in Items)
            if (!values.ContainsKey(item.Name)) Set(values, item, new(item.Default));
        return Sync(options with { ConfigValues = values.ToImmutable() });
    }

    public static CompileOptions Apply(CompileOptions options, string name, string text)
    {
        if (!Names.TryGetValue(name, out var item)) return options;
        long value;
        if (item.Kind == "bool")
        {
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) value = value == 0 ? 0 : 1;
            else if (text.Equals("YES", StringComparison.OrdinalIgnoreCase) || text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text == "前") value = 1;
            else if (text.Equals("NO", StringComparison.OrdinalIgnoreCase) || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || text == "後") value = 0;
            else return options;
        }
        else if (item.Kind == "Color")
        {
            var channels = text.Split(',');
            if (channels.Length != 3 || channels.Any(channel => !byte.TryParse(channel.Trim(), out _))) return options;
            value = byte.Parse(channels[0].Trim()) << 16 | byte.Parse(channels[1].Trim()) << 8 | byte.Parse(channels[2].Trim());
        }
        else if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || item.Kind == "int" && value is < int.MinValue or > int.MaxValue) return options;
        var values = options.ConfigValues.ToBuilder();
        Set(values, item, new(value));
        return options with { ConfigValues = values.ToImmutable() };
    }

    public static CompileOptions Sync(CompileOptions options)
    {
        var values = options.ConfigValues.ToBuilder();
        void Number(string name, long value) => Set(values, Names[name], new(value));
        void Flag(string name, bool value) => Number(name, value ? 1L : 0L);
        Number("ForeColor", options.DefaultForeground);
        Number("BackColor", options.DefaultBackground);
        Number("FocusColor", options.FocusColor);
        Number("FPS", options.FramesPerSecond);
        Number("PrintCLength", options.PrintColumnWidth);
        Number("PrintCPerLine", options.PrintColumnsPerLine);
        Number("ComAbleDefault", options.DefaultComAble);
        Flag("MoneyFirst", options.MoneyFirst);
        Flag("CompatiCallEvent", options.AllowCallEvent);
        Flag("CompatiSPChara", options.AllowSpCharacters);
        Flag("CompatiFuncArgAutoConvert", options.AutoStringArguments);
        Flag("CompatiFuncArgOptional", options.CompatiFuncArgOptional);
        Flag("TimesNotRigorousCalculation", options.LegacyTimes);
        Flag("DisableBeforeErrorThrow", options.DisableBeforeErrorThrow);
        Flag("SearchSubdirectory", options.SearchSubdirectories);
        Flag("SortWithFilename", options.SortFiles);
        Flag("UseRenameFile", options.UseRename);
        Flag("UseReplaceFile", options.UseReplace);
        Flag("UseERD", options.UseErd);
        Flag("UseSaveFolder", options.UseSaveFolder);
        return options with { ConfigValues = values.ToImmutable() };
    }

    private static void Set(ImmutableDictionary<string, EraValue>.Builder values, Item item, EraValue value)
    {
        values[item.Name] = values[item.Japanese] = values[item.English] = value;
    }
}
