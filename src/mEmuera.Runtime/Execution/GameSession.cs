using System.Collections.Immutable;
using System.Text;
using mEmuera.Contracts;
using mEmuera.Runtime.Presentation;

namespace mEmuera.Runtime.Execution;

public sealed partial class GameSession : IGameSession, IBuiltinContext, IFlowState
{
    private sealed class Cell(EraValue[]? values, ValueKind kind, VariableDescriptor? descriptor = null)
    {
        public ValueKind Kind { get; } = kind;
        public VariableDescriptor? Descriptor { get; } = descriptor;
        // 数组声明长度不变；逐项访问按需分配，整数组操作才展开完整存储。
        public int Length => Descriptor?.Length ?? values!.Length;
        public void ResetLocal()
        {
            if (Descriptor?.Scope == VariableScope.FunctionStatic) return;
            if (Descriptor is null) Array.Fill(values!, EraValue.Zero(Kind));
            else values = null;
        }
        public EraValue[] Values
        {
            get
            {
                if (values is null) values = CreateValues(Descriptor!);
                else if (values.Length < Length) Grow(Length);
                return values;
            }
        }
        public EraValue Read(int index)
        {
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
            if (values is null && Descriptor is { Initial.IsDefaultOrEmpty: false }) return Values[index];
            return values is not null && index < values.Length ? values[index] : EraValue.Zero(Kind);
        }
        public void Write(int index, EraValue value)
        {
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
            if (values is null && Descriptor is { Initial.IsDefaultOrEmpty: false }) _ = Values;
            if (values is null || index >= values.Length) Grow(Math.Min(Length, Math.Max(index + 1, Math.Max(8, (values?.Length ?? 0) * 2))));
            values![index] = value;
        }
        private void Grow(int length)
        {
            int previous = values?.Length ?? 0;
            Array.Resize(ref values, length);
            Array.Fill(values!, EraValue.Zero(Kind), previous, length - previous);
        }
    }
    private readonly record struct Operand(EraValue Value, SlotReference? Reference = null, bool IsOmitted = false);
    private sealed class Frame(FunctionDescriptor descriptor, long id, int mode, int stackBase)
    {
        public FunctionDescriptor Descriptor = descriptor;
        public long Id = id;
        public int Mode = mode;
        public bool IsJump;
        public int StackBase = stackBase;
        public int Pc;
        public bool Entered;
        public CompiledFunction? Function;
        public Dictionary<string, Cell> Locals = new(descriptor.Locals.Length);
        public Cell[] LocalSlots = [];
        public ImmutableArray<VariableDescriptor> LocalLayout;
        public Dictionary<string, SlotReference> Aliases = [];
        public bool OwnsLocals = true;
        public LinkedListNode<Frame>? PoolNode;
    }
    private readonly GameDefinition game;
    private readonly Func<long> clock;
    private readonly BuiltinLibrary builtins;
    private readonly Dictionary<string, BuiltinSignature> builtinSignatures = new(StringComparer.Ordinal);
    private readonly IScriptExtensions? extensions;
    private readonly Dictionary<string, Cell> globals = [];
    private readonly Dictionary<string, Dictionary<string, string>> stringMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Cell>> statics = [];
    private CharacterStore characters;
    private IEnumerator<FlowAction>? systemFlow;
    public SystemMode Mode { get; private set; } = SystemMode.Title;
    private readonly HashSet<string> warnings = [];
    bool IBuiltinContext.LegacyTimes => game.Index.Options.LegacyTimes;
    bool IBuiltinContext.AllowSpCharacters => game.Index.Options.AllowSpCharacters;
    int IBuiltinContext.LanguageCodePage => game.Index.Options.LanguageCodePage;
    TooltipSettings IBuiltinContext.Tooltip { get => document.Tooltip; set => document.Tooltip = value; }
    void IBuiltinContext.WarnOnce(string code, string message)
    { if (warnings.Add(code)) diagnostics.Add(new(code, message, Severity: Severity.Warning)); }
    private readonly List<Frame> frames = [];
    private readonly Dictionary<string, Frame> idleFrames = [];
    private readonly LinkedList<Frame> idleFrameOrder = new();
    private readonly List<Operand> stack = [];
    private readonly Dictionary<string, (CompiledFunction Function, LinkedListNode<string> Node)> compiled = [];
    private readonly LinkedList<string> compiledOrder = new();
    public int CachedFunctionCount => compiled.Count;
    private readonly DocumentBuilder document = new();
    private readonly List<Diagnostic> diagnostics = [];
    private readonly Queue<ExecutionEvent> trace = new(1024);
    private InputRequest? input;
    private string hoveredButton = "";
    bool IBuiltinContext.SkipDisplay { get; set; }
    private long hoveredEpoch, hoveredRevision;
    private ServiceRequest? service;
    private ValueKind? pendingBuiltinType;
    private InputRequest? savedInput;
    private StopReason state = StopReason.Yielded;
    private long epoch = 1, nextRequest, nextOperation, nextFrame, instructions;
    private long lastAcceptedInputId, lastAcceptedInputEpoch;
    private ulong randomState = 1;
    private Diagnostic? naturalEnd;
    private bool loadCommitted;
    private SourceSpan? currentSpan;
    private readonly ITextMetrics? metrics;
    private readonly Action<FunctionDescriptor>? functionEntered;
    private readonly Func<PresentationDocument, long, PointerMessage?>? pointerQuery;
    private int viewportColumns = 80;
    private int viewportRows = 25;
    public GameSession(GameDefinition game, BuiltinLibrary builtins, Func<long>? clock = null, ITextMetrics? metrics = null, Func<int, bool?>? keyState = null, Func<bool>? inputActive = null,
        Func<PresentationDocument, long, PointerMessage?>? pointerQuery = null, Action<FunctionDescriptor>? functionEntered = null)
    {
        this.game = game; this.builtins = builtins; this.clock = clock ?? (() => Environment.TickCount64);
        foreach (var signature in builtins.Catalog.Signatures)
            builtinSignatures.TryAdd(signature.ImplementationId, signature);
        document.DefaultFont = game.Index.Options.DefaultFont;
        nativeKeyState = keyState;
        this.inputActive = inputActive;
        this.pointerQuery = pointerQuery;
        this.functionEntered = functionEntered;
        if (game.Index.Options.Profile != "minimal-v1")
        {
            document.NumberedText = builtins.NumberedText;
            document.DefaultForeground = game.Index.Options.DefaultForeground;
            document.FocusColor = game.Index.Options.FocusColor;
            document.DefaultBackground = game.Index.Options.DefaultBackground;
        }
        extensions = builtins.CreateExtensions?.Invoke();
        this.metrics = metrics;
        characters = new(game.Index.Variables, game.CharacterTemplates);
        foreach (var variable in game.Index.Variables.Where(v => v.Scope is not (VariableScope.Character or VariableScope.CallLocal))) globals.Add(variable.Name, CreateCell(variable));
        Push(game.Index.Find(game.Entry) ?? throw new ScriptException("ENTRY", "入口不存在: " + game.Entry), [], 0);
    }
    private static EraValue[] CreateValues(VariableDescriptor v)
    {
        var values = Enumerable.Repeat(EraValue.Zero(v.Kind), v.Length).ToArray();
        if (!v.Initial.IsDefaultOrEmpty) v.Initial.CopyTo(values);
        return values;
    }
    private static Cell CreateCell(VariableDescriptor v) => new(CreateValues(v), v.Kind, v);
    private Cell LocalCell(FunctionDescriptor function, VariableDescriptor variable)
    {
        if (variable.Scope != VariableScope.FunctionStatic) return new(null, variable.Kind, variable);
        if (!statics.TryGetValue(function.Id, out var cells)) statics.Add(function.Id, cells = []);
        if (!cells.TryGetValue(variable.Name, out var cell)) cells.Add(variable.Name, cell = CreateCell(variable));
        return cell;
    }
    private void Log(TraceKind kind, string detail, string? source = null, long? request = null)
    { if (trace.Count == 1024) trace.Dequeue(); trace.Enqueue(new(kind, epoch, instructions, detail, currentSpan, source, request, document.Revision)); }
    public ImmutableArray<ExecutionEvent> Trace => trace.ToImmutableArray();
    void IBuiltinContext.DebugClear()
    {
        int count = trace.Count;
        for (int i = 0; i < count; i++)
        {
            var entry = trace.Dequeue();
            if (entry.Kind != TraceKind.Debug) trace.Enqueue(entry);
        }
    }
    private void Push(FunctionDescriptor descriptor, ReadOnlySpan<Operand> args, int mode, int? stackBase = null)
    {
        if (frames.Count >= 4096) throw new ScriptException("STACK_LIMIT", "调用深度超过限制");
        for (int i = 0; i < descriptor.Parameters.Length; i++)
        {
            var parameter = descriptor.Parameters[i];
            if (parameter.GlobalTarget && (!globals.TryGetValue(parameter.Name, out var target) || target.Descriptor?.ReadOnly == true
                || parameter.TargetIndex < 0 || parameter.TargetIndex >= target.Values.Length)) throw new ScriptException("PARAMETER_LAYOUT", "全局形参目标不可写或越界");
            bool omitted = i >= args.Length || args[i].IsOmitted;
            if (omitted)
            {
                if (parameter.Reference) throw new ScriptException("REFERENCE", "调用缺少引用参数");
                if (game.Index.Options.Profile != "minimal-v1" && !game.Index.Options.CompatiFuncArgOptional && parameter.Default is null)
                    throw new ScriptException("ARGUMENT_COUNT", "调用参数不能省略");
                continue;
            }
            if (parameter.Reference)
            {
                if (args[i].Reference is not { } reference || !ReferenceMatches(descriptor, parameter, reference))
                    throw new ScriptException("REFERENCE", "引用参数类型、维数或可写性不匹配");
            }
            else if (!game.Index.Options.CanBindArgument(args[i].Value.Kind, parameter.Kind)) throw new ScriptException("TYPE", "参数类型不匹配");
        }
        Frame frame;
        if (idleFrames.Remove(descriptor.Id, out var idle))
        {
            idleFrameOrder.Remove(idle.PoolNode!);
            frame = idle;
            frame.Id = ++nextFrame; frame.Mode = mode; frame.StackBase = stackBase ?? stack.Count;
            frame.Pc = 0; frame.Entered = false; frame.IsJump = false;
        }
        else
        {
            frame = new Frame(descriptor, ++nextFrame, mode, stackBase ?? stack.Count);
            foreach (var variable in descriptor.Locals) frame.Locals.Add(variable.Name, LocalCell(descriptor, variable));
        }
        for (int i = 0; i < descriptor.Parameters.Length; i++)
        {
            var parameter = descriptor.Parameters[i];
            if (parameter.GlobalTarget && parameter.Default is null && (i >= args.Length || args[i].IsOmitted)) continue;
            Operand argument = i < args.Length && !args[i].IsOmitted ? args[i] : new(parameter.Default ?? EraValue.Zero(parameter.Kind));
            if (parameter.Reference)
            {
                var reference = argument.Reference ?? throw new ScriptException("REFERENCE", "调用缺少引用参数");
                if (CellFor(reference).Kind != parameter.Kind) throw new ScriptException("TYPE", "引用参数类型不匹配");
                frame.Aliases[parameter.Name] = reference with { ArgumentRank = ReferenceRank(descriptor, parameter) };
            }
            else
            {
                if (!game.Index.Options.CanBindArgument(argument.Value.Kind, parameter.Kind)) throw new ScriptException("TYPE", "参数类型不匹配");
                if (argument.Value.Kind != parameter.Kind)
                    argument = new(parameter.Kind == ValueKind.String ? new EraValue(argument.Value.ToString()) : argument.Value.ConvertTo(parameter.Kind));
                if (parameter.GlobalTarget) { Write(new(parameter.Name, parameter.TargetIndex, 0, epoch), argument.Value); continue; }
                if (!frame.Locals.TryGetValue(parameter.Name, out Cell? cell)) frame.Locals[parameter.Name] = cell = new(
                    Enumerable.Repeat(EraValue.Zero(parameter.Kind), parameter.TargetIndex + 1).ToArray(), parameter.Kind);
                if (parameter.TargetIndex < 0 || parameter.TargetIndex >= cell.Length) throw new ScriptException("PARAMETER_LAYOUT", "局部形参目标越界");
                cell.Write(parameter.TargetIndex, argument.Value);
            }
        }
        if (compiled.TryGetValue(descriptor.Id, out var function))
        {
            if (function.Node != compiledOrder.Last)
            { compiledOrder.Remove(function.Node); compiledOrder.AddLast(function.Node); }
            Initialize(frame, function.Function);
        }
        frames.Add(frame);
    }
    private void RecycleFrame(Frame frame)
    {
        if (!frame.OwnsLocals || idleFrames.ContainsKey(frame.Descriptor.Id)) return;
        foreach (var cell in frame.Locals.Values) cell.ResetLocal();
        frame.Aliases.Clear(); frame.Function = null;
        frame.PoolNode ??= new(frame);
        idleFrames.Add(frame.Descriptor.Id, frame); idleFrameOrder.AddLast(frame.PoolNode);
        if (idleFrames.Count > 1024)
        {
            var oldest = idleFrameOrder.First!.Value;
            idleFrameOrder.RemoveFirst(); idleFrames.Remove(oldest.Descriptor.Id);
        }
    }
    private static int ReferenceRank(FunctionDescriptor function, Parameter parameter)
    {
        foreach (var variable in function.Locals)
            if (variable.Name == parameter.Name) return variable.ReferenceRank ?? variable.Rank;
        return 1;
    }
    private bool ReferenceMatches(FunctionDescriptor function, Parameter parameter, SlotReference reference)
    {
        var cell = CellFor(reference); var variable = cell.Descriptor;
        int actual = reference.ArgumentRank ?? variable?.ReferenceRank ?? (variable?.CharacterScalar == true ? 0 : variable?.Rank ?? 1);
        int expected = ReferenceRank(function, parameter);
        return cell.Kind == parameter.Kind && variable?.ReadOnly != true
            && (expected == 0 || actual == expected && (variable?.Scope != VariableScope.Character || reference.CharacterId is not null));
    }
    private void Initialize(Frame frame, CompiledFunction function)
    {
        frame.Function = function;
        if (frame.LocalLayout == function.Descriptor.Locals) return;
        for (int i = 0; i < function.Descriptor.Locals.Length; i++)
        {
            var local = function.Descriptor.Locals[i];
            // 入栈时已建立原有局部变量，仅补充编译器生成的变量。
            if (i < frame.Descriptor.Locals.Length && ReferenceEquals(local, frame.Descriptor.Locals[i])) continue;
            if (!frame.Locals.ContainsKey(local.Name)) frame.Locals.Add(local.Name, LocalCell(function.Descriptor, local));
        }
        frame.LocalSlots = new Cell[function.Descriptor.Locals.Length];
        for (int i = 0; i < frame.LocalSlots.Length; i++) frame.LocalSlots[i] = frame.Locals[function.Descriptor.Locals[i].Name];
        frame.LocalLayout = function.Descriptor.Locals;
    }
    private Operand Pop()
    { if (stack.Count == 0) throw new ScriptException("STACK", "表达式栈为空"); var value = stack[^1]; stack.RemoveAt(stack.Count - 1); return value; }
    private EraValue Value() { var operand = Pop(); return operand.Reference is { } reference ? Read(reference) : operand.Value; }
    private void Put(EraValue value) => stack.Add(new(value));
    private Cell CellFor(SlotReference reference)
    {
        if (reference.Generation != epoch) throw new ScriptException("STALE_REFERENCE", "引用代次已失效");
        if (reference.CharacterId is { } id)
        {
            var spec = game.Index.VariablesByName[reference.VariableId];
            return new(characters.Values(id, reference.VariableId), spec.Kind, spec);
        }
        Cell target;
        if (reference.StaticFunction is { } staticId) target = statics[staticId][reference.VariableId];
        else if (reference.FrameId == 0) target = globals[reference.VariableId];
        else
        {
            Frame? frame = null;
            for (int i = frames.Count - 1; i >= 0; i--)
                if (frames[i].Id == reference.FrameId) { frame = frames[i]; break; }
            if (frame is null) throw new ScriptException("STALE_REFERENCE", "引用调用帧已释放");
            target = frame.Locals[reference.VariableId];
        }
        if (target.Descriptor?.Reference == true) throw new ScriptException("UNBOUND_REFERENCE", "引用变量尚未绑定: " + reference.VariableId);
        return target;
    }
    private EraValue Read(SlotReference reference) => CellFor(reference).Read(reference.Index);
    int IBuiltinContext.ArrayLength(SlotReference reference) => CellFor(reference).Length;
    void IBuiltinContext.SetResult(long value, int index) { globals["RESULT"].Values[index] = new(value); }
    EraValue IBuiltinContext.TemplateValue(long number, string name, long at)
    {
        if (!game.CharacterTemplates.TryGetValue(number, out var template)) throw new ScriptException("CHARACTER_TEMPLATE", "角色模板不存在");
        var specification = game.Index.VariablesByName[name];
        if (at < 0 || at >= specification.Length) throw new ScriptException("BOUNDS", "角色模板下标越界");
        return template.Values.TryGetValue(name, out var values) ? values[(int)at] : EraValue.Zero(specification.Kind);
    }
    bool IBuiltinContext.TemplateExists(long number) => game.CharacterTemplates.ContainsKey(number);
    long IBuiltinContext.NameIndex(string name, string key, long? dimension)
    {
        if (key.Length == 0) return -1L;
        bool local = frames.Count > 0 && (frames[^1].Locals.ContainsKey(name) || frames[^1].Aliases.ContainsKey(name));
        var system = local ? null : game.Index.Options.SystemVariables.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (system is not null && NameQuerySupport.IsCharacterTable(name) && game.Index.Options.CharacterNameIndices.TryGetValue(key, out long character)) return character;
        if (system is not null && NameQuerySupport.HasSystemTable(name) && system.NameIndices.TryGetValue(key, out int number)) return number;
        if (system is not null) return -1L;
        string table = dimension is { } d ? name + "@" + d.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
        return game.Index.Options.UserNameTables.TryGetValue(table, out var names) && names.TryGetValue(key, out int index)
            && UserNameIndexWithinDeclaration(name, dimension, index) ? index : -1L;
    }
    private bool UserNameIndexWithinDeclaration(string name, long? dimension, long index)
    {
        var variable = frames.LastOrDefault()?.Function?.Descriptor.Locals.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? game.Index.VariablesByName.GetValueOrDefault(name);
        if (variable is null || variable.Reference || index < 0) return false;
        var shape = variable.Shape;
        if (shape.Length == 1) return dimension is null && index < shape[0];
        return dimension is { } axis && axis >= 1 && axis <= shape.Length && index < shape[(int)axis - 1];
    }
    void IBuiltinContext.CopyArray(string source, string destination)
    {
        source = source.ToUpperInvariant(); destination = destination.ToUpperInvariant();
        var from = ((IBuiltinContext)this).VariableInfo(source); var to = ((IBuiltinContext)this).VariableInfo(destination);
        if (from.Scope == VariableScope.Character || to.Scope == VariableScope.Character || to.ReadOnly)
            throw new ScriptException("REFERENCE", "ARRAYCOPY 不接受角色数组或只读目标");
        if (from.Kind != to.Kind || from.Shape.Length != to.Shape.Length) throw new ScriptException("TYPE", "ARRAYCOPY 数组类型或维数不匹配");
        var input = CellFor(Reference(frames[^1], source, new long[from.Shape.Length]));
        var output = CellFor(Reference(frames[^1], destination, new long[to.Shape.Length]));
        var shape = from.Shape.Zip(to.Shape, Math.Min).ToArray(); int count = shape.Aggregate(1, (a, b) => checked(a * b));
        for (int at = 0; at < count; at++)
        {
            int remaining = at, src = 0, dst = 0, srcStride = 1, dstStride = 1;
            for (int dimension = shape.Length - 1; dimension >= 0; dimension--)
            {
                int position = remaining % shape[dimension]; remaining /= shape[dimension];
                src += position * srcStride; dst += position * dstStride;
                srcStride *= from.Shape[dimension]; dstStride *= to.Shape[dimension];
            }
            output.Values[dst] = input.Values[src];
        }
    }
    IDictionary<string, Dictionary<string, string>> IBuiltinContext.StringMaps => stringMaps;
    EraValue IBuiltinContext.Extension(string operation, ImmutableArray<BuiltinArgument> arguments)
        => extensions?.Execute(operation, arguments, this) ?? throw new ScriptException("UNSUPPORTED", "会话扩展不可用");
    void IBuiltinContext.DebugPrint(string text) => Log(TraceKind.Debug, text);
    void IBuiltinContext.DynamicVariable(string expression, ValueKind kind, EraValue? fallback)
    {
        var frame = frames[^1];
        Request(ServiceKind.Compile, new DynamicVariablePayload(frame.Function!.Descriptor, expression, kind, fallback, currentSpan));
    }
    void IBuiltinContext.DynamicFormat(string text)
    {
        var frame = frames[^1];
        Request(ServiceKind.Compile, new DynamicVariablePayload(frame.Function!.Descriptor, text, ValueKind.String, null, currentSpan, Format: true));
    }
    void IBuiltinContext.MultiSort(MultiSortSpec sort)
    {
        Request(ServiceKind.Compile, new DynamicVariablePayload(frames[^1].Function!.Descriptor, "", ValueKind.Integer, null, currentSpan, Sort: sort));
    }
    void IBuiltinContext.DynamicFill(DynamicFillSpec fill) => Request(ServiceKind.Compile,
        new DynamicVariablePayload(frames[^1].Function!.Descriptor, "", ValueKind.Integer, null, currentSpan, Fill: fill));
    EraValue? IBuiltinContext.ExistsVariable(string text, long mode)
    {
        if (mode == 0)
        {
            VariableDescriptor? variable = null;
            if (frames.Count > 0)
            {
                var currentFrame = frames[^1];
                if (currentFrame.Aliases.TryGetValue(text.ToUpperInvariant(), out var alias)) variable = CellFor(alias).Descriptor;
                else if (currentFrame.Locals.TryGetValue(text.ToUpperInvariant(), out var local)) variable = local.Descriptor;
            }
            variable ??= game.Index.VariablesByName.GetValueOrDefault(text);
            if (variable is null) return new EraValue(0L);
            long flags = variable.Kind switch { ValueKind.Integer => 1, ValueKind.String => 2, ValueKind.Float => 32, _ => 0 };
            if (variable.ReadOnly) flags |= 4;
            if (variable.Shape.Length == 2) flags |= 8;
            if (variable.Shape.Length == 3) flags |= 16;
            return new EraValue(flags);
        }
        var frame = frames[^1];
        Request(ServiceKind.Compile, new DynamicVariablePayload(frame.Function!.Descriptor, text, ValueKind.Integer, new EraValue(0L), currentSpan, ValidateExpression: true));
        return null;
    }
    void IBuiltinContext.DynamicAssignment(string expression, ValueKind kind, EraValue? value, bool allowInvalid)
    {
        var frame = frames[^1];
        Request(ServiceKind.Compile, new DynamicVariablePayload(frame.Function!.Descriptor, expression, kind,
            allowInvalid ? new EraValue(0L) : null, currentSpan, ValidateWrite: value is null, WriteValue: value));
    }
    bool IBuiltinContext.MethodExists(string name)
    {
        var method = game.Index.Find(name);
        if (method is null) return false;
        if (!method.Attributes.Any(a => a is "#FUNCTION" or "#FUNCTIONS")) throw new ScriptException("FUNCTION_TYPE", "动态表达式调用需要函数属性");
        return true;
    }
    long IBuiltinContext.FunctionKind(string name, bool ignoreCase)
    {
        var function = ignoreCase ? game.Index.OrdinaryFunctionsByName.Values.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : game.Index.Find(name);
        return function is null ? 0 : function.Attributes.Contains("#FUNCTIONS") ? 3 : function.Attributes.Contains("#FUNCTION") ? 2 : 1;
    }
    bool IBuiltinContext.CanCall(string name, IReadOnlyList<int> argumentKinds)
    {
        var function = game.Index.Find(name);
        if (function is null) return false;
        for (int i = 0; i < function.Parameters.Length; i++)
        {
            int kind = i < argumentKinds.Count ? argumentKinds[i] : 0;
            var parameter = function.Parameters[i];
            if (kind == 0)
            {
                if (parameter.Reference || game.Index.Options.Profile != "minimal-v1" && !game.Index.Options.CompatiFuncArgOptional && parameter.Default is null) return false;
                continue;
            }
            if (!game.Index.Options.CanBindArgument((ValueKind)((kind & 3) - 1), parameter.Kind, parameter.Reference) || parameter.Reference && (kind & 4) == 0) return false;
            if (parameter.Reference)
            {
                int expected = ReferenceRank(function, parameter);
                if ((kind & 8) != 0 || expected > 0 && (kind >> 6) != expected) return false;
            }
        }
        return true;
    }
    EraValue? IBuiltinContext.Method(string name, ValueKind kind, EraValue? fallback, ImmutableArray<BuiltinArgument> arguments)
    {
        var method = game.Index.Find(name);
        if (method is null) return fallback ?? throw new ScriptException("UNDEFINED_FUNCTION", "动态函数不存在: " + name);
        if (!method.Attributes.Contains(kind == ValueKind.String ? "#FUNCTIONS" : "#FUNCTION")) throw new ScriptException("FUNCTION_TYPE", "动态函数返回类型不符");
        var values = arguments.Take(method.Parameters.Length).Select(a => new Operand(a.Value, a.Reference, a.IsOmitted)).ToArray();
        Push(method, values, 1); return null;
    }
    int IBuiltinContext.CallableArity(string name) => game.Index.Find(name)?.Parameters.Length ?? -1;
    bool IBuiltinContext.CallableReference(string name, int parameter)
        => game.Index.Find(name) is { } function && parameter >= 0 && parameter < function.Parameters.Length && function.Parameters[parameter].Reference;
    bool IBuiltinContext.CallableArrayReference(string name, int parameter)
        => game.Index.Find(name) is { } function && parameter >= 0 && parameter < function.Parameters.Length
            && function.Parameters[parameter].Reference && ReferenceRank(function, function.Parameters[parameter]) > 0;
    VariableDescriptor IBuiltinContext.VariableInfo(SlotReference reference)
    {
        var cell = CellFor(reference);
        return cell.Descriptor ?? new(reference.VariableId, reference.VariableId, cell.Kind, cell.Values.Length);
    }
    VariableDescriptor IBuiltinContext.VariableInfo(string name)
    {
        name = name.ToUpperInvariant();
        if (frames.Count > 0)
        {
            var frame = frames[^1];
            if (frame.Aliases.TryGetValue(name, out var alias) && CellFor(alias).Descriptor is { } descriptor) return descriptor;
            if (frame.Locals.TryGetValue(name, out var local) && local.Descriptor is { } localDescriptor) return localDescriptor;
        }
        return game.Index.VariablesByName.GetValueOrDefault(name) ?? throw new ScriptException("UNDEFINED_VARIABLE", "未定义变量: " + name);
    }
    string IBuiltinContext.FormatField(string text, long width, bool left)
        => FormatFieldSupport.Pad(text, width, left, Metrics);
    private void Write(SlotReference reference, EraValue value)
    {
        var cell = CellFor(reference);
        if (cell.Descriptor?.ReadOnly == true) throw new ScriptException("READ_ONLY", "常量不可写入");
        cell.Write(reference.Index, value.ConvertTo(cell.Kind));
    }
    private SlotReference ArrayReference(Frame frame, string name, long? character)
    {
        if (frame.Aliases.TryGetValue(name, out var alias)) return alias with { Index = 0, IsCharacterSelector = false };
        bool local = frame.Locals.TryGetValue(name, out Cell? target);
        if (!local && game.Index.VariablesByName.GetValueOrDefault(name) is { Scope: VariableScope.Character })
            return new(name, 0, 0, epoch, CharacterId: characters.IdAt(character ?? globals["TARGET"].Values[0].AsInt()));
        target ??= globals.GetValueOrDefault(name) ?? throw new ScriptException("VARIABLE", "变量不存在: " + name);
        if (target.Descriptor?.Reference == true) throw new ScriptException("UNBOUND_REFERENCE", "引用变量尚未绑定: " + name);
        return new(name, 0, local ? frame.Id : 0, epoch,
            target.Descriptor?.Scope == VariableScope.FunctionStatic ? frame.Descriptor.Id : null)
        { IsCharacterSelector = !local && target.Descriptor?.Scope == VariableScope.System && name is "MASTER" or "TARGET" or "ASSI" };
    }
    private SlotReference Reference(Frame frame, string name, ReadOnlySpan<long> indices, bool explicitIndices = true)
    {
        if (frame.Aliases.TryGetValue(name, out var alias))
        {
            var cell = CellFor(alias);
            long index = alias.ArgumentRank == 0 ? indices.Length == 1 && indices[0] == 0 ? 0 : throw new ScriptException("RANK", "标量引用不接受数组下标")
                : cell.Descriptor?.OffsetSpan(indices) ?? indices[0];
            long offset = checked(alias.Index + index);
            if (offset < 0 || offset >= cell.Length) throw new ScriptException("BOUNDS", "引用下标越界");
            return alias with { Index = (int)offset, IsCharacterSelector = false };
        }
        bool local = frame.Locals.TryGetValue(name, out Cell? target);
        if (!local && game.Index.VariablesByName.GetValueOrDefault(name) is { Scope: VariableScope.Character } spec)
        {
            bool explicitCharacter = spec.CharacterScalar ? explicitIndices : indices.Length == spec.Rank + 1;
            long character = explicitCharacter ? indices[0] : globals["TARGET"].Values[0].AsInt();
            ReadOnlySpan<long> subscripts = spec.CharacterScalar ? [0L] : explicitCharacter ? indices[1..] : indices;
            return new(name, spec.OffsetSpan(subscripts), 0, epoch, CharacterId: characters.IdAt(character));
        }
        target ??= globals.GetValueOrDefault(name) ?? throw new ScriptException("VARIABLE", "变量不存在: " + name);
        if (target.Descriptor?.Reference == true) throw new ScriptException("UNBOUND_REFERENCE", "引用变量尚未绑定: " + name);
        long offsetIndex = target.Descriptor?.OffsetSpan(indices) ?? indices[0];
        if (offsetIndex < 0 || offsetIndex >= target.Length) throw new ScriptException("BOUNDS", "数组下标越界: " + name);
        return new(name, (int)offsetIndex, local ? frame.Id : 0, epoch,
            target.Descriptor?.Scope == VariableScope.FunctionStatic ? frame.Descriptor.Id : null)
        { IsCharacterSelector = !local && target.Descriptor?.Scope == VariableScope.System && name is "MASTER" or "TARGET" or "ASSI" };
    }
    private EraValue ReadVariable(Frame frame, string name, ReadOnlySpan<long> indices, bool explicitIndices, VariableDescriptor? resolved = null)
    {
        if (frame.Aliases.ContainsKey(name)) return Read(Reference(frame, name, indices, explicitIndices));
        bool local = frame.Locals.TryGetValue(name, out Cell? target);
        if (!local && (resolved ?? game.Index.VariablesByName.GetValueOrDefault(name)) is { Scope: VariableScope.Character } spec)
            return ReadCharacterVariable(spec, indices, explicitIndices);
        target ??= globals.GetValueOrDefault(name) ?? throw new ScriptException("VARIABLE", "变量不存在: " + name);
        if (target.Descriptor?.Reference == true) throw new ScriptException("UNBOUND_REFERENCE", "引用变量尚未绑定: " + name);
        long index = target.Descriptor?.OffsetSpan(indices) ?? indices[0];
        if (index < 0 || index >= target.Length) throw new ScriptException("BOUNDS", "数组下标越界: " + name);
        return target.Read((int)index);
    }
    private EraValue ReadCharacterVariable(VariableDescriptor spec, ReadOnlySpan<long> indices, bool explicitIndices)
    {
        bool explicitCharacter = spec.CharacterScalar ? explicitIndices : indices.Length == spec.Rank + 1;
        long character = explicitCharacter ? indices[0] : globals["TARGET"].Values[0].AsInt();
        ReadOnlySpan<long> subscripts = spec.CharacterScalar ? [0L] : explicitCharacter ? indices[1..] : indices;
        int offset = spec.OffsetSpan(subscripts);
        var values = characters.ValuesAt(character, spec.Name);
        if (spec.Reference) throw new ScriptException("UNBOUND_REFERENCE", "引用变量尚未绑定: " + spec.Name);
        return values[offset];
    }
    private static EraValue ReadLocalVariable(Frame frame, Instruction instruction, ReadOnlySpan<long> indices)
    {
        if ((uint)instruction.LocalSlot >= (uint)frame.LocalSlots.Length) throw new ScriptException("BYTECODE", "局部变量位置无效");
        var cell = frame.LocalSlots[instruction.LocalSlot];
        long index = cell.Descriptor?.OffsetSpan(indices) ?? indices[0];
        if (index < 0 || index >= cell.Length) throw new ScriptException("BOUNDS", "数组下标越界: " + instruction.Text);
        return cell.Read((int)index);
    }
    private bool allowDoTrain;
    void IBuiltinContext.DoTrain(long command)
    {
        if (Mode != SystemMode.Train || !allowDoTrain || game.SystemFlow is null)
            throw new ScriptException("DOTRAIN_CONTEXT", "当前回调不能使用 DOTRAIN");
        var continuation = game.SystemFlow.DoTrain(command, this).GetEnumerator();
        ClearPendingThrow();
        systemFlow?.Dispose(); systemFlow = continuation;
        frames.Clear(); stack.Clear(); allowDoTrain = false;
    }
    public StepResult Step(ExecutionBudget budget)
    {
        if (budget.Instructions <= 0) throw new ArgumentOutOfRangeException(nameof(budget));
        if (budget.Cancellation.IsCancellationRequested) Apply(new CancelMessage());
        if (state != StopReason.Yielded) return new(state, service);
        long revision = document.Revision;
        try
        {
            for (int i = 0; i < budget.Instructions; i++)
            {
                if (budget.Cancellation.IsCancellationRequested) { Apply(new CancelMessage()); break; }
                if (frames.Count == 0)
                {
                    if (systemFlow is not null && systemFlow.MoveNext())
                    {
                        switch (systemFlow.Current)
                        {
                            case FlowCall call: allowDoTrain = call.AllowDoTrain; Push(game.Index.ByFunctionId[call.FunctionId], [], 0); break;
                            case FlowInput wait: ((IBuiltinContext)this).Input(wait.Kind); break;
                            case FlowSwitch change: Begin(change.Mode); break;
                            case FlowFill fill: ApplyFlowFill(fill); break;
                            case FlowButton button: document.Button(button.Text, button.Value); document.Text("", true); break;
                            case FlowTemporary temporary: document.Temporary(temporary.Text); break;
                        }
                        AdvanceInputMacro();
                        if (state != StopReason.Yielded) break;
                        continue;
                    }
                    systemFlow?.Dispose(); systemFlow = null;
                    if (naturalEnd is { } fault) throw new ScriptException(fault.Code, fault.Message);
                    state = StopReason.Completed; break;
                }
                var frame = frames[^1];
                if (!frame.Entered) { frame.Entered = true; Log(TraceKind.Enter, frame.Descriptor.Name); functionEntered?.Invoke(frame.Descriptor); }
                if (frame.Function is null)
                { Request(ServiceKind.Compile, new CompilePayload(frame.Descriptor.Id)); break; }
                if (frame.Pc >= frame.Function.Code.Length) throw new ScriptException("BYTECODE", "指令地址越界");
                var instruction = frame.Function.Code[frame.Pc++]; currentSpan = instruction.Span; instructions++;
                Execute(frame, instruction);
                AdvanceInputMacro();
                if (state != StopReason.Yielded) break;
            }
        }
        catch (Exception e) when (e is ScriptException or OverflowException or DivideByZeroException)
        { Fault(e is ScriptException script ? script.Diagnostic : new("ARITHMETIC", e.Message)); }
        if (revision != document.Revision) Log(TraceKind.Presentation, "文档更新");
        if (state == StopReason.Yielded) Log(TraceKind.Yielded, "执行预算");
        return new(state, service);
    }
    private void Fault(Diagnostic diagnostic)
    {
        if (beforeThrowCallback == "BEFORE_THROW") diagnostic = pendingThrow ?? diagnostic;
        ClearPendingThrow();
        diagnostics.Add(diagnostic with { Message = loadCommitted ? "存档已加载，后续执行失败：" + diagnostic.Message : diagnostic.Message,
            Span = diagnostic.Span ?? currentSpan, Stack = frames.Select(f => f.Descriptor.Name).ToImmutableArray() });
        state = StopReason.Faulted; input = null; service = null; Log(TraceKind.Fault, diagnostic.Code);
    }
    private void ApplyFlowFill(FlowFill fill)
    {
        var specification = game.Index.VariablesByName.GetValueOrDefault(fill.Variable)
            ?? throw new ScriptException("FLOW_VARIABLE", "系统流程缺少变量: " + fill.Variable);
        if (specification.ReadOnly) throw new ScriptException("READ_ONLY", "系统流程不能改写常量");
        void Fill(EraValue[] values)
        {
            Array.Fill(values, EraValue.Zero(specification.Kind));
            if (!fill.Initial.IsDefault) fill.Initial.AsSpan(0, Math.Min(fill.Initial.Length, values.Length)).CopyTo(values);
        }
        if (specification.Scope == VariableScope.Character)
            for (int i = 0; i < characters.Count; i++) Fill(characters.Values(characters.IdAt(i), fill.Variable));
        else Fill(globals[fill.Variable].Values);
    }
    private void Request(ServiceKind kind, ServicePayload payload)
    { service = new(epoch, ++nextOperation, kind, payload); state = StopReason.WaitingService; Log(TraceKind.ServiceRequested, kind.ToString()); }
    private void Execute(Frame frame, Instruction instruction)
    {
        document.Source = instruction.Span;
        switch (instruction.Op)
        {
            case Op.Builtin:
            {
                if (!builtins.Handlers.TryGetValue(instruction.Text, out var handler)) throw new ScriptException("BUILTIN_MISSING", "缺少内建处理器");
                var signature = builtinSignatures[instruction.Text];
                if (instruction.Number < signature.Parameters.Length || instruction.Number > signature.Parameters.Length && signature.Variadic is null) throw new ScriptException("BYTECODE", "内建参数数目错误");
                var values = new BuiltinArgument[instruction.Number];
                for (int i = values.Length - 1; i >= 0; i--)
                {
                    var operand = Pop();
                    var parameter = signature.Parameter(i);
                    if (parameter.Reference && operand.Reference is null && parameter.Default != operand.Value) throw new ScriptException("REFERENCE", "内建参数需要引用");
                    var value = operand.Value;
                    values[i] = new(value, parameter.Reference || parameter.CaptureReference ? operand.Reference : null, operand.IsOmitted);
                    if (parameter.Kind is { } kind && value.Kind != kind) throw new ScriptException("TYPE", "内建参数类型错误");
                }
                int depth = frames.Count;
                var result = handler.Execute(this, System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(values));
                if (signature.ReturnType is { } returnType)
                {
                    if (signature.NumericResult && values.Any(a => a.Value.Kind == ValueKind.Float)) returnType = ValueKind.Float;
                    if (result is null && frames.Count > depth) break;
                    if (result is null && state == StopReason.WaitingService) { pendingBuiltinType = returnType; break; }
                    if (result is not { } value || value.Kind != returnType) throw new ScriptException("BUILTIN_RESULT", "内建返回类型错误");
                    Put(value);
                }
                else if (result is not null) throw new ScriptException("BUILTIN_RESULT", "无返回值内建产生了值");
                break;
            }
            case Op.Constant: stack.Add(new(instruction.Value, IsOmitted: instruction.Text == "$omitted")); break;
            case Op.StringCall:
                Request(ServiceKind.Compile, new DynamicVariablePayload(frame.Function!.Descriptor, Value().AsString(), ValueKind.Integer, null,
                    instruction.Span, CallString: true, TryCall: instruction.Text.StartsWith("TRY", StringComparison.Ordinal)));
                pendingBuiltinType = ValueKind.Integer;
                break;
            case Op.Throw: StartThrow(Value().AsString()); break;
            case Op.ReadLocalConstant:
            {
                if ((uint)instruction.LocalSlot >= (uint)frame.LocalSlots.Length) throw new ScriptException("BYTECODE", "局部变量位置无效");
                var cell = frame.LocalSlots[instruction.LocalSlot];
                if ((uint)instruction.Number >= (uint)cell.Length) throw new ScriptException("BOUNDS", "数组下标越界: " + instruction.Text);
                Put(cell.Read(instruction.Number));
                break;
            }
            case Op.ReadVariable:
            case Op.ReadCharacter:
            case Op.ReadLocal:
            {
                int indexCount = Math.Max(1, instruction.Number);
                Span<long> indices = indexCount <= 4 ? stackalloc long[indexCount] : new long[indexCount];
                for (int i = indices.Length - 1; i >= 0; i--) indices[i] = Value().AsInt();
                Put(instruction.Op switch
                {
                    Op.ReadLocal => ReadLocalVariable(frame, instruction, indices),
                    Op.ReadCharacter => ReadCharacterVariable(instruction.ResolvedVariable ?? throw new ScriptException("BYTECODE", "角色读取缺少声明"), indices, instruction.Value.Integer != 0),
                    _ => ReadVariable(frame, instruction.Text, indices, instruction.Value.Integer != 0, instruction.ResolvedVariable)
                });
                break;
            }
            case Op.Reference:
            {
                int indexCount = Math.Max(1, instruction.Number);
                Span<long> indices = indexCount <= 4 ? stackalloc long[indexCount] : new long[indexCount];
                for (int i = indices.Length - 1; i >= 0; i--) indices[i] = Value().AsInt();
                var reference = (instruction.Value.Integer & 2) != 0
                    ? ArrayReference(frame, instruction.Text, (instruction.Value.Integer & 1) != 0 ? indices[0] : null)
                    : Reference(frame, instruction.Text, indices, instruction.Value.Integer != 0);
                var specification = frame.Locals.GetValueOrDefault(instruction.Text)?.Descriptor
                    ?? game.Index.VariablesByName.GetValueOrDefault(instruction.Text);
                if (specification is not null) reference = reference with { ArgumentRank = specification.ReferenceRank ?? (specification.CharacterScalar ? 0 : specification.Rank) };
                stack.Add(new(Read(reference), reference)); break;
            }
            case Op.Load: Put(Value()); break;
            case Op.Store:
            { var value = Value(); var reference = Pop().Reference ?? throw new ScriptException("REFERENCE", "需要写引用"); Write(reference, value); break; }
            case Op.StoreArray:
            {
                var values = new EraValue[instruction.Number];
                for (int i = values.Length - 1; i >= 0; i--) values[i] = Value();
                var reference = Pop().Reference ?? throw new ScriptException("REFERENCE", "数组连续赋值需要引用");
                var cell = CellFor(reference);
                int width = cell.Descriptor is { Shape.Length: > 0 } spec ? spec.Shape[^1] : cell.Values.Length;
                if (values.Length > cell.Values.Length - reference.Index || values.Length > width - reference.Index % width)
                    throw new ScriptException("BOUNDS", "连续赋值越过数组行末");
                if (cell.Descriptor?.ReadOnly == true) throw new ScriptException("READ_ONLY", "常量不可写入");
                for (int i = 0; i < values.Length; i++) values[i] = values[i].ConvertTo(cell.Kind);
                values.CopyTo(cell.Values, reference.Index); break;
            }
            case Op.Duplicate: stack.Add(stack[^1]); break;
            case Op.Pop: Pop(); break;
            case Op.Unary:
                if (instruction.Text is "pre" or "post")
                { var reference = Pop().Reference!; var old = Read(reference); var updated = Binary("+", old, new((long)instruction.Number)); Write(reference, updated); Put(instruction.Text == "post" ? old : updated); }
                else Put(Unary(instruction.Text, Value()));
                break;
            case Op.Binary: { var right = Value(); var left = Value(); Put(Binary(instruction.Text, left, right)); break; }
            case Op.AdvanceReference:
            {
                var reference = Pop().Reference ?? throw new ScriptException("REFERENCE", "数组连续赋值需要引用");
                var cell = CellFor(reference); int next = checked(reference.Index + 1);
                if (next >= cell.Values.Length || cell.Descriptor is { } spec && next % spec.Shape[^1] == 0) throw new ScriptException("BOUNDS", "连续赋值越过数组行末");
                stack.Add(new(default, reference with { Index = next })); break;
            }
            case Op.Jump: frame.Pc = instruction.Number; break;
            case Op.JumpFalse: if (Value().AsInt() == 0) frame.Pc = instruction.Number; break;
            case Op.JumpTrue: if (Value().AsInt() != 0) frame.Pc = instruction.Number; break;
            case Op.Call: case Op.DynamicCall:
            {
                int argumentStart = stack.Count - instruction.Number;
                int callStart = argumentStart - (instruction.Op == Op.DynamicCall ? 1 : 0);
                try
                {
                    // Push 只绑定参数，不改变值栈；绑定完成后统一移除调用参数。
                    var args = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(stack).Slice(argumentStart, instruction.Number);
                    var targetOperand = instruction.Op == Op.DynamicCall ? stack[callStart] : default;
                    string target = instruction.Op == Op.Call ? instruction.Text
                        : (targetOperand.Reference is { } reference ? Read(reference) : targetOperand.Value).AsString();
                    var function = instruction.Op == Op.Call ? game.Index.ByFunctionId.GetValueOrDefault(target) : game.Index.Find(target);
                    if (function is null && instruction.Text == "TRYCALLFORM") break;
                    if (function is null) throw new ScriptException("UNDEFINED_FUNCTION", "调用目标不存在: " + target);
                    int mode = (int)instruction.Value.Integer;
                    if (function.Attributes.Any(a => a is "#FUNCTION" or "#FUNCTIONS") != (mode == 1))
                        throw new ScriptException("FUNCTION_TYPE", mode == 1 ? "表达式调用需要 FUNCTION 属性" : "普通调用不能调用表达式函数");
                    Push(function, args, mode == 2 ? frame.Mode : mode, callStart);
                    frames[^1].IsJump = mode == 2;
                }
                finally { stack.RemoveRange(callStart, stack.Count - callStart); }
                break;
            }
            case Op.Return:
            {
                EraValue single = default;
                EraValue[]? values = null;
                if (instruction.Number == 1) single = Value();
                else if (instruction.Number > 1)
                {
                    values = new EraValue[instruction.Number];
                    for (int i = values.Length - 1; i >= 0; i--) values[i] = Value();
                }
                while (true)
                {
                    frames.RemoveAt(frames.Count - 1);
                    if (stack.Count != frame.StackBase) throw new ScriptException("STACK", "函数返回时栈不平衡");
                    Log(TraceKind.Return, frame.Descriptor.Name);
                    RecycleFrame(frame);
                    if (!frame.IsJump) break;
                    frame = frames.LastOrDefault() ?? throw new ScriptException("STACK", "跳转缺少源调用帧");
                }
                if (instruction.Text != "RETURNF")
                    for (int i = 0; i < instruction.Number && i < globals["RESULT"].Values.Length; i++)
                        globals["RESULT"].Values[i] = new((values is null ? single : values[i]).AsInt());
                if (frame.Mode == 1)
                {
                    if (instruction.Number == 0) throw new ScriptException("BYTECODE", "表达式函数缺少返回值");
                    Put(values is null ? single : values[0]);
                }
                if (pendingThrow is not null && frames.Count == beforeThrowDepth) AdvanceBeforeThrow();
                break;
            }
            default: throw new ScriptException("BYTECODE", "未知指令");
        }
    }
    private EraValue Unary(string op, EraValue value) => op switch
    {
        "text" => new(value.ToString()), "forms" => new(ProtectFormatEscapes(value.AsString())), "nonnegative" => new(Math.Max(0, value.AsInt())), "bool" => new(value.AsFloat() != 0 ? 1L : 0L), "+" => value,
        "-" => value.Kind == ValueKind.Float ? new(-value.Float) : Binary("-", new(0L), value), "!" => new(value.AsFloat() == 0 ? 1L : 0L), "~" => new(~value.AsInt()),
        "nonzero" => value.AsInt() == 0 ? throw new ScriptException("FOR_STEP", "FOR 步长不能为零") : value,
        "call_arguments" => value.AsInt() == 0 ? throw new ScriptException("TYPE", "动态函数参数不匹配") : value,
        "call_target" => value.AsInt() == 0 ? throw new ScriptException("UNDEFINED_FUNCTION", "动态调用目标不存在") : value,
        "fill_arguments" => value.AsInt() == 0 ? throw new ScriptException("TYPE", "动态填充值类型不匹配") : value,
        _ => throw new ScriptException("OPERATOR", "未知运算符")
    };
    private static string ProtectFormatEscapes(string text)
    {
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != '\\') { result.Append(c); continue; }
            result.Append('\\');
            if (++i == text.Length) { result.Append('\\'); break; }
            c = text[i];
            if (c == '\\') { result.Append('\\'); continue; }
            if (c is not ('{' or '}' or '%' or '@')) result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }
    private EraValue Binary(string op, EraValue left, EraValue right)
    {
        if (op == "*" && left.Kind == ValueKind.Integer && right.Kind == ValueKind.String) (left, right) = (right, left);
        if (op == "*" && left.Kind == ValueKind.String && right.Kind == ValueKind.Integer)
        {
            long count = right.AsInt();
            if (count < 0 || count > 16_000_000 || left.Text.Length * count > 16_000_000) throw new ScriptException("STRING_LIMIT", "字符串重复长度超限");
            return new(string.Concat(Enumerable.Repeat(left.Text, (int)count)));
        }
        if ((left.Kind == ValueKind.Float || right.Kind == ValueKind.Float) && left.Kind != ValueKind.String && right.Kind != ValueKind.String)
        {
            double x = left.AsFloat(), y = right.AsFloat();
            return op switch
            {
                "+" => new(x + y), "-" => new(x - y), "*" => new(x * y), "/" => new(y == 0 ? WarnZero() : x / y), "%" => new(y == 0 ? WarnZero() : x % y),
                "==" => new(x == y ? 1L : 0L), "!=" => new(x != y ? 1L : 0L), "<" => new(x < y ? 1L : 0L), ">" => new(x > y ? 1L : 0L), "<=" => new(x <= y ? 1L : 0L), ">=" => new(x >= y ? 1L : 0L),
                _ => throw new ScriptException("OPERATOR", "浮点运算不支持: " + op)
            };
        }
        if (left.Kind != right.Kind) throw new ScriptException("TYPE", "运算值类型不同");
        if (left.Kind == ValueKind.String && op == "+") return new(left.Text + right.Text);
        int compare = left.Kind == ValueKind.String ? string.CompareOrdinal(left.Text, right.Text) : left.Integer.CompareTo(right.Integer);
        bool? comparison = op switch { "==" => compare == 0, "!=" => compare != 0, "<" => compare < 0, ">" => compare > 0, "<=" => compare <= 0, ">=" => compare >= 0, _ => null };
        if (comparison is { } boolean) return new(boolean ? 1L : 0L);
        long a = left.AsInt(), b = right.AsInt();
        try
        {
            return new(op switch
            {
                "+" => checked(a + b), "-" => checked(a - b), "*" => checked(a * b),
                "/" => b == 0 ? WarnZero() : a / b, "%" => b == 0 ? WarnZero() : a % b,
                "&" => a & b, "|" => a | b, "^" => a ^ b, "<<" => a << (int)b, ">>" => a >> (int)b,
                "^^" => (a != 0) ^ (b != 0) ? 1L : 0L,
                _ => throw new ScriptException("OPERATOR", "未支持运算符: " + op)
            });
        }
        catch (OverflowException) when (op is "+" or "-" or "*")
        {
            diagnostics.Add(new("INTEGER_OVERFLOW", "整数溢出，结果饱和", currentSpan, Severity.Warning));
            return new(op == "*" ? (a > 0) == (b > 0) ? long.MaxValue : long.MinValue : a >= 0 ? long.MaxValue : long.MinValue);
        }
    }
    private long WarnZero() { diagnostics.Add(new("DIVIDE_ZERO", "零除，结果为零", currentSpan, Severity.Warning)); return 0; }
    private void Restart()
    {
        if (game.SystemFlow is not null) { Begin(SystemMode.Title); return; }
        ClearPendingThrow();
        epoch++; frames.Clear(); stack.Clear(); document.Reset(); input = null; state = StopReason.Yielded; naturalEnd = null;
        string entry = game.Flow?.RestartEntry ?? game.Entry;
        Push(game.Index.Find(entry) ?? throw new ScriptException("ENTRY", "缺少入口: " + entry), [], 0);
    }
    private void Begin(SystemMode mode)
    {
        var policy = game.SystemFlow ?? throw new ScriptException("FLOW_UNSUPPORTED", "当前配置没有系统流程");
        ClearPendingThrow();
        epoch++; frames.Clear(); stack.Clear(); input = null; savedInput = null; service = null; pendingBuiltinType = null; naturalEnd = null;
        ((IBuiltinContext)this).SkipDisplay = false;
        systemFlow?.Dispose(); Mode = mode; systemFlow = policy.Start(mode, this).GetEnumerator(); state = StopReason.Yielded;
        if (mode == SystemMode.Title) document.Reset(true);
    }
    private SaveCompatibility SaveCompatibilityPolicy => game.SaveCompatibility ?? new(game.Profile);
    private IEnumerable<VariableDescriptor> SavedVariables(bool global) => game.Index.Variables.Where(v => v.Scope != VariableScope.Character && (global ? v.GlobalSaved : v.Saved && !v.GlobalSaved));
    private ExtensionSaveNames ExtensionNames(bool global) => global ? game.Index.Options.GlobalExtensions : game.Index.Options.GameExtensions;
    private SaveSnapshot Snapshot(bool global = false) => new(game.Index.Options.Profile == "minimal-v1" ? 1 : 2, game.GameId, game.Profile, DateTimeOffset.UtcNow,
        SavedVariables(global).Select(v => new SavedVariable(v.Name, v.Kind, v.Length, globals[v.Name].Values.ToImmutableArray()) { Shape = v.Shape }).ToImmutableArray(), randomState)
        {
            Characters = global ? [] : characters.Snapshot(), GameVersion = globals.TryGetValue("GAMEBASE_VERSION", out var version) ? version.Values[0].AsInt() : SaveCompatibilityPolicy.GameVersion,
            CompatibilityFingerprint = SaveCompatibilityPolicy.Fingerprint,
            Tables = extensions?.Capture(ExtensionNames(global).Tables) ?? [],
            XmlDocuments = extensions?.CaptureXml(ExtensionNames(global).XmlDocuments) ?? ImmutableDictionary<string, string>.Empty,
            Maps = stringMaps.Where(p => ExtensionNames(global).Maps.Contains(p.Key)).ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableDictionary(StringComparer.Ordinal), StringComparer.Ordinal)
        };
    public void ValidateSave(SaveSnapshot snapshot) => Restore(snapshot, validateOnly: true);
    private void Restore(SaveSnapshot snapshot, bool global = false, bool validateOnly = false)
    {
        if (snapshot.SchemaVersion is not (1 or 2) || snapshot.FlowVersion != 1 || snapshot.GameId != game.GameId || !SaveCompatibilityPolicy.CanRead(snapshot))
            throw new ScriptException("SAVE_COMPATIBILITY", "存档版本、游戏或配置不匹配");
        if (snapshot.SchemaVersion == 1 && (!snapshot.Characters.IsEmpty || !snapshot.Tables.IsEmpty || snapshot.XmlDocuments is null || snapshot.XmlDocuments.Count != 0 || snapshot.Maps is null || snapshot.Maps.Count != 0))
            throw new ScriptException("SAVE_COMPATIBILITY", "第一版存档不能包含角色或扩展数据");
        var expected = SavedVariables(global).ToArray();
        if (snapshot.Variables.IsDefault || snapshot.Variables.Length != expected.Length) throw new ScriptException("SAVE_VARIABLES", "保存变量集合不匹配");
        var candidate = new Dictionary<string, Cell>();
        foreach (var variable in snapshot.Variables)
        {
            if (variable is null) throw new ScriptException("SAVE_VARIABLES", "存档变量缺失");
            var definition = expected.FirstOrDefault(v => v.Name == variable.Name);
            if (definition is null || definition.Kind != variable.Kind || definition.Length != variable.Length || variable.Values.IsDefault
                || variable.Values.Length != variable.Length || variable.Values.Any(v => v.Kind != variable.Kind || v.Kind == ValueKind.String && v.Text is null)
                || snapshot.SchemaVersion == 2 && (variable.Shape.IsDefault || !variable.Shape.SequenceEqual(definition.Shape))
                || snapshot.SchemaVersion == 1 && definition.Shape.Length != 1
                || !candidate.TryAdd(variable.Name, new(variable.Values.ToArray(), variable.Kind, definition)))
                throw new ScriptException("SAVE_VARIABLES", "存档变量规格错误");
        }
        if (global && !snapshot.Characters.IsEmpty) throw new ScriptException("SAVE_CHARACTERS", "全局文件不能包含角色");
        var characterCandidate = global ? characters : characters.Candidate(snapshot.Characters);
        if (!global && snapshot.SchemaVersion == 2)
            foreach (string name in new[] { "TARGET", "MASTER", "ASSI", "PLAYER" })
                if (candidate.TryGetValue(name, out var selector) && selector.Values.Length > 0 && selector.Values[0].AsInt() is var selection
                    && (selection < -1 || selection >= Math.Max(1, characterCandidate.Count)))
                    throw new ScriptException("SAVE_SELECTION", "角色选择下标无效");
        var extensionNames = ExtensionNames(global);
        if (snapshot.Maps is null || snapshot.Maps.Any(p => !extensionNames.Maps.Contains(p.Key) || p.Value is null || p.Value.Any(v => v.Value is null)))
            throw new ScriptException("SAVE_MAP", "存档映射集合无效");
        if (snapshot.Maps.Sum(p => (long)p.Value.Count) > 1_000_000) throw new ScriptException("SAVE_LIMIT", "存档映射总量超限");
        var mapCandidate = snapshot.Maps.ToDictionary(p => p.Key, p => p.Value.ToDictionary(StringComparer.Ordinal), StringComparer.Ordinal);
        if (extensions is null && !snapshot.Tables.IsEmpty) throw new ScriptException("SAVE_TABLE", "宿主没有数据表服务");
        var commitTables = extensions?.PrepareRestore(snapshot.Tables, extensionNames.Tables);
        if (snapshot.XmlDocuments is null || snapshot.XmlDocuments.Any(p => !extensionNames.XmlDocuments.Contains(p.Key) || p.Value is null)
            || snapshot.XmlDocuments.Sum(p => (long)p.Value.Length) > 16_000_000 || extensions is null && snapshot.XmlDocuments.Count != 0)
            throw new ScriptException("SAVE_XML", "存档 XML 集合无效");
        var commitXml = extensions?.PrepareRestoreXml(snapshot.XmlDocuments, extensionNames.XmlDocuments);
        if (validateOnly) return;
        if (!global) ResetOrdinaryVariables();
        foreach (var pair in candidate) globals[pair.Key] = pair.Value;
        foreach (string name in extensionNames.Maps)
            if (stringMaps.TryGetValue(name, out var map)) map.Clear();
        foreach (var pair in mapCandidate) stringMaps[pair.Key] = pair.Value;
        commitTables?.Invoke();
        commitXml?.Invoke();
        if (global) { globals["RESULT"].Values[0] = new(1L); return; }
        characters = characterCandidate;
        var continuation = savedInput is null ? game.Flow?.ScriptLoad : game.Flow?.UiLoad;
        randomState = snapshot.RandomState; epoch++; service = null; savedInput = null;
        ClearPendingThrow(); frames.Clear(); stack.Clear(); document.Reset(true); input = null; state = StopReason.Yielded;
        naturalEnd = continuation?.OnNaturalEnd; loadCommitted = true;
        if (game.SystemFlow is not null)
        { systemFlow?.Dispose(); Mode = SystemMode.Load; systemFlow = game.SystemFlow.Start(Mode, this).GetEnumerator(); naturalEnd = null; }
        else foreach (string id in (continuation?.Functions ?? []).Reverse()) Push(game.Index.ByFunctionId[id], [], 0);
        Log(TraceKind.Loaded, epoch.ToString());
    }
    public ApplyResult Apply(SessionMessage message)
    {
        if (message is AnimationFrames animation)
        {
            if (animation.Epoch != epoch || animation.DocumentRevision != document.Revision || animation.InputRequestId != input?.Id
                || state != StopReason.WaitingInput || document.AnimationInterval == 0)
                return new(false, "过期动画帧");
            if (animation.Error is { } error) Fault(error);
            else if (animation.Data is { } frames) document.UpdateAnimation(frames.Images);
            return new(true, "动画刷新");
        }
        if (message is ViewportMessage viewport)
        {
            if (viewport.Columns is <= 0 or > 10000 || viewport.Rows is <= 0 or > 10000) return new(false, "显示尺寸无效");
            viewportColumns = viewport.Columns; viewportRows = viewport.Rows; return new(true, "显示尺寸已更新");
        }
        if (state is StopReason.Completed or StopReason.Cancelled or StopReason.Faulted) return new(false, "会话已结束");
        if (message is KeyStateMessage key)
        {
            if (key.Epoch != epoch || key.Key is < 0 or > 255) return new(false, "过期或无效按键");
            if (key.Down && !keysDown[key.Key]) keysPressed[key.Key] = true;
            keysDown[key.Key] = key.Down; return new(true, "按键状态已更新");
        }
        if (message is TextBoxMessage textBox)
        {
            if (textBox.Epoch != epoch || textBox.Revision != textBoxRevision) return new(false, "过期输入框编辑");
            textBoxText = textBox.Text; return new(true, "输入框已更新");
        }
        if (message is PointerMessage pointer)
        {
            if (pointer.Epoch != epoch || pointer.DocumentRevision != document.Revision) return new(false, "过期指向");
            mouseX = pointer.X; mouseY = pointer.Y;
            hoveredButton = pointer.ButtonId is { } id && document.Buttons().TryGetValue(id, out var value) ? value.ToString() : "";
            hoveredEpoch = epoch; hoveredRevision = pointer.DocumentRevision; return new(true, "指向已更新");
        }
        if (message is CancelMessage) { ClearPendingThrow(); state = StopReason.Cancelled; input = null; service = null; epoch++; Log(TraceKind.Cancelled, "取消"); return new(true, "取消"); }
        if (message is TimeoutMessage timeout)
        {
            if (input?.Deadline is not { } deadline || timeout.Now < deadline) return new(false, "尚未到期");
            AcceptTimeout(); return new(true, "超时默认值");
        }
        if (message is InputReply reply)
        {
            if (input is null || reply.Epoch != epoch || reply.RequestId != input.Id) return Reject("过期输入");
            if (input.Deadline is { } deadline && reply.ReceivedAt >= deadline) { AcceptTimeout(); return Reject("输入已超时"); }
            if (input.IgnoreInput) return Reject("当前等待不接受输入");
            if (reply.ButtonId is null && reply.Pointer is { } blankClick)
            {
                if (!input.MouseInput || !Enum.IsDefined(blankClick.Button)) return Reject("当前请求不接受空白鼠标输入");
                globals["RESULT"].Values[1] = new((long)blankClick.Button);
                globals["RESULT"].Values[2] = new((long)blankClick.Modifiers);
                ((IBuiltinContext)this).TextBox = "";
                input = input with { Deadline = null };
                messageSkip = false;
                reply = reply with { Text = "", Source = "mouse" };
            }
            if (reply.ButtonId is null && reply.Source == "keyboard" && inputMacroEnabled && InputMacro.HasSyntax(reply.Text))
            {
                try { QueueInputMacro(reply.Text); AdvanceInputMacro(); }
                catch (ScriptException error) { return Reject(error.Message); }
                return new(state != StopReason.WaitingInput, "输入宏已处理");
            }
            if (input.Kind == InputKind.Wait) { Accept(new(0L)); return new(true, "继续"); }
            if (input.SingleCharacter && inputMacroEnabled && reply.ButtonId is null)
                reply = reply with { Text = FirstInputCharacter(reply.Text) };
            EraValue value;
            if (reply.ButtonId is { } id)
            { if (!input.Buttons.TryGetValue(id, out value)) return Reject("按钮无效"); }
            else if (reply.Text.Length == 0 && input.Default is { } fallback && input.Deadline is null) value = fallback;
            else if (input.Kind == InputKind.String) value = new(reply.Text);
            else if (long.TryParse(reply.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long integer)) value = new(integer);
            else return Reject("需要整数");
            if (input.Kind == InputKind.String && value.Kind == ValueKind.Integer) value = new(value.ToString());
            if ((input.Kind == InputKind.Integer) != (value.Kind == ValueKind.Integer)) return Reject("输入类型错误");
            if (input.ButtonsOnly && !input.Buttons.Values.Contains(value) && !(reply.Text.Length == 0 && input.Default == value)) return Reject("输入不对应当前按钮");
            if (input.MouseInput && reply.ButtonId is not null)
            {
                var click = reply.Pointer ?? new(InputMouseButton.Left);
                if (!Enum.IsDefined(click.Button)) return Reject("鼠标按键无效");
                globals["RESULT"].Values[1] = new((long)click.Button);
                globals["RESULT"].Values[2] = new((long)click.Modifiers);
                globals["RESULT"].Values[3] = new(0L);
                globals["RESULTS"].Values[1] = new(value.ToString());
                messageSkip = click.Button == InputMouseButton.Right;
            }
            Accept(value, reply.Source); return new(true, "接受");
        }
        if (message is SaveCommand command)
        {
            if (state != StopReason.WaitingInput) return new(false, "当前不是稳定保存点");
            if (game.SystemFlow?.CanSaveLoad(Mode) == false) return new(false, "当前系统流程不允许打开存读档");
            if (command.Slot < 0) return new(false, "槽位越界");
            savedInput = input; input = null;
            Request(command.Load ? ServiceKind.Load : ServiceKind.Save, command.Load ? new LoadPayload(command.Slot) : new SavePayload(command.Slot, Snapshot()));
            return new(true, "服务已开始");
        }
        if (message is ServiceResult result)
        {
            if (service is null || result.Epoch != epoch || service.Id != result.OperationId) return Reject("过期服务结果");
            var kind = service.Kind;
            if (result.Error is not null)
            {
                if (ContinueScriptSaveFailure(result.Error)) return new(true, result.Error.Code);
                if (savedInput is not null) { diagnostics.Add(result.Error); input = savedInput; savedInput = null; service = null; state = StopReason.WaitingInput; }
                else Fault(result.Error);
                return new(false, result.Error.Code);
            }
            bool global = service.Payload is LoadPayload { Scope: SaveScope.Global } or SavePayload { Scope: SaveScope.Global };
            bool matches = service.Payload is CharacterFilePayload characterRequest
                ? characterRequest.Operation == CharacterFileOperation.Save ? result.Data is SavedData { Committed: true } : result.Data is CharacterFileData
                : pendingBuiltinType is { } expectedType ? result.Data is BuiltinValueData valueData && valueData.Value.Kind == expectedType || service.Payload is DynamicVariablePayload && result.Data is DynamicVariableData dynamicData && dynamicData.Kind == expectedType || expectedType == ValueKind.Integer && service.Payload is CheckSavePayload && result.Data is CheckSaveData || service.Payload is GraphicsPayload && result.Data is GraphicsData graphicsData && graphicsData.Value.Kind == expectedType
                || service.Payload is HtmlResourcePayload { SplitLength: not null } slice && result.Data is HtmlResourceData && expectedType == (slice.CountLines ? ValueKind.Integer : ValueKind.String)
                || service.Payload is HtmlResourcePayload { MeasureLength: true } && result.Data is HtmlResourceData && expectedType == ValueKind.Integer
                || service.Payload is MediaInfoPayload && result.Data is MediaInfoData && expectedType == ValueKind.Integer
                : kind switch { ServiceKind.Compile => result.Data is CompiledData, ServiceKind.Save => result.Data is SavedData { Committed: true }, ServiceKind.Load => result.Data is LoadedData || global && result.Data is MissingSaveData, ServiceKind.Media => service.Payload is MediaPayload && result.Data is MediaCompletedData, ServiceKind.Resource => service.Payload is HtmlResourcePayload && result.Data is HtmlResourceData || service.Payload is GraphicsPayload && result.Data is GraphicsData, ServiceKind.Extension => service.Payload is PluginPayload && result.Data is PluginData, _ => false };
            if (!matches) return Reject("服务结果类型错误");
            try
            {
                if (result.Data is PluginData pluginData && service.Payload is PluginPayload plugin)
                {
                    if (pluginData.Values.Length != plugin.Arguments.Length) throw new ScriptException("PLUGIN_RESULT", "插件返回参数数量不匹配");
                    for (int i = 0; i < plugin.Arguments.Length; i++)
                    {
                        if (pluginData.Values[i].Kind != plugin.Arguments[i].Value.Kind) throw new ScriptException("PLUGIN_RESULT", "插件返回参数类型不匹配");
                        if (plugin.Arguments[i].Reference is { } reference)
                        {
                            _ = Read(reference);
                            if (CellFor(reference).Descriptor?.ReadOnly == true) throw new ScriptException("READ_ONLY", "插件不能写入常量");
                        }
                    }
                    for (int i = 0; i < plugin.Arguments.Length; i++)
                        if (plugin.Arguments[i].Reference is { } reference) Write(reference, pluginData.Values[i]);
                }
                if (result.Data is MediaInfoData mediaInfo && service.Payload is MediaInfoPayload mediaRequest)
                {
                    long[] values = [mediaInfo.Duration, mediaInfo.Position, mediaInfo.Playing ? 1 : 0, mediaInfo.Volume, mediaInfo.Speed];
                    if (mediaInfo.Valid && mediaRequest.Field is null)
                        for (int i = 0; i < Math.Min(values.Length, globals["RESULT"].Values.Length); i++) globals["RESULT"].Values[i] = new(values[i]);
                    int field = mediaRequest.Field ?? 1;
                    Put(new(mediaInfo.Valid && field is >= 1 and <= 5 ? values[field - 1] : 0L)); pendingBuiltinType = null;
                }
                if (result.Data is CharacterFileData characterData && service.Payload is CharacterFilePayload characterFile)
                    ApplyCharacterFile(characterFile, characterData);
                if (result.Data is BuiltinValueData returned) { Put(returned.Value); pendingBuiltinType = null; }
                if (result.Data is HtmlResourceData htmlResult && service.Payload is HtmlResourcePayload html)
                {
                    if (html.MeasureLength) { Put(new(MeasureHtmlLength(html.Text, html.Pixels, htmlResult.Images))); pendingBuiltinType = null; }
                    else if (html.SplitLength is { } length) { Put(SplitHtmlResult(html.Text, length, html.CountLines, htmlResult.Images, html.RemainderOnly)); pendingBuiltinType = null; }
                    else if (html.ColumnWidth is { } width) document.HtmlColumn(html.Text, html.AlignRight, width, viewportColumns, Metrics, htmlResult.Images);
                    else document.Html(html.Text, html.ToBuffer, htmlResult.Images, Metrics.CellWidth);
                }
                if (result.Data is DynamicVariableData dynamicResult)
                {
                    if (frames.Count >= 4096) throw new ScriptException("STACK_LIMIT", "调用深度超过限制");
                    var caller = frames[^1];
                    if (dynamicResult.Function.Descriptor.Id != caller.Descriptor.Id) return Reject("动态变量作用域不匹配");
                    var dynamicFrame = new Frame(caller.Descriptor, ++nextFrame, 1, stack.Count)
                    { Locals = new(caller.Locals), Aliases = caller.Aliases, Entered = true, OwnsLocals = false };
                    Initialize(dynamicFrame, dynamicResult.Function); frames.Add(dynamicFrame);
                    pendingBuiltinType = null;
                }
                if (result.Data is GraphicsData graphicsResult)
                {
                    if (!graphicsResult.ResultTail.IsDefault)
                        for (int i = 0; i < graphicsResult.ResultTail.Length && i + 1 < globals["RESULT"].Values.Length; i++)
                            globals["RESULT"].Values[i + 1] = new(graphicsResult.ResultTail[i]);
                    if (pendingBuiltinType is not null) Put(graphicsResult.Value);
                    pendingBuiltinType = null;
                    if (graphicsResult.ImageLayer is { } layer)
                    {
                        if (layer.FollowScroll) layer = layer with { InitialScrollRows = Math.Max(0, document.Snapshot().Lines.Sum(l => PhysicalLineLayout.Wrap(l, viewportColumns, Metrics).Length) - viewportRows) };
                        document.AddImageLayer(layer);
                    }
                    if (!graphicsResult.Background.IsDefault) document.BackgroundLayers(graphicsResult.Background);
                }
                if (result.Data is CheckSaveData metadata)
                {
                    Put(new((long)metadata.Status)); globals["RESULTS"].Values[0] = new(metadata.Description);
                    if (globals["RESULT"].Values.Length > 1) globals["RESULT"].Values[1] = new(metadata.Version);
                    pendingBuiltinType = null;
                }
                if (result.Data is CompiledData compiledData)
                {
                    var payload = (CompilePayload)service.Payload;
                    if (compiledData.Function.Descriptor.Id != payload.FunctionId) return Reject("编译结果函数不匹配");
                    var node = compiled.TryGetValue(payload.FunctionId, out var cached) ? cached.Node : compiledOrder.AddLast(payload.FunctionId);
                    compiled[payload.FunctionId] = (compiledData.Function, node);
                    while (compiled.Count > Math.Max(1, game.Index.Options.CacheCapacity))
                    {
                        string oldest = compiledOrder.First!.Value;
                        compiledOrder.RemoveFirst(); compiled.Remove(oldest);
                    }
                    Initialize(frames[^1], compiledData.Function);
                }
                if (result.Data is LoadedData loaded) Restore(loaded.Snapshot, global);
                if (service?.Payload is LoadPayload { Scope: SaveScope.Global }) globals["RESULT"].Values[0] = new(result.Data is MissingSaveData ? 0L : 1L);
                if (result.Data is not LoadedData || global) { service = null; state = StopReason.Yielded; if (savedInput is not null) { input = savedInput; savedInput = null; state = StopReason.WaitingInput; } }
                Log(TraceKind.ServiceCompleted, kind.ToString()); return new(true, "服务完成");
            }
            catch (ScriptException e)
            {
                if (ContinueScriptSaveFailure(e.Diagnostic)) return new(true, e.Diagnostic.Code);
                if (savedInput is not null) { diagnostics.Add(e.Diagnostic); input = savedInput; savedInput = null; service = null; state = StopReason.WaitingInput; }
                else Fault(e.Diagnostic);
                return new(false, e.Diagnostic.Code);
            }
        }
        return Reject("非法消息状态");
    }
    private ApplyResult Reject(string reason) { Log(TraceKind.InputRejected, reason); return new(false, reason); }
    private void Accept(EraValue value, string source = "timeout")
    {
        if (input!.Kind != InputKind.Wait) globals[input.Kind == InputKind.String ? "RESULTS" : "RESULT"].Values[0] = value;
        lastAcceptedInputId = input.Id; lastAcceptedInputEpoch = input.Epoch;
        Log(TraceKind.InputAccepted, input.Kind.ToString(), source, input.Id); input = null; state = StopReason.Yielded;
    }
    private void AcceptTimeout()
    {
        if (input!.TimeoutText.Length > 0) document.Text(input.TimeoutText, true);
        Accept(input.Default!.Value);
    }
    public SessionSnapshot Inspect() => SnapshotForUi(true);
    public SessionSnapshot InspectPresentation() => SnapshotForUi(false);
    public SessionSnapshot InspectSelected(IEnumerable<StateSelection> selection)
    {
        var selected = selection.Distinct().ToArray();
        var values = ImmutableDictionary.CreateBuilder<string, ImmutableArray<EraValue>>();
        var shapes = ImmutableDictionary.CreateBuilder<string, ImmutableArray<int>>();
        var characterValues = new Dictionary<int, List<SavedVariable>>();
        foreach (var item in selected)
        {
            var spec = game.Index.Variables.FirstOrDefault(v => v.Name == item.Variable)
                ?? throw new ScriptException("INSPECT_VARIABLE", "变量不存在: " + item.Variable);
            shapes[item.Variable] = spec.Shape;
            if (item.Character is { } index)
            {
                var data = characters.Values(characters.IdAt(index), item.Variable).ToImmutableArray();
                if (!characterValues.TryGetValue(index, out var list)) characterValues[index] = list = [];
                list.Add(new(item.Variable, spec.Kind, data.Length, data) { Shape = spec.Shape });
            }
            else if (globals.TryGetValue(item.Variable, out var cell)) values[item.Variable] = cell.Values.ToImmutableArray();
            else throw new ScriptException("INSPECT_VARIABLE", "需指定角色或不支持局部变量: " + item.Variable);
        }
        return InspectPresentation() with
        {
            Variables = values.ToImmutable(), Shapes = shapes.ToImmutable(),
            Characters = characters.SnapshotIdentity().Select((c, i) => c with
                { Variables = characterValues.TryGetValue(i, out var list) ? list.ToImmutableArray() : [] }).ToImmutableArray()
        };
    }
    private bool CanSaveLoad => state == StopReason.WaitingInput && game.SystemFlow?.CanSaveLoad(Mode) != false;
    private SessionSnapshot SnapshotForUi(bool variables) => new(epoch, state, document.Snapshot(), input,
        variables ? globals.ToImmutableDictionary(p => p.Key, p => p.Value.Values.ToImmutableArray()) : ImmutableDictionary<string, ImmutableArray<EraValue>>.Empty, diagnostics.ToImmutableArray(), instructions,
        CanSaveLoad, CanSaveLoad,
        CanSaveLoad ? null : "仅允许保存的系统流程稳定等待输入时可存读档", Trace)
        {
            Mode = Mode, Characters = variables ? characters.Snapshot() : [],
            TextBox = textBoxText, TextBoxRevision = textBoxRevision,
            LastAcceptedInputId = lastAcceptedInputId, LastAcceptedInputEpoch = lastAcceptedInputEpoch,
            Shapes = variables ? game.Index.Variables.ToImmutableDictionary(v => v.Name, v => v.Shape) : ImmutableDictionary<string, ImmutableArray<int>>.Empty
        };

    void IBuiltinContext.Emit(string text, bool newline, bool html) { if (html) document.Html(text); else document.Text(text, newline, numbered: true); }
    void IBuiltinContext.EmitPlain(string text) => document.Plain(text);
    void IBuiltinContext.EmitDefault(string text, bool newline)
    { int? color = document.Color; document.Color = null; document.Text(text, newline, numbered: true); document.Color = color; }
    void IBuiltinContext.EmitTemporary(string text) => document.Temporary(text);
    private int kanaMode;
    bool IBuiltinContext.CachePrintedLines { get => document.CachePrintedLines; set => document.CachePrintedLines = value; }
    void IBuiltinContext.ClearImageLayers(long? depth) => document.ClearImageLayers(depth);
    bool IBuiltinContext.HasImageLayer(long depth) => document.HasImageLayer(depth);
    private string textBoxText = "";
    private long textBoxRevision;
    string IBuiltinContext.TextBox { get => textBoxText; set { textBoxText = value; textBoxRevision++; } }
    int IBuiltinContext.KanaMode { get => kanaMode; set => kanaMode = value; }
    string IBuiltinContext.ErdName(string name, long index, long? dimension)
    {
        bool local = frames.Count > 0 && (frames[^1].Locals.ContainsKey(name) || frames[^1].Aliases.ContainsKey(name));
        if (!local && game.Index.Options.SystemVariables.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return "";
        string key = dimension is { } number ? name + "@" + number : name;
        return UserNameIndexWithinDeclaration(name, dimension, index) && game.Index.Options.UserNameTables.TryGetValue(key, out var names)
            ? names.FirstOrDefault(pair => pair.Value == index).Key ?? "" : "";
    }
    void IBuiltinContext.EmitSingle(string text) => document.Single(text);
    void IBuiltinContext.EmitColumn(string text, bool right, bool defaultStyle)
    {
        int? color = document.Color;
        if (defaultStyle) document.Color = null;
        document.Column(text, right, game.Index.Options.PrintColumnWidth, viewportColumns, Metrics);
        document.Color = color;
    }
    int IBuiltinContext.PrintColumnWidth => game.Index.Options.PrintColumnWidth;
    int IBuiltinContext.PrintColumnsPerLine => game.Index.Options.PrintColumnsPerLine;
    void IBuiltinContext.Align(TextAlignment alignment) => document.Alignment = alignment;
    TextAlignment IBuiltinContext.CurrentAlignment => document.Alignment;
    bool IBuiltinContext.RedrawEnabled => document.RedrawEnabled;
    string IBuiltinContext.HoveredButton
    {
        get
        {
            var current = document.Snapshot();
            if (pointerQuery?.Invoke(current, epoch) is { } pointer) Apply(pointer);
            return hoveredEpoch == epoch && hoveredRevision == current.Revision ? hoveredButton : "";
        }
    }
    void IBuiltinContext.Redraw(long flags)
    {
        document.Redraw((flags & 1) != 0);
        if ((flags & 2) != 0) document.Refresh();
    }
    EraValue IBuiltinContext.Read(SlotReference reference) => Read(reference);
    EraValue IBuiltinContext.ReadGlobal(string name, int index) => Read(new(name, index, 0, epoch));
    void IBuiltinContext.Write(SlotReference reference, EraValue value) => Write(reference, value);
    void IBuiltinContext.Button(string text, EraValue value) => document.Button(text.Replace("\n", "", StringComparison.Ordinal), value);
    void IBuiltinContext.ButtonColumn(string text, EraValue value, bool right) => document.Column(text.Replace("\n", "", StringComparison.Ordinal), right, game.Index.Options.PrintColumnWidth, viewportColumns, Metrics, value);
    string IBuiltinContext.CurrentFunction => frames.LastOrDefault(f => f.Function is not null)?.Descriptor.Name ?? "";
    void IBuiltinContext.Input(InputKind kind, long? timeout, EraValue? defaultValue)
    {
        if (timeout < 0) throw new ScriptException("TIMEOUT", "期限不能为负");
        document.Refresh();
        input = new(epoch, ++nextRequest, kind, document.Buttons(game.Index.Options.Profile == "minimal-v1" ? null : kind, currentSpan, rawStrings: kind == InputKind.String), defaultValue, timeout is { } duration ? checked(clock() + duration) : null);
        input = input with { SequenceInputAllowed = kind != InputKind.Wait };
        state = StopReason.WaitingInput; Log(TraceKind.InputRequested, kind.ToString(), request: input.Id);
    }
    void IBuiltinContext.Clear(long count) => document.Clear(count);
    void IBuiltinContext.TimedWait(long? timeout, bool ignoreInput)
    {
        ((IBuiltinContext)this).Input(InputKind.Wait, timeout, new EraValue(0L));
        input = input! with { IgnoreInput = ignoreInput, NoFocus = ignoreInput, SequenceInputAllowed = true };
    }
    void IBuiltinContext.InputMode(bool singleCharacter, bool buttonsOnly, bool anyKey)
    {
        input = input! with { SingleCharacter = singleCharacter, ButtonsOnly = buttonsOnly, AnyKey = anyKey };
        if (buttonsOnly && input.Buttons.Count == 0)
        {
            if (input.Default is not { } fallback) throw new ScriptException("INPUT_BUTTONS", "按钮输入没有可选按钮或默认值");
            Accept(fallback, "default");
        }
    }
    int IBuiltinContext.LineCount => document.LineCount;
    void IBuiltinContext.Color(int? color) => document.Color = color;
    int IBuiltinContext.ForegroundColor => document.Color ?? 0xc0c0c0;
    int IBuiltinContext.DefaultForegroundColor => game.Index.Options.DefaultForeground;
    int IBuiltinContext.FocusColor => game.Index.Options.FocusColor;
    private int mouseX = -1, mouseY = -1;
    int IBuiltinContext.MouseX => mouseX;
    int IBuiltinContext.MouseY => mouseY;
    int IBuiltinContext.DefaultBackgroundColor => game.Index.Options.DefaultBackground;
    string IBuiltinContext.MoneyLabel => game.Index.Options.MoneyLabel;
    bool IBuiltinContext.MoneyFirst => game.Index.Options.MoneyFirst;
    int IBuiltinContext.CurrentBackgroundColor => document.CurrentBackground;
    int IBuiltinContext.TextStyle { get => document.Style; set => document.Style = value; }
    string IBuiltinContext.FontName { get => document.Font ?? game.Index.Options.DefaultFont; set => document.Font = string.IsNullOrEmpty(value) ? null : value; }
    void IBuiltinContext.BackgroundColor(int? color) => document.Background(color);
    void IBuiltinContext.TextBackground(uint? color) => document.TextBackground = color;
    void IBuiltinContext.Html(string text, bool toBuffer)
    {
        if (text.Contains("<img", StringComparison.OrdinalIgnoreCase)) Request(ServiceKind.Resource, new HtmlResourcePayload(text, toBuffer));
        else document.Html(text, toBuffer, cellWidth: Metrics.CellWidth);
    }
    void IBuiltinContext.HtmlColumn(string text, long width, bool right)
    {
        if (text.Length == 0) return;
        if (width <= 0) width = (long)game.Index.Options.PrintColumnWidth * Metrics.CellWidth;
        if (width > 1_000_000) throw new ScriptException("HTML_COLUMN", "HTML 列宽超限");
        if (text.Contains("<img", StringComparison.OrdinalIgnoreCase)) Request(ServiceKind.Resource, new HtmlResourcePayload(text, true, (int)width, right));
        else document.HtmlColumn(text, right, (int)width, viewportColumns, Metrics);
    }
    private ITextMetrics Metrics => metrics ?? throw new ScriptException("DISPLAY_METRICS", "宿主未提供文本测量");
    ImmutableArray<long> IBuiltinContext.ReadMatrix(SlotReference reference)
    {
        var cell = CellFor(reference); var shape = cell.Descriptor?.Shape ?? [];
        if (shape.Length < 2 || cell.Kind != ValueKind.Integer) throw new ScriptException("GRAPHICS_MATRIX", "颜色矩阵需要二维或三维整数数组");
        int width = shape[^1], row = reference.Index / width % shape[^2], column = reference.Index % width;
        if (row + 5 > shape[^2] || column + 5 > width) throw new ScriptException("GRAPHICS_MATRIX", "颜色矩阵区域越界");
        var result = ImmutableArray.CreateBuilder<long>(25);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++) result.Add(cell.Values[reference.Index + y * width + x].AsInt());
        return result.ToImmutable();
    }
    int IBuiltinContext.ClientWidth => checked(viewportColumns * Metrics.CellWidth);
    int IBuiltinContext.ClientHeight => checked(viewportRows * Metrics.CellWidth * 2);
    int IBuiltinContext.FontSize => checked(Metrics.CellWidth * 2);
    int IBuiltinContext.FramesPerSecond => game.Index.Options.FramesPerSecond;
    EraValue IBuiltinContext.ConfigValue(string key) => game.Index.Options.ConfigValues.TryGetValue(key, out var value)
        ? value : throw new ScriptException("CONFIG_UNSUPPORTED", "配置项不可用: " + key);
    string IBuiltinContext.PopPrintingHtml() => document.PopPrintingHtml(viewportColumns, Metrics);
    string IBuiltinContext.PrintedHtml(long line) => document.PrintedHtml(line, viewportColumns, Metrics);
    EraValue? IBuiltinContext.SplitHtml(string text, long length, bool countLines)
    {
        int width = unchecked((int)length);
        if (text.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            Request(ServiceKind.Resource, new HtmlResourcePayload(text, true) { SplitLength = width, CountLines = countLines }); return null;
        }
        return SplitHtmlResult(text, width, countLines, null);
    }
    EraValue? IBuiltinContext.HtmlLineRemainder(string text, long length)
    {
        int width = unchecked((int)length);
        if (text.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            Request(ServiceKind.Resource, new HtmlResourcePayload(text, true) { SplitLength = width, RemainderOnly = true }); return null;
        }
        return SplitHtmlResult(text, width, false, null, true);
    }
    private EraValue SplitHtmlResult(string text, int width, bool countLines, IReadOnlyDictionary<string, RasterImage>? images, bool remainderOnly = false)
    {
        if (remainderOnly) return new(DocumentBuilder.SplitHtml(text, width, Metrics, images).Remainder);
        if (!countLines)
        {
            var (first, rest) = DocumentBuilder.SplitHtml(text, width, Metrics, images);
            if (globals["RESULTS"].Values.Length > 0) globals["RESULTS"].Values[0] = new(first);
            if (globals["RESULTS"].Values.Length > 1) globals["RESULTS"].Values[1] = new(rest);
            return new(first);
        }
        long count = 0;
        while (text.Length > 0)
        {
            var (_, rest) = DocumentBuilder.SplitHtml(text, width, Metrics, images);
            if (rest == text || count >= 1_000_000) throw new ScriptException("HTML_WIDTH", "HTML 无法在指定宽度内继续截取");
            text = rest; count++;
        }
        return new(count);
    }
    string IBuiltinContext.DisplayLine(long index) => document.DisplayLine(index);
    void IBuiltinContext.SetStringResult(string value, int index) => globals["RESULTS"].Values[index] = new(value);
    void IBuiltinContext.SetAnimationTimer(long interval) => document.SetAnimationTimer(interval);
    int IBuiltinContext.AnimationInterval => document.AnimationInterval;
    bool IBuiltinContext.LineIsEmpty => document.IsPrintingBufferEmpty;
    EraValue? IBuiltinContext.HtmlLength(string text, bool pixels)
    {
        if (text.Contains("<img", StringComparison.OrdinalIgnoreCase))
        {
            Request(ServiceKind.Resource, new HtmlResourcePayload(text, true) { MeasureLength = true, Pixels = pixels }); return null;
        }
        return new EraValue(MeasureHtmlLength(text, pixels));
    }
    private long MeasureHtmlLength(string text, bool pixels, IReadOnlyDictionary<string, RasterImage>? images = null)
    {
        long width = DocumentBuilder.MeasureHtml(text, Metrics, images, viewportColumns);
        return pixels ? width : (long)Math.Ceiling((double)width / Metrics.CellWidth);
    }
    void IBuiltinContext.DrawLine(string pattern)
        => document.DrawLine(LineText(pattern));
    string IBuiltinContext.DefaultLine => LineText(game.Index.Options.DrawLinePattern);
    private string LineText(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) throw new ScriptException("DRAWLINE", "分隔线不能为空");
        var result = new System.Text.StringBuilder();
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(pattern);
        var pieces = new List<string>();
        while (elements.MoveNext()) pieces.Add(elements.GetTextElement());
        int columns = 0, at = 0;
        while (columns < viewportColumns)
        {
            string piece = pieces[at++ % pieces.Count]; int width = Metrics.Columns(piece);
            if (width <= 0) throw new ScriptException("DRAWLINE", "分隔线字符宽度无效");
            if (columns + width > viewportColumns) break;
            result.Append(piece); columns += width;
        }
        return result.ToString();
    }
    void IBuiltinContext.TimedInput(InputKind kind, long timeout, EraValue value, bool displayTime, string timeoutText, bool noFocus)
    {
        ((IBuiltinContext)this).Input(kind, timeout, value);
        input = input! with { DisplayTime = displayTime, TimeoutText = timeoutText, NoFocus = noFocus };
    }
    void IBuiltinContext.InputOptions(bool noFocus, bool displayTime, string timeoutText, bool mouseInput)
        => input = input! with { NoFocus = noFocus, DisplayTime = displayTime, TimeoutText = timeoutText, MouseInput = mouseInput };
    string IBuiltinContext.TimeupLabel => game.Index.Options.TimeupLabel;
    void IBuiltinContext.Save(int slot, string description)
    {
        if (description.Contains('\n')) throw new ScriptException("SAVE_MESSAGE", "存档说明不能包含换行");
        Request(ServiceKind.Save, new SavePayload(slot, Snapshot() with { Description = description }));
    }
    void IBuiltinContext.Load(int slot) => Request(ServiceKind.Load, new LoadPayload(slot));
    void IBuiltinContext.GlobalData(bool load) => Request(load ? ServiceKind.Load : ServiceKind.Save,
        load ? new LoadPayload(0, SaveScope.Global) : new SavePayload(0, Snapshot(true), SaveScope.Global));
    void IBuiltinContext.Defer(ServiceKind kind, ServicePayload payload)
    {
        if (payload is GraphicsPayload { Operation: "SETIMAGELAYERL" } graphics) payload = graphics with { AnchorLine = document.PrintingLineIndex };
        Request(kind, payload);
    }
    void IBuiltinContext.CheckSave(int slot) => Request(ServiceKind.Load, new CheckSavePayload(slot, game.Profile) { Compatibility = SaveCompatibilityPolicy });
    void IBuiltinContext.Restart() => Restart();
    void IBuiltinContext.Begin(SystemMode mode) => Begin(mode);
    EraValue IFlowState.Get(string name, int index) => globals[name].Values[index];
    long IFlowState.InputRequests => nextRequest;
    bool IFlowState.LastLineTemporary => document.LastLineTemporary;
    void IFlowState.Set(string name, EraValue value, int index) => Write(new(name, index, 0, epoch), value);
    void IBuiltinContext.Quit() { ClearPendingThrow(); state = StopReason.Completed; input = null; }
    long IBuiltinContext.CharacterCount => characters.Count;
    void IBuiltinContext.PickCharacters(IReadOnlyList<long> indices)
    {
        var picks = indices.Where(i => i >= 0).Distinct().ToList();
        var selectors = new[] { "MASTER", "TARGET", "ASSI" }.Where(globals.ContainsKey).ToDictionary(n => n, n => globals[n].Values[0].AsInt());
        foreach (string name in selectors.Keys) globals[name].Values[0] = new(-1L);
        for (int i = 0; i < picks.Count; i++)
        {
            if (i != picks[i])
            {
                characters.Swap(picks[i], i);
                int later = picks.IndexOf(i);
                if (later > i) picks[later] = picks[i];
            }
            foreach (var (name, before) in selectors)
                if (globals[name].Values[0].Integer < 0 && picks[i] == before) globals[name].Values[0] = new((long)i);
        }
        for (int i = characters.Count - 1; i >= picks.Count; i--) characters.Delete(i);
    }
    void IBuiltinContext.Characters(CharacterOperation operation, long first, long second)
    {
        switch (operation)
        {
            case CharacterOperation.Add: characters.Add(first); break;
            case CharacterOperation.Delete: characters.Delete(first); break;
            case CharacterOperation.Copy: characters.Copy(first, second); break;
            case CharacterOperation.AddCopy: characters.AddCopy(first); break;
            case CharacterOperation.Swap: characters.Swap(first, second); break;
            case CharacterOperation.Clear: characters.Clear(); break;
        }
    }
    void IBuiltinContext.ResetData()
    {
        var names = ExtensionNames(false);
        extensions?.PrepareRestoreXml(ImmutableDictionary<string, string>.Empty, names.XmlDocuments).Invoke();
        extensions?.ClearTableRows(names.Tables);
        foreach (string name in names.Maps)
            if (stringMaps.TryGetValue(name, out var map)) map.Clear();
        ResetOrdinaryVariables();
        characters.Clear();
        document.Color = null; document.Style = 0; document.Font = null; document.Alignment = TextAlignment.Left;
    }
    private void ResetOrdinaryVariables()
    {
        foreach (var variable in game.Index.Variables.Where(v => globals.ContainsKey(v.Name) && !v.GlobalSaved)) globals[variable.Name] = CreateCell(variable);
        foreach (var cells in statics.Values)
            foreach (var cell in cells.Values)
                if (cell.Descriptor is { } spec) CreateCell(spec).Values.CopyTo(cell.Values, 0);
    }
    void IBuiltinContext.ResetStain(long character)
    {
        var values = characters.Values(characters.IdAt(character), "STAIN");
        var defaults = game.Index.Options.DefaultStain;
        for (int i = 0; i < values.Length; i++) values[i] = new EraValue(i < defaults.Length ? defaults[i] : 0L);
    }
    void IBuiltinContext.Fill(SlotReference reference, EraValue value, long start, long end)
    {
        var cell = CellFor(reference);
        if (cell.Descriptor?.ReadOnly == true) throw new ScriptException("READ_ONLY", "常量不可写入");
        if (cell.Descriptor?.Shape.Length > 1)
        { Array.Fill(cell.Values, value.ConvertTo(cell.Kind)); return; }
        if (end == -1) end = cell.Values.Length;
        if (start > end) (start, end) = (end, start);
        if (start < 0 || end > cell.Values.Length) throw new ScriptException("BOUNDS", "数组填充范围无效");
        Array.Fill(cell.Values, value.ConvertTo(cell.Kind), (int)start, (int)(end - start));
    }
    EraValue IBuiltinContext.ReadCharacter(string name, IReadOnlyList<long> indices, long character)
    {
        var spec = game.Index.VariablesByName.GetValueOrDefault(name) ?? throw new ScriptException("VARIABLE", "角色变量不存在");
        if (spec.Scope != VariableScope.Character) throw new ScriptException("REFERENCE", "需要角色变量");
        return characters.Values(characters.IdAt(character), name)[spec.Offset(indices)];
    }
    void IBuiltinContext.FillCharacters(string name, int index, EraValue value, int start, int end)
    {
        var spec = game.Index.VariablesByName.GetValueOrDefault(name) ?? throw new ScriptException("VARIABLE", "角色变量不存在");
        if (spec.Scope != VariableScope.Character || spec.ReadOnly) throw new ScriptException("REFERENCE", "需要可写角色变量");
        if (index < 0 || index >= spec.Length || start < 0 || start > end || end > characters.Count) throw new ScriptException("BOUNDS", "角色批量赋值范围无效");
        value = value.ConvertTo(spec.Kind);
        for (int i = start; i < end; i++) characters.Values(characters.IdAt(i), name)[index] = value;
    }
    long IBuiltinContext.Random(long exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0) throw new ScriptException("RANDOM_RANGE", "随机上限必须为正数");
        // SplitMix64 状态随新存档保存；不承诺旧引擎随机序列相同。
        randomState = unchecked(randomState + 0x9E3779B97F4A7C15UL);
        ulong value = randomState;
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        return (long)((value ^ (value >> 31)) % (ulong)exclusiveMaximum);
    }
    void IBuiltinContext.SeedRandom(long seed) => randomState = unchecked((ulong)seed);
    void IBuiltinContext.RandomData(bool restore)
    {
        const long marker = 0x534D3634;
        if (!globals.TryGetValue("RANDDATA", out var cell) || cell.Kind != ValueKind.Integer || cell.Values.Length < 2)
            throw new ScriptException("RANDOM_STATE", "RANDDATA 需要至少两个整数元素");
        if (restore)
        {
            if (cell.Values[1].AsInt() != marker || cell.Values.Skip(2).Any(v => v.AsInt() != 0))
                throw new ScriptException("RANDOM_STATE", "RANDDATA 不是 mEmuera SplitMix64 状态");
            randomState = unchecked((ulong)cell.Values[0].AsInt());
        }
        else
        {
            Array.Fill(cell.Values, new EraValue(0L));
            cell.Values[0] = new EraValue(unchecked((long)randomState));
            cell.Values[1] = new EraValue(marker);
        }
    }
}
