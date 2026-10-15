using System.Collections.Immutable;

namespace mEmuera.Contracts;

public enum BuiltinEffect { Query, Output, Input, Persistence, Flow }
public sealed record BuiltinParameter(ValueKind? Kind, bool Reference = false, EraValue? Default = null, bool VariableName = false, bool CaptureReference = false);
public sealed record BuiltinSignature(string Name, string ImplementationId, ImmutableArray<BuiltinParameter> Parameters,
    ValueKind? ReturnType, BuiltinEffect Effect, BuiltinParameter? Variadic = null, bool NumericResult = false, bool SameType = false)
{
    public BuiltinParameter Parameter(int index) => index < Parameters.Length ? Parameters[index] : Variadic ?? throw new ScriptException("ARGUMENT_COUNT", "内建参数过多");
}
public interface IBuiltinRegistry
{
    ImmutableArray<BuiltinSignature> Signatures { get; }
    BuiltinSignature? Find(string name);
    bool CanEvaluateConstant(string name) => false;
    EraValue EvaluateConstant(string name, ImmutableArray<EraValue> arguments) => throw new ScriptException("CONSTANT", "函数不能用于常量: " + name);
}
public sealed class SignatureCatalog(IEnumerable<BuiltinSignature> signatures, IReadOnlyDictionary<string, Func<ImmutableArray<EraValue>, EraValue>>? constants = null) : IBuiltinRegistry
{
    public ImmutableArray<BuiltinSignature> Signatures { get; } = signatures.ToImmutableArray();
    private readonly Dictionary<string, BuiltinSignature> names = signatures.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    public BuiltinSignature? Find(string name) => names.GetValueOrDefault(name);
    public bool CanEvaluateConstant(string name) => constants?.ContainsKey(name) == true;
    public EraValue EvaluateConstant(string name, ImmutableArray<EraValue> arguments)
    {
        if (constants is null || !constants.TryGetValue(name, out var evaluate)) throw new ScriptException("CONSTANT", "函数不能用于常量: " + name);
        var signature = names[name];
        if (arguments.Length > signature.Parameters.Length && signature.Variadic is null || signature.Parameters.Skip(arguments.Length).Any(p => p.Default is null)) throw new ScriptException("ARGUMENT_COUNT", "常量函数参数数量不符");
        arguments = arguments.AddRange(signature.Parameters.Skip(arguments.Length).Select(p => p.Default!.Value));
        if (signature.SameType && arguments.Any(a => a.Kind != arguments[0].Kind)) throw new ScriptException("TYPE", "参数类型必须一致");
        return evaluate(arguments);
    }
}
public interface IBuiltinContext
{
    void WarnOnce(string code, string message) { }
    bool LegacyTimes => false;
    int LanguageCodePage => 932;
    EraValue TemplateValue(long number, string name, long index) => throw new ScriptException("UNSUPPORTED", "角色模板查询不可用");
    bool TemplateExists(long number) => throw new ScriptException("UNSUPPORTED", "角色模板查询不可用");
    bool CanCall(string name, IReadOnlyList<int> argumentKinds) => throw new ScriptException("UNSUPPORTED", "动态调用查询不可用");
    void CopyArray(string source, string destination) => throw new ScriptException("UNSUPPORTED", "数组复制不可用");
    TooltipSettings Tooltip { get => new(); set => throw new ScriptException("UNSUPPORTED", "悬浮说明不可用"); }
    int ArrayLength(SlotReference reference) => throw new ScriptException("UNSUPPORTED", "数组查询不可用");
    void SetResult(long value, int index = 0) => throw new ScriptException("UNSUPPORTED", "结果数组不可用");
    void SetStringResult(string value, int index) => throw new ScriptException("UNSUPPORTED", "字符串结果数组不可用");
    void SetAnimationTimer(long interval) => throw new ScriptException("UNSUPPORTED", "动画刷新不可用");
    int AnimationInterval => throw new ScriptException("UNSUPPORTED", "动画间隔查询不可用");
    bool LineIsEmpty => throw new ScriptException("UNSUPPORTED", "当前行查询不可用");
    bool CachePrintedLines { get => false; set => throw new ScriptException("UNSUPPORTED", "显示行缓存不可用"); }
    void ClearImageLayers(long? depth) => throw new ScriptException("UNSUPPORTED", "图片层不可用");
    bool HasImageLayer(long depth) => throw new ScriptException("UNSUPPORTED", "图片层不可用");
    void MultiSort(MultiSortSpec sort) => throw new ScriptException("UNSUPPORTED", "动态关联排序不可用");
    void DynamicFill(DynamicFillSpec fill) => throw new ScriptException("UNSUPPORTED", "动态数组填充不可用");
    string CurrentFunction => "";
    int KanaMode { get => 0; set => throw new ScriptException("UNSUPPORTED", "假名模式不可用"); }
    void EmitSingle(string text) => throw new ScriptException("UNSUPPORTED", "单行输出不可用");
    string ErdName(string name, long index, long? dimension) => throw new ScriptException("UNSUPPORTED", "ERD 名称查询不可用");
    void PickCharacters(IReadOnlyList<long> indices) => throw new ScriptException("UNSUPPORTED", "角色筛选不可用");
    void ButtonColumn(string text, EraValue value, bool right) => throw new ScriptException("UNSUPPORTED", "按钮列不可用");
    EraValue Extension(string operation, ImmutableArray<BuiltinArgument> arguments) => throw new ScriptException("UNSUPPORTED", "会话扩展不可用");
    IDictionary<string, Dictionary<string, string>> StringMaps => throw new ScriptException("UNSUPPORTED", "映射容器不可用");
    VariableDescriptor VariableInfo(string name) => throw new ScriptException("UNSUPPORTED", "变量元数据不可用");
    VariableDescriptor VariableInfo(SlotReference reference) => VariableInfo(reference.VariableId);
    string FormatField(string text, long width, bool left) => throw new ScriptException("UNSUPPORTED", "插值对齐不可用");
    void DebugPrint(string text) => throw new ScriptException("UNSUPPORTED", "调试输出不可用");
    void DebugClear() => throw new ScriptException("UNSUPPORTED", "调试输出不可用");
    void DynamicVariable(string expression, ValueKind kind, EraValue? fallback) => throw new ScriptException("UNSUPPORTED", "动态变量不可用");
    void DynamicFormat(string text) => throw new ScriptException("UNSUPPORTED", "动态格式文本不可用");
    EraValue? ExistsVariable(string text, long mode) => throw new ScriptException("UNSUPPORTED", "变量存在查询不可用");
    void DynamicAssignment(string expression, ValueKind kind, EraValue? value, bool allowInvalid = false) => throw new ScriptException("UNSUPPORTED", "动态赋值不可用");
    bool MethodExists(string name) => throw new ScriptException("UNSUPPORTED", "动态函数查询不可用");
    int CallableArity(string name) => throw new ScriptException("UNSUPPORTED", "函数参数查询不可用");
    bool CallableReference(string name, int parameter) => throw new ScriptException("UNSUPPORTED", "函数引用参数查询不可用");
    bool CallableArrayReference(string name, int parameter) => CallableReference(name, parameter);
    long FunctionKind(string name, bool ignoreCase) => throw new ScriptException("UNSUPPORTED", "函数存在查询不可用");
    EraValue? Method(string name, ValueKind kind, EraValue? fallback, ImmutableArray<BuiltinArgument> arguments) => throw new ScriptException("UNSUPPORTED", "动态函数不可用");
    EraValue Read(SlotReference reference);
    EraValue ReadGlobal(string name, int index) => throw new ScriptException("UNSUPPORTED", "全局变量查询不可用");
    void Write(SlotReference reference, EraValue value);
    void Emit(string text, bool newline, bool html = false);
    bool SkipDisplay { get => false; set => throw new ScriptException("UNSUPPORTED", "输出跳过不可用"); }
    void EmitDefault(string text, bool newline) => Emit(text, newline);
    void EmitPlain(string text) => Emit(text, false);
    void EmitTemporary(string text) => throw new ScriptException("UNSUPPORTED", "临时行不可用");
    void EmitColumn(string text, bool right, bool defaultStyle) => throw new ScriptException("UNSUPPORTED", "列输出不可用");
    int PrintColumnWidth => throw new ScriptException("UNSUPPORTED", "列宽配置不可用");
    int PrintColumnsPerLine => throw new ScriptException("UNSUPPORTED", "列数配置不可用");
    void Align(TextAlignment alignment) => throw new ScriptException("UNSUPPORTED", "文本对齐不可用");
    TextAlignment CurrentAlignment => throw new ScriptException("UNSUPPORTED", "文本对齐查询不可用");
    void Redraw(long flags) => throw new ScriptException("UNSUPPORTED", "重绘控制不可用");
    bool RedrawEnabled => true;
    string HoveredButton => throw new ScriptException("UNSUPPORTED", "鼠标指向查询不可用");
    void Button(string text, EraValue value);
    void Input(InputKind kind, long? timeout = null, EraValue? defaultValue = null);
    string TextBox { get => ""; set => throw new ScriptException("UNSUPPORTED", "输入框不可用"); }
    bool InputMacroEnabled { get => true; set => throw new ScriptException("UNSUPPORTED", "输入宏不可用"); }
    bool MessageSkip { get => false; set => throw new ScriptException("UNSUPPORTED", "等待跳过不可用"); }
    void SequenceInput(string text) => throw new ScriptException("UNSUPPORTED", "连续输入不可用");
    bool KeyState(long key, bool triggered) => throw new ScriptException("UNSUPPORTED", "按键查询不可用");
    bool KeyInputActive => true;
    void ClearKeyLatches() => throw new ScriptException("UNSUPPORTED", "按键锁存清理不可用");
    string TimeupLabel => "時間切れ";
    void InputOptions(bool noFocus, bool displayTime, string timeoutText, bool mouseInput) => throw new ScriptException("UNSUPPORTED", "输入选项不可用");
    void TimedWait(long? timeout, bool ignoreInput) => throw new ScriptException("UNSUPPORTED", "限时等待不可用");
    void InputMode(bool singleCharacter, bool buttonsOnly, bool anyKey = false) => throw new ScriptException("UNSUPPORTED", "输入模式不可用");
    void Clear(long count);
    int LineCount { get; }
    void Color(int? color);
    int ForegroundColor => throw new ScriptException("UNSUPPORTED", "前景颜色查询不可用");
    int DefaultForegroundColor => 0xc0c0c0;
    int FocusColor => 0xffff00;
    int MouseX => -1;
    int MouseY => -1;
    int DefaultBackgroundColor => 0;
    string MoneyLabel => "$";
    bool MoneyFirst => true;
    int CurrentBackgroundColor => throw new ScriptException("UNSUPPORTED", "背景颜色查询不可用");
    int TextStyle { get => throw new ScriptException("UNSUPPORTED", "文本样式不可用"); set => throw new ScriptException("UNSUPPORTED", "文本样式不可用"); }
    void BackgroundColor(int? color) => throw new ScriptException("UNSUPPORTED", "背景颜色不可用");
    void TextBackground(uint? color) => throw new ScriptException("UNSUPPORTED", "文字背景不可用");
    void TimedInput(InputKind kind, long timeout, EraValue value, bool displayTime, string timeoutText, bool noFocus)
        => throw new ScriptException("UNSUPPORTED", "计时输入选项不可用");
    void Html(string text, bool toBuffer) => throw new ScriptException("UNSUPPORTED", "HTML 输出不可用");
    string PopPrintingHtml() => throw new ScriptException("UNSUPPORTED", "HTML 缓冲区读取不可用");
    string PrintedHtml(long line) => throw new ScriptException("UNSUPPORTED", "已输出 HTML 查询不可用");
    EraValue? SplitHtml(string text, long length, bool countLines) => throw new ScriptException("UNSUPPORTED", "HTML 截取不可用");
    EraValue? HtmlLineRemainder(string text, long length) => throw new ScriptException("UNSUPPORTED", "HTML 截取不可用");
    string DisplayLine(long index) => throw new ScriptException("UNSUPPORTED", "显示行读取不可用");
    void DoTrain(long command) => throw new ScriptException("FLOW_UNSUPPORTED", "当前流程不支持 DOTRAIN");
    void HtmlColumn(string text, long width, bool right) => throw new ScriptException("UNSUPPORTED", "HTML 列输出不可用");
    int ClientWidth => throw new ScriptException("UNSUPPORTED", "显示尺寸不可用");
    int ClientHeight => throw new ScriptException("UNSUPPORTED", "显示尺寸不可用");
    int FontSize => throw new ScriptException("UNSUPPORTED", "字体尺寸不可用");
    string FontName { get => throw new ScriptException("UNSUPPORTED", "字体名称不可用"); set => throw new ScriptException("UNSUPPORTED", "字体名称不可用"); }
    int FramesPerSecond => throw new ScriptException("UNSUPPORTED", "帧率配置不可用");
    EraValue? HtmlLength(string text, bool pixels) => throw new ScriptException("UNSUPPORTED", "文本测量不可用");
    long NameIndex(string name, string key, long? dimension) => throw new ScriptException("UNSUPPORTED", "名称下标查询不可用");
    EraValue ConfigValue(string key) => throw new ScriptException("CONFIG_UNSUPPORTED", "配置项不可用: " + key);
    void DrawLine(string pattern) => throw new ScriptException("UNSUPPORTED", "分隔线不可用");
    string DefaultLine => throw new ScriptException("UNSUPPORTED", "默认分隔线不可用");
    ImmutableArray<long> ReadMatrix(SlotReference reference) => throw new ScriptException("UNSUPPORTED", "矩阵读取不可用");
    void Save(int slot, string description);
    void Load(int slot);
    void GlobalData(bool load) => throw new ScriptException("UNSUPPORTED", "全局数据服务不可用");
    void Restart();
    void Begin(SystemMode mode) => throw new ScriptException("UNSUPPORTED", "系统流程不可用");
    void Quit();
    long Random(long exclusiveMaximum);
    void SeedRandom(long seed) => throw new ScriptException("UNSUPPORTED", "随机种子不可用");
    void RandomData(bool restore) => throw new ScriptException("UNSUPPORTED", "随机状态不可用");
    long CharacterCount => throw new ScriptException("UNSUPPORTED", "角色服务不可用");
    bool AllowSpCharacters => false;
    void Characters(CharacterOperation operation, long first = 0, long second = 0) => throw new ScriptException("UNSUPPORTED", "角色服务不可用");
    void ResetData() => throw new ScriptException("UNSUPPORTED", "数据重置不可用");
    void ResetStain(long character) => throw new ScriptException("UNSUPPORTED", "角色初始污渍不可用");
    void FillCharacters(string name, int index, EraValue value, int start, int end) => throw new ScriptException("UNSUPPORTED", "角色批量赋值不可用");
    EraValue ReadCharacter(string name, IReadOnlyList<long> indices, long character) => throw new ScriptException("UNSUPPORTED", "角色查询不可用");
    void Fill(SlotReference reference, EraValue value, long start, long end) => throw new ScriptException("UNSUPPORTED", "数组填充不可用");
    void Defer(ServiceKind kind, ServicePayload payload) => throw new ScriptException("UNSUPPORTED", "异步内建服务不可用");
    void CheckSave(int slot) => throw new ScriptException("UNSUPPORTED", "存档查询不可用");
    void CharacterFile(CharacterFileOperation operation, string name, string description = "", IReadOnlyList<long>? indices = null)
        => throw new ScriptException("UNSUPPORTED", "角色存档不可用");
}
public interface IBuiltinHandler
{
    string Id { get; }
    EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments);
}
public sealed record BuiltinArgument(EraValue Value, SlotReference? Reference = null, bool IsOmitted = false);
public sealed record NumberedTextPart(int Start, int Length, long? Value);
public sealed class BuiltinLibrary
{
    public Func<string, IReadOnlyList<NumberedTextPart>>? NumberedText { get; init; }
    public Func<IScriptExtensions>? CreateExtensions { get; init; }
    public IBuiltinRegistry Catalog { get; }
    public ImmutableDictionary<string, IBuiltinHandler> Handlers { get; }
    public BuiltinLibrary(IBuiltinRegistry catalog, IEnumerable<IBuiltinHandler> handlers)
    {
        Catalog = catalog;
        Handlers = handlers.ToImmutableDictionary(h => h.Id, StringComparer.Ordinal);
        if (!catalog.Signatures.Select(s => s.ImplementationId).ToHashSet(StringComparer.Ordinal).SetEquals(Handlers.Keys)
            || catalog.Signatures.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Signatures.Length)
            throw new ArgumentException("内建签名与处理器不匹配");
    }
}
public interface IScriptExtensions
{
    EraValue Execute(string operation, ImmutableArray<BuiltinArgument> arguments, IBuiltinContext context);
    ImmutableArray<TableSnapshot> Capture(ImmutableHashSet<string> names);
    Action PrepareRestore(ImmutableArray<TableSnapshot> tables, ImmutableHashSet<string> names);
    void ClearTableRows(ImmutableHashSet<string> names);
    ImmutableDictionary<string, string> CaptureXml(ImmutableHashSet<string> names) => ImmutableDictionary<string, string>.Empty;
    Action PrepareRestoreXml(ImmutableDictionary<string, string> documents, ImmutableHashSet<string> names) => () => { };
}
public sealed record FlowContinuation(ImmutableArray<string> Functions, Diagnostic? OnNaturalEnd = null);
public sealed record SystemFlowDefinition(string RestartEntry, FlowContinuation ScriptLoad, FlowContinuation UiLoad);
public enum TraceKind { Enter, Return, InputRequested, InputAccepted, InputRejected, ServiceRequested, ServiceCompleted, Loaded, Fault, Cancelled, Presentation, Yielded, Debug }
public sealed record ExecutionEvent(TraceKind Kind, long Epoch, long Instructions, string Detail, SourceSpan? Span,
    string? InputSource = null, long? RequestId = null, long DocumentRevision = 0);
