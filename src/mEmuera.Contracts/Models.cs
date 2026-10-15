using System.Collections.Immutable;
using System.Globalization;

namespace mEmuera.Contracts;

public sealed record SourceId(string Path, string Hash);
public sealed record SourceSpan(SourceId Source, int Start, int Length, int Line, int Column = 1);
public enum Severity { Warning, Error }
public sealed record Diagnostic(string Code, string Message, SourceSpan? Span = null,
    Severity Severity = Severity.Error, ImmutableArray<string> Stack = default, ImmutableArray<SourceSpan> Related = default)
{
    public ImmutableArray<string> Stack { get; init; } = Stack.IsDefault ? [] : Stack;
    public ImmutableArray<SourceSpan> Related { get; init; } = Related.IsDefault ? [] : Related;
}
public sealed class ScriptException(string code, string message, SourceSpan? span = null) : Exception(message)
{
    public Diagnostic Diagnostic { get; } = new(code, message, span);
}
public enum ValueKind { Integer, String, Float }
public readonly record struct EraValue
{
    public ValueKind Kind { get; init; }
    public long Integer { get; init; }
    public double Float { get; init; }
    public string Text { get; init; }
    public EraValue(long value) { Kind = ValueKind.Integer; Integer = value; Text = ""; }
    public EraValue(string value) { Kind = ValueKind.String; Text = value ?? throw new ArgumentNullException(nameof(value)); }
    public EraValue(double value) { Kind = ValueKind.Float; Float = value; Text = ""; }
    public long AsInt() => Kind == ValueKind.Integer ? Integer : throw new ScriptException("TYPE", "需要整数");
    public string AsString() => Kind == ValueKind.String ? Text : throw new ScriptException("TYPE", "需要字符串");
    public double AsFloat() => Kind switch { ValueKind.Float => Float, ValueKind.Integer => Integer, _ => throw new ScriptException("TYPE", "需要数值") };
    public EraValue ConvertTo(ValueKind kind) => Kind == kind ? this : kind == ValueKind.Float && Kind == ValueKind.Integer ? new((double)Integer) : throw new ScriptException("TYPE", "数值类型不匹配");
    public override string ToString() => Kind switch { ValueKind.Integer => Integer.ToString(CultureInfo.InvariantCulture), ValueKind.Float => Float.ToString(CultureInfo.InvariantCulture), _ => Text };
    public static EraValue Zero(ValueKind kind) => kind switch { ValueKind.Integer => new(0L), ValueKind.Float => new(0d), _ => new("") };
}
public sealed record SourceFile(SourceId Id, long Bytes, string Category)
{ public int DiscoveryOrder { get; init; } = -1; }
public sealed record SourceManifest(ImmutableArray<SourceFile> Files)
{ public ImmutableArray<string> DirectoryOrder { get; init; } = []; }
public sealed record SourceText(SourceId Id, string Text);
public interface IGameSource
{
    ValueTask<SourceManifest> EnumerateAsync(CancellationToken ct = default);
    ValueTask<SourceText> ReadAsync(SourceId source, CancellationToken ct = default);
    async ValueTask ValidateAsync(SourceId source, CancellationToken ct = default) => await ReadAsync(source, ct);
    async ValueTask ValidateAsync(IReadOnlyList<SourceId> sources, CancellationToken ct = default)
    { foreach (var source in sources) await ValidateAsync(source, ct); }
}
public enum VariableScope { Global, CallLocal, FunctionStatic, Character, System }
public sealed record VariableDescriptor(string Id, string Name, ValueKind Kind, int Length = 1,
    bool Saved = false, bool Local = false, bool Reference = false,
    ImmutableArray<int> Dimensions = default, ImmutableArray<EraValue> Initial = default,
    VariableScope Scope = VariableScope.Global, bool ReadOnly = false, bool GlobalSaved = false)
{
    public ImmutableArray<int> Dimensions { get; init; } = Dimensions.IsDefault ? [] : Dimensions;
    public ImmutableArray<EraValue> Initial { get; init; } = Initial.IsDefault ? [] : Initial;
    public bool CharacterScalar { get; init; }
    public int? ReferenceRank { get; init; }
    public ImmutableDictionary<string, int> NameIndices { get; init; } = ImmutableDictionary<string, int>.Empty;
    public ImmutableDictionary<int, ImmutableDictionary<string, int>> DimensionNames { get; init; } = ImmutableDictionary<int, ImmutableDictionary<string, int>>.Empty;
    public ImmutableDictionary<string, int> NamesAt(int dimension) => DimensionNames.GetValueOrDefault(dimension) ?? NameIndices;
    public ImmutableArray<int> Shape => Dimensions.IsDefaultOrEmpty ? [Length] : Dimensions;
    public int Rank => Dimensions.IsDefaultOrEmpty ? 1 : Dimensions.Length;
    public int Offset(IReadOnlyList<long> indices)
    {
        if (indices.Count != Rank) throw new ScriptException("RANK", "数组下标维数不匹配: " + Name);
        long offset = 0;
        for (int i = 0; i < indices.Count; i++)
        {
            int width = Dimensions.IsDefaultOrEmpty ? Length : Dimensions[i];
            if (indices[i] < 0 || indices[i] >= width) throw new ScriptException("BOUNDS", "数组下标越界: " + Name);
            offset = checked(offset * width + indices[i]);
        }
        return checked((int)offset);
    }
    public int OffsetSpan(ReadOnlySpan<long> indices)
    {
        if (indices.Length != Rank) throw new ScriptException("RANK", "数组下标维数不匹配: " + Name);
        long offset = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            int width = Dimensions.IsDefaultOrEmpty ? Length : Dimensions[i];
            if (indices[i] < 0 || indices[i] >= width) throw new ScriptException("BOUNDS", "数组下标越界: " + Name);
            offset = checked(offset * width + indices[i]);
        }
        return checked((int)offset);
    }
}
public sealed record Parameter(string Name, ValueKind Kind, bool Reference = false, EraValue? Default = null, int TargetIndex = 0)
{
    public bool ExplicitTarget { get; init; }
    public bool GlobalTarget { get; init; }
}
public sealed record LabelDescriptor(string Name, SourceSpan Span);
public sealed record FunctionDescriptor(string Id, string Name, SourceSpan Span, int BodyStart, int BodyEnd,
    ImmutableArray<Parameter> Parameters, ImmutableArray<string> Attributes,
    ImmutableArray<VariableDescriptor> Locals, ImmutableArray<LabelDescriptor> Labels = default)
{
    public bool IsEvent { get; init; }
}
public sealed record Requirement(string Name, string Category, SourceSpan Span, bool Supported, long Count = 1);
public sealed record MacroDefinition(string Name, ImmutableArray<string> Parameters, string Replacement, SourceSpan Span);
public sealed record IndexCoverage(int Files, int Structured, int Unknown, int Dynamic);
public sealed record CompileOptions(string Profile = "minimal-v1", string Encoding = "utf-8", int CacheCapacity = 1024, int SourceCacheCapacity = 8,
    bool Debug = false, bool SearchSubdirectories = true)
{
    public ImmutableArray<VariableDescriptor> SystemVariables { get; init; } = [];
    public bool AllowCallEvent { get; init; }
    public bool AllowSpCharacters { get; init; }
    public bool AutoStringArguments { get; init; }
    public bool CompatiFuncArgOptional { get; init; }
    public bool SystemIgnoreTripleSymbol { get; init; }
    public bool CanBindArgument(ValueKind source, ValueKind target, bool reference = false)
        => source == target || !reference && (source == ValueKind.Integer && target == ValueKind.Float
            || AutoStringArguments && source != ValueKind.String && target == ValueKind.String);
    public bool LegacyTimes { get; init; }
    public bool DisableBeforeErrorThrow { get; init; }
    public ImmutableDictionary<string, EraValue> ConfigValues { get; init; } = ImmutableDictionary<string, EraValue>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public bool SortFiles { get; init; } = true;
    public int FramesPerSecond { get; init; } = 5;
    public string DefaultFont { get; init; } = "monospace";
    public int DefaultForeground { get; init; } = 0xc0c0c0;
    public int FocusColor { get; init; } = 0xffff00;
    public int DefaultBackground { get; init; }
    public string MoneyLabel { get; init; } = "$";
    public bool MoneyFirst { get; init; } = true;
    public int PrintColumnWidth { get; init; } = 25;
    public int PrintColumnsPerLine { get; init; } = 3;
    public bool UseRename { get; init; } = true;
    public bool UseReplace { get; init; } = true;
    public bool UseErd { get; init; } = true;
    public int LanguageCodePage { get; init; } = 932;
    public ImmutableArray<string> TextExtensions { get; init; } = ["txt"];
    public bool UseSaveFolder { get; init; }
    public ExtensionSaveNames GameExtensions { get; init; } = new();
    public ExtensionSaveNames GlobalExtensions { get; init; } = new();
    public string DrawLinePattern { get; init; } = "-";
    public string TimeupLabel { get; init; } = "時間切れ";
    public ImmutableDictionary<string, ImmutableDictionary<string, int>> NameTables { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, int>>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, int>> UserNameTables { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, int>>.Empty;
    public ImmutableDictionary<string, long> CharacterNameIndices { get; init; } = ImmutableDictionary<string, long>.Empty;
    public long DefaultComAble { get; init; } = 1;
    public ImmutableArray<long> DefaultStain { get; init; } = [0, 0, 2, 1, 8];
    public ImmutableDictionary<long, CharacterTemplate> CharacterTemplates { get; init; } = ImmutableDictionary<long, CharacterTemplate>.Empty;
    public ImmutableDictionary<string, string> Renames { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableArray<Diagnostic> PreparationDiagnostics { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public SourceManifest? PreparedManifest { get; init; }
    public ImmutableArray<SourceId> PreparationSources { get; init; } = [];
}
public sealed record ProgramIndex(string Identity, SourceManifest Manifest,
    ImmutableArray<VariableDescriptor> Variables, ImmutableArray<FunctionDescriptor> Functions,
    ImmutableArray<Requirement> Requirements, IndexCoverage Coverage, CompileOptions Options, ImmutableArray<SourceId> DeclarationSources = default)
{
    public ImmutableDictionary<string, MacroDefinition> Macros { get; init; } = ImmutableDictionary<string, MacroDefinition>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public ImmutableArray<SourceId> LoadedSources { get; init; } = [];
    private FunctionTables? tables;
    private (ImmutableArray<VariableDescriptor> Source, ImmutableDictionary<string, VariableDescriptor> Values)? variableTable;
    [System.Text.Json.Serialization.JsonIgnore]
    public ImmutableDictionary<string, VariableDescriptor> VariablesByName
    {
        get
        {
            if (variableTable is { } cached && cached.Source == Variables) return cached.Values;
            var values = Variables.ToImmutableDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);
            variableTable = (Variables, values); return values;
        }
    }
    private sealed record FunctionTables(ImmutableArray<FunctionDescriptor> Source,
        bool AllowCallEvent,
        ImmutableDictionary<string, FunctionDescriptor> Ids,
        ImmutableDictionary<string, FunctionDescriptor> Names,
        ImmutableDictionary<string, ImmutableArray<FunctionDescriptor>> Events);
    private FunctionTables Tables
    {
        get
        {
            var value = tables;
            bool allow = Options.Profile == "minimal-v1" || Options.AllowCallEvent;
            if (value is not null && value.Source == Functions && value.AllowCallEvent == allow) return value;
            var groups = Functions.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return tables = new(Functions, allow, Functions.ToImmutableDictionary(f => f.Id),
                groups.Where(g => allow || !g.First().IsEvent).ToImmutableDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
                groups.Where(g => g.First().IsEvent)
                    .ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.OrdinalIgnoreCase));
        }
    }
    [System.Text.Json.Serialization.JsonIgnore] public ImmutableDictionary<string, FunctionDescriptor> ByFunctionId => Tables.Ids;
    [System.Text.Json.Serialization.JsonIgnore] public ImmutableDictionary<string, FunctionDescriptor> OrdinaryFunctionsByName => Tables.Names;
    [System.Text.Json.Serialization.JsonIgnore] public ImmutableDictionary<string, ImmutableArray<FunctionDescriptor>> EventGroups => Tables.Events;
    public FunctionDescriptor? Find(string name) => Tables.Names.GetValueOrDefault(name);
}
public sealed record IndexResult(ProgramIndex Index, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Success => Diagnostics.All(d => d.Severity != Severity.Error);
}
public enum Op
{
    Constant, Load, Reference, Store, Duplicate, Pop, Unary, Binary, Jump, JumpFalse, JumpTrue, AdvanceReference,
    Call, DynamicCall, Return, Emit, Button, Input, ClearLine, Color, Save, LoadSave, BeginTitle, Quit, LineCount, Builtin, StoreArray,
    StringCall, Throw, ReadVariable, ReadCharacter, ReadLocal, ReadLocalConstant
}
public sealed record Instruction(Op Op, string Text, int Number, EraValue Value, SourceSpan Span)
{
    public VariableDescriptor? ResolvedVariable { get; init; }
    public int LocalSlot { get; init; } = -1;
}
public sealed record CompiledFunction(FunctionDescriptor Descriptor, ImmutableArray<Instruction> Code);
public sealed record CompileResult(CompiledFunction? Function, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Success => Function is not null && Diagnostics.All(d => d.Severity != Severity.Error);
}
public interface IGameCompiler
{
    ValueTask<IndexResult> IndexAsync(IGameSource source, CompileOptions options, CancellationToken ct = default);
    ValueTask<CompileResult> CompileAsync(ProgramIndex index, string functionId, CancellationToken ct = default);
}
public interface ICompilationService
{
    ValueTask<CompileResult> CompileAsync(string functionId, CancellationToken ct);
    ValueTask<CompileResult> CompileVariableAsync(DynamicVariablePayload payload, CancellationToken ct) => throw new ScriptException("UNSUPPORTED", "动态变量编译不可用");
}
public sealed record GameDefinition(string GameId, string Title, string Profile, ProgramIndex Index, string Entry = "SYSTEM_TITLE", SystemFlowDefinition? Flow = null)
{
    public SaveCompatibility? SaveCompatibility { get; init; }
    public ImmutableDictionary<long, CharacterTemplate> CharacterTemplates { get; init; } = ImmutableDictionary<long, CharacterTemplate>.Empty;
    [System.Text.Json.Serialization.JsonIgnore] public ISystemFlowPolicy? SystemFlow { get; init; }
}
public sealed record StyledRun(string Text, int? Color = null, long? ButtonId = null, EraValue? ButtonValue = null, SourceSpan? Source = null)
{
    public string? Font { get; init; }
    public string? Tooltip { get; init; }
    public long? TooltipId { get; init; }
    public uint? Background { get; init; }
    public int Style { get; init; }
    public bool ColorSpecified { get; init; }
    public string? RawInput { get; init; }
    public string? ExportHtml { get; init; }
    public bool VisualOnly { get; init; }
    public InlineRaster? Image { get; init; }
    public InlinePanel? Panel { get; init; }
}
public sealed record InlinePanel(int X, int Y, int Width, int Height, int Padding, int Border, int Radius,
    int? Color, int? BorderColor, ImmutableList<LogicalLine> Lines);
public enum TextAlignment { Left, Center, Right }
public sealed record LogicalLine(ImmutableArray<StyledRun> Runs)
{
    public bool ContinuesPrevious { get; init; }
    public bool CacheLayout { get; init; }
    public bool Temporary { get; init; }
    public TextAlignment Alignment { get; init; }
    public bool NoWrap { get; init; }
}
public sealed record PresentationDocument(long Revision, ImmutableList<LogicalLine> Lines)
{
    public int FocusColor { get; init; } = 0xffff00;
    public int AnimationInterval { get; init; }
    public TooltipSettings Tooltip { get; init; } = new();
    public int? BackgroundColor { get; init; }
    public ImmutableArray<RasterLayer> BackgroundLayers { get; init; } = [];
    public ImmutableArray<ScriptImageLayer> ImageLayers { get; init; } = [];
}
public sealed record TooltipSettings(int Delay = 500, int Duration = 5000, int Foreground = 0, int Background = 0xffffe1);
public enum InputKind { Integer, String, Wait }
public sealed record InputRequest(long Epoch, long Id, InputKind Kind, ImmutableDictionary<long, EraValue> Buttons,
    EraValue? Default, long? Deadline)
{
    public bool NoFocus { get; init; }
    public bool SingleCharacter { get; init; }
    public bool AnyKey { get; init; }
    public bool ButtonsOnly { get; init; }
    public bool IgnoreInput { get; init; }
    public bool MouseInput { get; init; }
    public bool SequenceInputAllowed { get; init; }
    public bool DisplayTime { get; init; }
    public string TimeoutText { get; init; } = "";
}
public abstract record SessionMessage;
public sealed record TextBoxMessage(long Epoch, long Revision, string Text) : SessionMessage;
public sealed record KeyStateMessage(long Epoch, int Key, bool Down) : SessionMessage;
public sealed record PointerMessage(long Epoch, long DocumentRevision, long? ButtonId) : SessionMessage
{
    public int X { get; init; } = -1;
    public int Y { get; init; } = -1;
}
public enum InputMouseButton { Left = 1, Right = 2, Middle = 3 }
[Flags]
public enum InputModifiers { None = 0, Shift = 1 << 16, Control = 1 << 17, Alt = 1 << 18 }
public sealed record InputPointerInfo(InputMouseButton Button, InputModifiers Modifiers = InputModifiers.None);
public sealed record InputReply(long Epoch, long RequestId, string Text, long? ButtonId, long ReceivedAt,
    string Source = "keyboard") : SessionMessage
{
    // 鼠标信息存在且 ButtonId 为空时，表示正文空白点击。
    public InputPointerInfo? Pointer { get; init; }
}
public sealed record TimeoutMessage(long Now) : SessionMessage;
public sealed record CancelMessage : SessionMessage;
public sealed record SaveCommand(int Slot, bool Load) : SessionMessage;
public enum ServiceKind { Compile, Save, Load, Database, Resource, Media, Extension }
public abstract record ServicePayload;
public sealed record CompilePayload(string FunctionId) : ServicePayload;
public sealed record DynamicVariablePayload(FunctionDescriptor Caller, string Expression, ValueKind Kind, EraValue? Fallback, SourceSpan? Span,
    bool ValidateWrite = false, EraValue? WriteValue = null, bool Format = false, bool ValidateExpression = false,
    MultiSortSpec? Sort = null, DynamicFillSpec? Fill = null, bool CallString = false, bool TryCall = false) : ServicePayload
{
    public ValueKind ResultKind => ValidateWrite || WriteValue is not null ? ValueKind.Integer : Kind;
}
public sealed record MultiSortSpec(string Key, ImmutableArray<string> Targets, bool Ascending, long Length);
public sealed record DynamicFillSpec(string Target, EraValue Value, bool AllDimensions, long Start, long? End)
{
    public string? ValueExpression { get; init; }
    public string? AllExpression { get; init; }
    public string? StartExpression { get; init; }
    public string? EndExpression { get; init; }
}
public sealed record DynamicVariableData(CompiledFunction Function, ValueKind Kind) : ServiceData;
public sealed record SavePayload(int Slot, SaveSnapshot Snapshot, SaveScope Scope = SaveScope.Game) : ServicePayload;
public sealed record DeleteSavePayload(int Slot) : ServicePayload;
public sealed record LoadPayload(int Slot, SaveScope Scope = SaveScope.Game) : ServicePayload;
public sealed record CheckSavePayload(int Slot, string Profile) : ServicePayload
{
    public SaveCompatibility? Compatibility { get; init; }
}
public sealed record UnsupportedPayload(string Name) : ServicePayload;
public sealed record ServiceRequest(long Epoch, long Id, ServiceKind Kind, ServicePayload Payload);
public abstract record ServiceData;
public sealed record CompiledData(CompiledFunction Function) : ServiceData;
public sealed record SavedData(bool Committed) : ServiceData;
public sealed record LoadedData(SaveSnapshot Snapshot) : ServiceData;
public sealed record MissingSaveData : ServiceData;
public sealed record CheckSaveData(int Status, string Description, long Version) : ServiceData;
public sealed record ServiceResult(long Epoch, long OperationId, ServiceData? Data, Diagnostic? Error = null) : SessionMessage;
public enum StopReason { Yielded, WaitingInput, WaitingService, Completed, Faulted, Cancelled }
public sealed record ExecutionBudget(int Instructions = 10000, CancellationToken Cancellation = default);
public sealed record StepResult(StopReason Reason, ServiceRequest? Service = null);
public sealed record ApplyResult(bool Accepted, string Reason);
public sealed record SessionSnapshot(long Epoch, StopReason State, PresentationDocument Document,
    InputRequest? Input, ImmutableDictionary<string, ImmutableArray<EraValue>> Variables,
    ImmutableArray<Diagnostic> Diagnostics, long Instructions, bool CanSave, bool CanLoad, string? SaveDisabledReason = null,
    ImmutableArray<ExecutionEvent> Events = default)
{
    public string TextBox { get; init; } = "";
    public long TextBoxRevision { get; init; }
    public long LastAcceptedInputId { get; init; }
    public long LastAcceptedInputEpoch { get; init; }
    public SystemMode Mode { get; init; }
    public ImmutableArray<CharacterSnapshot> Characters { get; init; } = [];
    public ImmutableDictionary<string, ImmutableArray<int>> Shapes { get; init; } = ImmutableDictionary<string, ImmutableArray<int>>.Empty;
}
public interface IGameSession
{
    StepResult Step(ExecutionBudget budget);
    ApplyResult Apply(SessionMessage message);
    SessionSnapshot Inspect();
    SessionSnapshot InspectPresentation() => Inspect() with { Variables = ImmutableDictionary<string, ImmutableArray<EraValue>>.Empty };
}
public sealed record SavedVariable(string Name, ValueKind Kind, int Length, ImmutableArray<EraValue> Values)
{
    public ImmutableArray<int> Shape { get; init; } = [];
}
public sealed record SaveSnapshot(int SchemaVersion, string GameId, string CompatibilityProfile,
    DateTimeOffset CreatedAt, ImmutableArray<SavedVariable> Variables, ulong RandomState, int FlowVersion = 1)
{
    public string CompatibilityFingerprint { get; init; } = "";
    public ImmutableArray<CharacterSnapshot> Characters { get; init; } = [];
    public string Description { get; init; } = "";
    public long GameVersion { get; init; }
    public ImmutableArray<TableSnapshot> Tables { get; init; } = [];
    public ImmutableDictionary<string, string> XmlDocuments { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, ImmutableDictionary<string, string>> Maps { get; init; } = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
}
public enum SaveScope { Game, Global, Character }
public sealed record SaveKey(string GameId, int Slot, SaveScope Scope = SaveScope.Game, string? Name = null);
public sealed record SaveResult(bool Committed, Diagnostic? Error = null);
public sealed record LoadResult(SaveSnapshot? Snapshot, Diagnostic? Error = null);
public interface ISaveStore
{
    ValueTask<SaveResult> WriteAsync(SaveKey key, SaveSnapshot snapshot, CancellationToken ct = default);
    ValueTask<LoadResult> ReadAsync(SaveKey key, CancellationToken ct = default);
    ValueTask DeleteAsync(SaveKey key, CancellationToken ct = default) => throw new ScriptException("SAVE_DELETE", "存档删除不可用");
    ValueTask<ImmutableArray<string>> FindCharactersAsync(string gameId, string pattern, CancellationToken ct = default) => throw new ScriptException("SAVE_FIND", "角色存档查询不可用");
}
public interface IServiceDispatcher
{
    ValueTask<ServiceResult> ExecuteAsync(ServiceRequest request, CancellationToken ct);
}
public sealed record ResourceId(string Path);
public sealed record ResourceInfo(ResourceId Id, int Width, int Height);
public enum MediaStatus { Stopped, Playing, Paused }
public sealed record SlotReference(string VariableId, int Index, long FrameId, long Generation, string? StaticFunction = null, long? CharacterId = null)
{
    public int? ArgumentRank { get; init; }
    public bool IsCharacterSelector { get; init; }
}
public sealed record ExtensionCall(string Method, ImmutableArray<EraValue> Arguments, ImmutableArray<SlotReference> References);
public sealed record ExtensionResult(EraValue Value, ImmutableArray<EraValue> WriteBack);
