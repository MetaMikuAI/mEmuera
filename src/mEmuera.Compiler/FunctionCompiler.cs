using System.Collections.Immutable;
using mEmuera.Compiler.Indexing;
using mEmuera.Compiler.Lexing;
using mEmuera.Compiler.Syntax;
using mEmuera.Compiler.Preprocessing;
using mEmuera.Contracts;

namespace mEmuera.Compiler;

internal sealed partial class FunctionCompiler(ProgramIndex index, FunctionDescriptor descriptor, IBuiltinRegistry builtins)
{
    private readonly MacroEnvironment macros = new(index.Macros.Values);
    private readonly List<Instruction> code = [];
    private readonly List<VariableDescriptor> locals = descriptor.Locals.ToList();
    private readonly Dictionary<string, int> labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(int At, string Label)> gotos = [];
    private readonly Stack<Block> blocks = [];
    private SourceSpan span = descriptor.Span;
    private string lineText = "";
    private bool IgnoreTripleSymbols => index.Options.SystemIgnoreTripleSymbol
        || index.Options.ConfigValues.TryGetValue("SystemIgnoreTripleSymbol", out var value) && value.AsInt() != 0;
    private SourceSpan Location(string fragment)
    {
        int offset = Math.Max(0, lineText.IndexOf(fragment, StringComparison.Ordinal));
        return span with { Start = span.Start + offset, Column = span.Column + offset, Length = fragment.Length };
    }
    private Expr Parse(string text)
    {
        var location = Location(text);
        return ExpressionParser.Parse(Preprocessor.CharacterTokens(macros.Expand(text, location), index.Options, location), location);
    }
    private int? sif;
    private string? dataCommand;
    private string dataTarget = "";
    private readonly List<List<(string Text, bool Format)>> dataGroups = [];
    private List<(string Text, bool Format)>? dataList;
    private sealed class Block(string kind, int start)
    {
        public string Kind = kind;
        public int Start = start;
        public int False = -1;
        public List<int> Ends = [];
        public List<int> Continues = [];
        public string Variable = "";
        public Variable? Counter;
        public string Bound = "";
        public string Step = "";
        public Dictionary<string, int>? StringTargets;
        public Dictionary<long, int>? IntegerTargets;
        public int DefaultTarget = -1;
        public int CaseCount;
    }
    private bool TryCaseInteger(Expr expression, out long value)
    {
        if (expression is Literal { Value.Kind: ValueKind.Integer } literal)
        { value = literal.Value.Integer; return true; }
        if (expression is Variable { ExplicitIndices: false } variable
            && Resolve(variable.Name) is { ReadOnly: true, Reference: false, Kind: ValueKind.Integer, Length: 1 } specification
            && specification.Scope != VariableScope.Character && specification.Initial.Length == 1)
        { value = specification.Initial[0].Integer; return true; }
        value = 0; return false;
    }
    private int Emit(Op op, string text = "", int number = 0, EraValue value = default, VariableDescriptor? variable = null, int localSlot = -1)
    {
        string? name = op switch
        {
            Op.Emit => text == "HTML" ? "HTML_PRINT" : text == "D" ? number == 1 ? "PRINTDL" : "PRINTD" : number == 1 ? "PRINTL" : "PRINT",
            Op.Button => "PRINTBUTTON", Op.Input => text, Op.ClearLine => "CLEARLINE", Op.LineCount => "LINECOUNT",
            Op.Color => text == "RESET" ? "RESETCOLOR" : "SETCOLOR", Op.Save => "SAVEDATA", Op.LoadSave => "LOADDATA",
            Op.BeginTitle => "BEGIN_TITLE", Op.Quit => "QUIT", _ => null
        };
        if (name is not null) return Builtin(name);
        code.Add(new(op, text, number, value, span) { ResolvedVariable = variable, LocalSlot = localSlot }); return code.Count - 1;
    }
    private int Builtin(string name, int? argumentCount = null)
    {
        var signature = builtins.Find(name) ?? throw new ScriptException("BUILTIN_MISSING", "缺少内建签名: " + name, span);
        code.Add(new(Op.Builtin, signature.ImplementationId, argumentCount ?? signature.Parameters.Length, new(signature.ReturnType is null ? 0L : 1L), span));
        return code.Count - 1;
    }
    private void Patch(int at, int target) => code[at] = code[at] with { Number = target };
    public CompileResult CompileVariable(DynamicVariablePayload payload)
    {
        span = payload.Span ?? descriptor.Span;
        if (payload.CallString)
        {
            string rawCall = payload.Expression.Trim(); string call = LogicalLines.StripComment(rawCall);
            if (rawCall.Length > 0)
            {
                string tail = LogicalLines.Head(call).Tail;
                if (tail.StartsWith(',')) tail = tail[1..].Trim();
                _ = new Tokens(Preprocessor.CharacterTokens(macros.Expand(tail, span), index.Options, span), span);
            }
            try
            {
                if (rawCall.Length == 0) { Emit(Op.Constant, value: new EraValue(1L)); }
                else
                {
                    StatementCore((payload.TryCall ? "TRYCCALL " : "CALL ") + call);
                    Emit(Op.Constant, value: new EraValue(1L)); Emit(Op.Return, "RETURNF", 1);
                    if (payload.TryCall)
                    {
                        StatementCore("CATCH"); Emit(Op.Constant, value: new EraValue(0L)); Emit(Op.Return, "RETURNF", 1);
                        StatementCore("ENDCATCH");
                    }
                    Emit(Op.Constant, value: new EraValue(0L));
                }
            }
            catch (ScriptException) when (payload.TryCall)
            {
                code.Clear(); blocks.Clear(); Emit(Op.Constant, value: new EraValue(0L));
            }
            Emit(Op.Return, "RETURNF", 1);
            var instructions = code.ToImmutableArray(); var diagnostics = BytecodeValidator.Validate(instructions);
            return diagnostics.IsEmpty ? new(new(descriptor with { Locals = locals.ToImmutableArray() }, instructions), []) : new(null, diagnostics);
        }
        if (payload.Fill is { } fill)
        {
            if (Parse(fill.Target) is not Variable variable) throw new ScriptException("REFERENCE", "动态填充需要变量", span);
            var specification = Resolve(variable.Name);
            if (variable.ExplicitIndices)
            {
                int rank = specification.ReferenceRank ?? specification.Shape.Length, count = variable.Indices.Count;
                bool targetRankValid = specification.Scope == VariableScope.Character
                    ? specification.CharacterScalar ? count == 1 : rank == 1 ? count is 1 or 2 : count == rank + 1
                    : count == rank;
                if (!targetRankValid) throw new ScriptException("RANK", "动态填充目标的下标维数不匹配", span);
            }
            if (specification.ReadOnly || specification.Scope == VariableScope.Character && specification.Shape.Length != 1)
                throw new ScriptException("REFERENCE", "动态填充不接受常量或多维角色数组", span);
            Expr fillValue = fill.ValueExpression is { } fillExpression ? Parse(fillExpression) : new Literal(fill.Value);
            bool valueValid = Kind(fillValue) == specification.Kind || specification.Kind == ValueKind.Float && Kind(fillValue) == ValueKind.Integer;
            string startSlot = Temporary(), endSlot = Temporary(), allSlot = Temporary(), valueSlot = Temporary(specification.Kind);
            Assign(Var(startSlot), fill.StartExpression is { } startExpression ? Parse(startExpression) : new Literal(new EraValue(fill.Start)));
            if (specification.Reference && fill.EndExpression is null && fill.End is null)
            { ArrayArgumentReference(Var(variable.Name)); Emit(Op.Pop); }
            Assign(Var(endSlot), fill.EndExpression is { } endExpression ? Parse(endExpression) : new Literal(new EraValue(fill.End ?? -1L)));
            Assign(Var(allSlot), fill.AllExpression is { } allExpression ? Parse(allExpression) : new Literal(new EraValue(fill.AllDimensions ? 1L : 0L)));
            Emit(Op.Constant, value: new EraValue(valueValid ? 1L : 0L)); Emit(Op.Unary, "fill_arguments"); Emit(Op.Pop);
            Assign(Var(valueSlot), fillValue);
            if (specification.Scope == VariableScope.Character)
                ArrayArgumentReference(new Variable(variable.Name, new Literal(new EraValue(0L)), specification.CharacterScalar ? null : [new Literal(new EraValue(0L))], true));
            else ArrayArgumentReference(Var(variable.Name));
            Expression(Var(valueSlot)); Expression(Var(allSlot)); Expression(Var(startSlot));
            if (fill.EndExpression is null && fill.End is null) Emit(Op.Constant, text: "$omitted", value: new EraValue(0L)); else Expression(Var(endSlot));
            if (specification.Shape.Length > 1)
                for (int i = 0; i < specification.Shape.Length; i++)
                    Expression(i < variable.Indices.Count ? Subscript(variable, variable.Indices[i], i) : new Literal(new EraValue(0L)));
            Builtin("$VARSETEX_RAW", 5 + (specification.Shape.Length > 1 ? specification.Shape.Length : 0));
            Emit(Op.Return, "RETURNF", 1);
            return new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
        }
        if (payload.Sort is { } sort)
        {
            var arguments = new List<Expr> { new Literal(new EraValue(sort.Ascending ? 1L : 0L)), new Literal(new EraValue(sort.Length)), Parse(sort.Key) };
            arguments.AddRange(sort.Targets.Select(Parse));
            InvokeBuiltin("$ARRAYMSORTEX", arguments);
            Emit(Op.Return, "RETURNF", 1);
            return new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
        }
        if (payload.ValidateExpression)
        {
            bool expressionValid = true;
            try { Expression(Parse(payload.Expression)); }
            catch (ScriptException) { expressionValid = false; }
            code.Clear();
            Emit(Op.Constant, value: new EraValue(expressionValid ? 1L : 0L)); Emit(Op.Return, "RETURNF", 1);
            return new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
        }
        if (payload.Format)
        {
            Format(payload.Expression);
            Emit(Op.Return, "RETURNF", 1);
            return new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
        }
        if (payload.ValidateWrite || payload.WriteValue is not null)
        {
            var target = Parse(payload.Expression);
            bool writable = target is Variable variable && !Resolve(variable.Name).ReadOnly
                && (Kind(target) == payload.Kind || Kind(target) == ValueKind.Float && payload.Kind == ValueKind.Integer);
            if (!writable && payload.Fallback is null) throw new ScriptException("DYNAMIC_VARIABLE", "动态赋值需要可写且类型匹配的变量", span);
            if (writable && payload.WriteValue is { } value) Assign((Variable)target, new Literal(value));
            Emit(Op.Constant, value: new EraValue(writable ? 1L : 0L)); Emit(Op.Return, "RETURNF", 1);
            return new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
        }
        Expr? expression = null;
        try { expression = Parse(payload.Expression); }
        catch (ScriptException) when (payload.Fallback is not null) { }
        bool valid;
        try { valid = expression is Variable && Kind(expression) == payload.Kind; }
        catch (ScriptException) when (payload.Fallback is not null) { valid = false; }
        if (valid) Expression(expression!);
        else if (payload.Fallback is { } fallback) Emit(Op.Constant, value: fallback);
        else throw new ScriptException("DYNAMIC_VARIABLE", "动态表达式不是指定类型的变量", span);
        Emit(Op.Return, "RETURNF", 1);
        return new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
    }
    private VariableDescriptor Resolve(string name, SourceSpan? location = null)
    {
        var variable = locals.FirstOrDefault(v => v.Name == name) ?? index.VariablesByName.GetValueOrDefault(name);
        if (variable is not null) return variable;
        var parameter = descriptor.Parameters.FirstOrDefault(p => p.Name == name);
        if (parameter is not null) return new(name, name, parameter.Kind, Local: true, Reference: parameter.Reference);
        throw new ScriptException("UNDEFINED_VARIABLE", "未定义变量: " + name, location ?? span);
    }
    private string Temporary(ValueKind kind = ValueKind.Integer)
    { string name = "$" + locals.Count; locals.Add(new(name, name, kind, Local: true)); return name; }
    private static Variable Var(string name) => new(name, new Literal(new(0L)));
    private ValueKind Kind(Expr expression)
    {
        ValueKind RequireSame(Expr left, Expr right)
        {
            var kind = Kind(left);
            if (kind != ValueKind.String && Kind(right) != ValueKind.String && (kind == ValueKind.Float || Kind(right) == ValueKind.Float)) return ValueKind.Float;
            if (kind != Kind(right)) throw new ScriptException("TYPE", "表达式两侧类型不匹配", span);
            return kind;
        }
        switch (expression)
        {
            case Literal literal: return literal.Value.Kind;
            case FormattedLiteral: return ValueKind.String;
            case Variable { Name: "LINECOUNT" }: return ValueKind.Integer;
            case Variable { Name: "CHARANUM" }: return ValueKind.Integer;
            case Variable { Name: "RAND" } random:
                if (random.Indices.Count != 1 || Kind(random.Indices[0]) != ValueKind.Integer) throw new ScriptException("RANDOM_RANGE", "RAND 需要整数上限", span);
                return ValueKind.Integer;
            case Variable { Name: "DRAWLINESTR" }: return ValueKind.String;
            case Variable variable:
                if (variable.Indices.Select((i, n) => Subscript(variable, i, n)).Any(i => Kind(i) != ValueKind.Integer)) throw new ScriptException("TYPE", "数组下标需要整数", span);
                return Resolve(variable.Name, variable.Span).Kind;
            case Invocation invocation:
                if (builtins.Find(invocation.Name) is { } builtin)
                {
                    if (builtin.ReturnType is not { } result) throw new ScriptException("FUNCTION_TYPE", "内建指令不能作为表达式", span);
                    BindBuiltin(builtin, invocation.Arguments);
                    return builtin.NumericResult && invocation.Arguments.Any(a => Kind(a) == ValueKind.Float) ? ValueKind.Float : result;
                }
                var function = index.Find(invocation.Name);
                if (function is null) throw new ScriptException(KnownIdentifiers.Functions.Contains(invocation.Name) ? "UNSUPPORTED_FUNCTION" : "UNDEFINED_FUNCTION", "函数不可用: " + invocation.Name, span);
                return function.Attributes.Contains("#FUNCTIONS") ? ValueKind.String : ValueKind.Integer;
            case Mutation mutation:
                if (Kind(mutation.Variable) != ValueKind.Integer) throw new ScriptException("TYPE", "递增需要整数变量", span);
                return ValueKind.Integer;
            case Unary unary:
                if (Kind(unary.Operand) == ValueKind.Float && unary.Operator is "+" or "-") return ValueKind.Float;
                if (Kind(unary.Operand) != ValueKind.Integer) throw new ScriptException("TYPE", "一元运算需要整数", span);
                return ValueKind.Integer;
            case Conditional conditional:
                if (Kind(conditional.Test) != ValueKind.Integer) throw new ScriptException("TYPE", "条件需要整数", span);
                return RequireSame(conditional.Yes, conditional.No);
            case Binary binary:
                if (binary.Operator == "*" && (Kind(binary.Left), Kind(binary.Right)) is (ValueKind.String, ValueKind.Integer) or (ValueKind.Integer, ValueKind.String)) return ValueKind.String;
                var type = RequireSame(binary.Left, binary.Right);
                if (binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=") return ValueKind.Integer;
                if (type == ValueKind.Float && binary.Operator is not ("+" or "-" or "*" or "/" or "%")) throw new ScriptException("TYPE", "浮点运算符无效", span);
                if (binary.Operator != "+" && type == ValueKind.String) throw new ScriptException("TYPE", "此运算需要数值", span);
                return type;
            default: throw new ScriptException("TYPE", "表达式类型未知", span);
        }
    }
    private void Reference(Variable variable, bool read = false)
    {
        var specification = Resolve(variable.Name);
        if (read && !variable.ExplicitIndices
            && specification is { Scope: VariableScope.Global, ReadOnly: true, Reference: false, Saved: false, GlobalSaved: false, Length: 1 }
            && specification.Initial.Length == 1 && !descriptor.Parameters.Any(p => p.Name == variable.Name))
        {
            Emit(Op.Constant, value: specification.Initial[0]);
            return;
        }
        int count = variable.Indices.Count;
        int localSlot = read && !specification.Reference && !descriptor.Parameters.Any(p => p.Name == variable.Name && p.Reference)
            ? locals.FindIndex(v => v.Name == variable.Name) : -1;
        if (localSlot >= 0 && specification.Shape.Length == 1 && count == 1
            && variable.Indices[0] is Literal { Value.Kind: ValueKind.Integer } literal
            && literal.Value.Integer >= 0 && literal.Value.Integer < specification.Length)
        {
            Emit(Op.ReadLocalConstant, variable.Name, (int)literal.Value.Integer, variable: specification, localSlot: localSlot);
            return;
        }
        for (int i = 0; i < variable.Indices.Count; i++) Expression(Subscript(variable, variable.Indices[i], i));
        if (!variable.ExplicitIndices)
            while (count < specification.Shape.Length) { Emit(Op.Constant, value: new EraValue(0L)); count++; }
        var operation = !read ? Op.Reference : localSlot >= 0 ? Op.ReadLocal : specification.Scope == VariableScope.Character
            && !locals.Any(v => v.Name == variable.Name)
            && !descriptor.Parameters.Any(p => p.Name == variable.Name) ? Op.ReadCharacter : Op.ReadVariable;
        Emit(operation, variable.Name, count, new(variable.ExplicitIndices ? 1L : 0L), read ? specification : null, localSlot);
    }
    private void ArrayArgumentReference(Variable variable)
    {
        var specification = Resolve(variable.Name);
        bool character = specification.Scope == VariableScope.Character
            && (specification.CharacterScalar ? variable.ExplicitIndices : variable.Indices.Count == specification.Shape.Length + 1);
        if (character) Expression(variable.Indices[0]); else Emit(Op.Constant, value: new(0L));
        // 整数组引用只保留显式角色选择器，不求值元素下标。
        Emit(Op.Reference, variable.Name, 1, new(character ? 3L : 2L));
    }
    private Expr Subscript(Variable target, Expr subscript, int dimension)
    {
        var spec = Resolve(target.Name, target.Span);
        if (spec.Scope == VariableScope.Character && target.Indices.Count == spec.Shape.Length + 1) dimension--;
        if (dimension < 0) return subscript;
        var names = spec.NamesAt(dimension);
        if (subscript is Variable name && !name.ExplicitIndices && !locals.Any(v => v.Name == name.Name) && !index.VariablesByName.ContainsKey(name.Name)
            && names.TryGetValue(name.Keyword ?? name.Name, out int number)) return new Literal(new((long)number));
        if (subscript is Literal { Value.Kind: ValueKind.String } literal && names.TryGetValue(literal.Value.AsString(), out int at)) return new Literal(new((long)at));
        if (Kind(subscript) == ValueKind.String) return new Invocation("$NAME_INDEX", [new Literal(new(target.Name)), subscript, new Literal(new((long)dimension))]);
        return subscript;
    }
    private void Expression(Expr expression)
    {
        Kind(expression);
        switch (expression)
        {
            case Literal literal: Emit(Op.Constant, value: literal.Value); break;
            case FormattedLiteral format: Format(format.Text); break;
            case Variable { Name: "LINECOUNT" }: Emit(Op.LineCount); break;
            case Variable { Name: "CHARANUM" }: Builtin("CHARANUM"); break;
            case Variable { Name: "RAND" } random: InvokeBuiltin("RAND", random.Indices); break;
            case Variable { Name: "DRAWLINESTR" }: Builtin("DRAWLINESTR"); break;
            case Variable variable: Reference(variable, read: true); break;
            case Unary unary: Expression(unary.Operand); Emit(Op.Unary, unary.Operator); break;
            case Binary binary when binary.Operator is "&&" or "||" or "!&" or "!|":
                Expression(binary.Left); Emit(Op.Unary, "bool"); Emit(Op.Duplicate);
                int skip = Emit(binary.Operator is "&&" or "!&" ? Op.JumpFalse : Op.JumpTrue);
                Emit(Op.Pop); Expression(binary.Right); Emit(Op.Unary, "bool"); Patch(skip, code.Count);
                if (binary.Operator is "!&" or "!|") Emit(Op.Unary, "!");
                break;
            case Binary binary when binary.Operator == "/" && Kind(binary.Left) == ValueKind.Float && Kind(binary.Right) == ValueKind.Float:
            {
                string divisor = Temporary(ValueKind.Float); Assign(Var(divisor), binary.Right);
                Expression(binary.Left); Expression(Var(divisor)); Emit(Op.Binary, binary.Operator); break;
            }
            case Binary binary: Expression(binary.Left); Expression(binary.Right); Emit(Op.Binary, binary.Operator); break;
            case Conditional conditional:
                Expression(conditional.Test); int no = Emit(Op.JumpFalse); Expression(conditional.Yes);
                int end = Emit(Op.Jump); Patch(no, code.Count); Expression(conditional.No); Patch(end, code.Count); break;
            case Invocation invocation when invocation.Name == "LINECOUNT" && invocation.Arguments.Count == 0: Emit(Op.LineCount); break;
            case Invocation invocation when builtins.Find(invocation.Name) is not null:
                InvokeBuiltin(invocation.Name, invocation.Arguments); break;
            case Invocation invocation: Call(invocation.Name, invocation.Arguments, 1); break;
            case Mutation mutation:
                Reference(mutation.Variable); Emit(Op.Unary, mutation.Postfix ? "post" : "pre", mutation.Delta); break;
        }
    }
    private void Expression(string text) => Expression(Parse(text));
    private void TypedExpression(string text, ValueKind kind)
    {
        var expression = Parse(text);
        if (Kind(expression) != kind) throw new ScriptException("TYPE", "表达式类型不匹配", expression.Span ?? span);
        Expression(expression);
    }
    private void Assign(Variable target, Expr value)
    { var store = PrepareStore(target); Expression(value); Store(store); }
    private int ConditionFalse(string text)
    {
        var expression = Parse(text);
        if (Kind(expression) != ValueKind.Integer) throw new ScriptException("TYPE", "表达式类型不匹配", expression.Span ?? span);
        if (expression is Binary { Operator: "==" or "!=" or "<" or ">" or "<=" or ">=" } comparison)
        {
            Expression(comparison.Left); Expression(comparison.Right);
            return Emit(Op.JumpComparisonFalse, comparison.Operator);
        }
        Expression(expression);
        return Emit(Op.JumpFalse);
    }
    private Instruction? PrepareStore(Variable target)
    {
        var specification = Resolve(target.Name);
        int slot = locals.FindIndex(v => v.Name == target.Name);
        if (slot >= 0 && !specification.Reference && !descriptor.Parameters.Any(p => p.Name == target.Name && p.Reference)
            && specification.Shape.Length == 1 && target.Indices.Count == 1
            && target.Indices[0] is Literal { Value.Kind: ValueKind.Integer } literal
            && literal.Value.Integer >= 0 && literal.Value.Integer < specification.Length)
            return new(Op.StoreLocalConstant, target.Name, (int)literal.Value.Integer, default, span) { LocalSlot = slot };
        Reference(target);
        return null;
    }
    private void Store(Instruction? store)
    { if (store is null) Emit(Op.Store); else code.Add(store); }
    private void ReadStoreTarget(Instruction? store)
    {
        if (store is null) { Emit(Op.Duplicate); Emit(Op.Load); }
        else Emit(Op.ReadLocalConstant, store.Text, store.Number, localSlot: store.LocalSlot);
    }
    private void Increment(Variable target, Variable step)
    {
        var store = PrepareStore(target);
        int stepSlot = locals.FindIndex(v => v.Name == step.Name);
        if (store is not null && stepSlot >= 0)
        {
            Emit(Op.IncrementLocal, store.Text, store.Number, new EraValue(stepSlot), localSlot: store.LocalSlot);
            return;
        }
        ReadStoreTarget(store); Expression(step); Emit(Op.Binary, "+"); Store(store);
    }
    private void OptimizeCharacterFill(Block block)
    {
        int start = block.Start;
        if (code.Count - start != 6 || code[start] is not { Op: Op.JumpForLocalEnd, LoopCondition: { } loop }) return;
        var character = code[start + 1]; var counter = code[start + 2];
        var reference = code[start + 3]; var value = code[start + 4];
        if (character.Op != Op.ReadLocalConstant || counter.Op != Op.ReadLocalConstant
            || counter.LocalSlot != loop.CounterSlot || counter.Number != loop.CounterIndex
            || character.LocalSlot == loop.CounterSlot && character.Number == loop.CounterIndex
            || reference.Op != Op.Reference || reference.Number != 2 || reference.Value.Integer != 1
            || value.Op != Op.Constant || code[start + 5].Op != Op.Store
            || locals.Any(v => v.Name == reference.Text) || descriptor.Parameters.Any(p => p.Name == reference.Text)) return;
        var variable = Resolve(reference.Text);
        if (variable is not { Scope: VariableScope.Character, Rank: 1, CharacterScalar: false, ReadOnly: false, Reference: false }
            || variable.Kind != value.Value.Kind) return;
        code[start] = code[start] with { LoopCondition = loop with
            { Fill = new(variable, character.LocalSlot, character.Number, value.Value) } };
    }
    private void OptimizeCharacterCopy(Block block)
    {
        int start = block.Start, at = start + 1;
        if (code[start] is not { Op: Op.JumpForLocalEnd, LoopCondition: { } loop } || code.Count - start < 8) return;
        var character = code[at++];
        if (character.Op != Op.ReadLocalConstant
            || character.LocalSlot == loop.CounterSlot && character.Number == loop.CounterIndex
            || !ReadIndex(out int destinationOffset) || at >= code.Count) return;
        var destination = code[at++];
        if (destination.Op != Op.Reference || destination.Number != 2 || destination.Value.Integer != 1
            || at >= code.Count || code[at] is not { Op: Op.ReadLocalConstant } sourceCharacter
            || sourceCharacter.LocalSlot != character.LocalSlot || sourceCharacter.Number != character.Number) return;
        at++;
        if (!ReadIndex(out int sourceOffset) || at + 2 != code.Count) return;
        var source = code[at++];
        if (source.Op != Op.ReadCharacter || source.Number != 2 || source.Value.Integer != 1 || code[at].Op != Op.Store
            || locals.Any(v => v.Name == destination.Text) || descriptor.Parameters.Any(p => p.Name == destination.Text)) return;
        var target = Resolve(destination.Text); var origin = source.ResolvedVariable;
        if (target is not { Scope: VariableScope.Character, Rank: 1, CharacterScalar: false, ReadOnly: false, Reference: false }
            || origin is not { Scope: VariableScope.Character, Rank: 1, CharacterScalar: false, Reference: false }
            || target.Kind != origin.Kind) return;
        code[start] = code[start] with { LoopCondition = loop with
            { Copy = new(target, origin, character.LocalSlot, character.Number, destinationOffset, sourceOffset) } };

        bool Counter(Instruction instruction) => instruction.Op == Op.ReadLocalConstant
            && instruction.LocalSlot == loop.CounterSlot && instruction.Number == loop.CounterIndex;
        bool ReadIndex(out int offset)
        {
            offset = 0;
            if (at >= code.Count) return false;
            if (at + 2 < code.Count && code[at + 2] is { Op: Op.Binary, Text: "+" })
            {
                var constant = Counter(code[at]) ? code[at + 1] : Counter(code[at + 1]) ? code[at] : null;
                if (constant is not { Op: Op.Constant, Value.Kind: ValueKind.Integer }
                    || constant.Value.Integer < 0 || constant.Value.Integer > int.MaxValue) return false;
                offset = (int)constant.Value.Integer; at += 3; return true;
            }
            return Counter(code[at++]);
        }
    }
    private void Call(string name, IReadOnlyList<Expr> args, int mode)
    {
        var target = index.Find(name) ?? throw new ScriptException("UNDEFINED_FUNCTION", "未定义函数: " + name, span);
        ValidateKnownCall(target, args, mode == 1);
        for (int i = 0; i < target.Parameters.Length; i++)
        {
            if (i >= args.Count || args[i] is Omitted)
            {
                if (target.Parameters[i].Reference) throw new ScriptException("REFERENCE", "引用参数不能省略", span);
                Emit(Op.Constant, text: "$omitted", value: new EraValue(0L)); continue;
            }
            if (target.Parameters[i].Reference)
            {
                if (args[i] is not Variable variable) throw new ScriptException("REFERENCE", "引用参数需要变量", span);
                if (Kind(variable) != target.Parameters[i].Kind) throw new ScriptException("TYPE", "引用参数类型不匹配", span);
                if (ReferenceRank(target, target.Parameters[i]) == 0) Reference(variable); else ArrayArgumentReference(variable);
            }
            else
            {
                if (!index.Options.CanBindArgument(Kind(args[i]), target.Parameters[i].Kind)) throw new ScriptException("TYPE", "调用参数类型不匹配", span);
                Expression(args[i]);
            }
        }
        Emit(Op.Call, target.Id, target.Parameters.Length, new((long)mode));
    }
    private void ValidateCallType(FunctionDescriptor target, bool methodCall)
    {
        if (target.Attributes.Any(a => a is "#FUNCTION" or "#FUNCTIONS") != methodCall)
            throw new ScriptException("FUNCTION_TYPE", methodCall ? "表达式调用需要 FUNCTION 属性" : "普通调用不能调用表达式函数", span);
    }
    private static int ReferenceRank(FunctionDescriptor target, Parameter parameter)
    {
        var variable = target.Locals.FirstOrDefault(v => v.Name == parameter.Name);
        return variable?.ReferenceRank ?? variable?.Shape.Length ?? 1;
    }
    private int CallArgumentKind(Expr expression)
    {
        if (expression is Omitted) return 0;
        int kind = (int)Kind(expression) + 1;
        if (expression is not Variable variable || variable.Name is "RAND" or "CHARANUM" or "LINECOUNT" or "DRAWLINESTR") return kind;
        var specification = Resolve(variable.Name);
        int rank = specification.ReferenceRank ?? (specification.CharacterScalar ? 0 : specification.Shape.Length);
        return kind | 4 | (specification.ReadOnly ? 8 : 0) | (specification.Scope == VariableScope.Character ? 16 : 0) | rank << 6;
    }
    private void ValidateReferenceArgument(FunctionDescriptor target, Parameter parameter, Variable variable)
    {
        var specification = Resolve(variable.Name);
        int rank = specification.ReferenceRank ?? (specification.CharacterScalar ? 0 : specification.Shape.Length), expected = ReferenceRank(target, parameter);
        if (specification.ReadOnly || expected > 0 && expected != rank)
            throw new ScriptException("REFERENCE", "引用参数不接受常量或不同维数", span);
    }
    private void ValidateKnownCall(FunctionDescriptor target, IReadOnlyList<Expr> args, bool methodCall)
    {
        ValidateCallType(target, methodCall);
        for (int i = 0; i < target.Parameters.Length; i++)
        {
            var parameter = target.Parameters[i];
            if (i >= args.Count || args[i] is Omitted)
            {
                if (parameter.Reference) throw new ScriptException("REFERENCE", "引用参数不能省略", span);
                if (index.Options.Profile != "minimal-v1" && !index.Options.CompatiFuncArgOptional && parameter.Default is null)
                    throw new ScriptException("ARGUMENT_COUNT", "调用参数不能省略", span);
                continue;
            }
            if (!index.Options.CanBindArgument(Kind(args[i]), parameter.Kind, parameter.Reference)) throw new ScriptException("TYPE", "调用参数类型不匹配", span);
            if (parameter.Reference && args[i] is not Variable) throw new ScriptException("REFERENCE", "引用参数需要变量", span);
            if (parameter.Reference) ValidateReferenceArgument(target, parameter, (Variable)args[i]);
        }
    }
    private void Format(string text)
        => Format(FormattedText.Parse(text, span, IgnoreTripleSymbols));
    private void Format(IReadOnlyList<FormatPart> parts)
    {
        Emit(Op.Constant, value: new EraValue(""));
        foreach (var part in parts)
        {
            if (part is FormatText literal)
            { Emit(Op.Constant, value: new EraValue(literal.Text)); Emit(Op.Binary, "+"); continue; }
            if (part is FormatChoice choice)
            {
                TypedExpression(choice.Condition, ValueKind.Integer); int alternate = Emit(Op.JumpFalse);
                Format(choice.Yes); int finish = Emit(Op.Jump); Patch(alternate, code.Count);
                Format(choice.No); Patch(finish, code.Count); Emit(Op.Binary, "+"); continue;
            }
            var field = (FormatField)part;
            var arguments = FormattedText.FieldArguments(field, span, text => macros.Expand(text, span));
            var value = Parse(arguments.Expression);
            if (field.String ? Kind(value) != ValueKind.String : Kind(value) == ValueKind.String)
                throw new ScriptException("TYPE", "格式字段类型不匹配", value.Span ?? span);
            Expression(value); Emit(Op.Unary, "text");
            if (arguments.Width is { } width)
            {
                TypedExpression(width, ValueKind.Integer);
                if (field.String) Emit(Op.Unary, "nonnegative");
                Emit(Op.Constant, value: new EraValue(arguments.Left ? 1L : 0L)); Builtin("$FORMAT_FIELD");
            }
            Emit(Op.Binary, "+");
        }
    }
    public CompileResult Compile(IReadOnlyList<ExpandedLine> lines, CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (var attribute in descriptor.Attributes.Where(a => a is not ("#FUNCTION" or "#FUNCTIONS" or "#PRI" or "#LATER" or "#SINGLE" or "#ONLY")))
            diagnostics.Add(new("UNSUPPORTED_ATTRIBUTE", "尚未实现函数属性: " + attribute, descriptor.Span));
        int low = 0, high = lines.Count;
        while (low < high)
        { int middle = low + (high - low) / 2; if (lines[middle].Line.Span.Start < descriptor.BodyStart) low = middle + 1; else high = middle; }
        for (int lineIndex = low; lineIndex < lines.Count && lines[lineIndex].Line.Span.Start < descriptor.BodyEnd; lineIndex++)
        {
            ct.ThrowIfCancellationRequested(); var expanded = lines[lineIndex];
            var line = expanded.Line;
            span = line.Span;
            string text = LogicalLines.StripStatementComment(line.Text);
            lineText = text;
            if (text.Length == 0) continue;
            if (text[0] == '#')
            {
                string attribute = LogicalLines.HeadName(text);
                if (attribute is not ("#DIM" or "#DIMS" or "#DIMF" or "#FUNCTION" or "#FUNCTIONS" or "#LOCALSIZE" or "#LOCALSSIZE" or "#LOCALFSIZE" or "#PRI" or "#LATER" or "#SINGLE" or "#ONLY"))
                    diagnostics.Add(new("UNSUPPORTED_ATTRIBUTE", "尚未实现声明或属性", span));
                continue;
            }
            int? previousSif = sif; sif = null;
            try { Statement(text); }
            catch (ScriptException e) { diagnostics.Add(e.Diagnostic with { Span = e.Diagnostic.Span ?? span, Related = expanded.Map.MacroOrigins }); }
            if (previousSif is { } at) Patch(at, code.Count);
        }
        if (blocks.Count > 0 || sif is not null || dataCommand is not null) diagnostics.Add(new("UNCLOSED_BLOCK", "控制结构未结束", span));
        foreach (var (at, label) in gotos)
            if (labels.TryGetValue(label, out int target)) Patch(at, target);
            else diagnostics.Add(new("UNDEFINED_LABEL", "标签不存在: " + label, code[at].Span));
        bool method = descriptor.Attributes.Any(a => a is "#FUNCTION" or "#FUNCTIONS");
        Emit(Op.Constant, value: descriptor.Attributes.Contains("#FUNCTIONS") ? new EraValue("") : new EraValue(0L));
        Emit(Op.Return, method ? "RETURNF" : "RETURN", number: 1);
        if (diagnostics.Count == 0) diagnostics.AddRange(BytecodeValidator.Validate(code.ToImmutableArray()));
        return diagnostics.Count > 0 ? new(null, diagnostics.ToImmutable()) : new(new(descriptor with { Locals = locals.ToImmutableArray() }, code.ToImmutableArray()), []);
    }
    private Block Top(string kind)
    {
        if (blocks.Count == 0 || blocks.Peek().Kind != kind) throw new ScriptException("BLOCK", "不匹配的控制结构: " + kind, span);
        return blocks.Peek();
    }
    private void Statement(string text)
    {
        string command = LogicalLines.HeadName(text);
        bool output = KnownIdentifiers.Commands.Contains(command) && command.StartsWith("PRINT", StringComparison.Ordinal) && command is not ("PRINTCLENGTH" or "PRINTCPERLINE")
            || command is "DRAWLINE" or "DRAWLINEFORM" or "CUSTOMDRAWLINE" or "HTML_PRINT" or "HTML_PRINTC" or "HTML_PRINTLC" or "REUSELASTLINE" or "WAIT" or "TWAIT" or "FORCEWAIT";
        if (dataCommand is not null) output = command == "ENDDATA" && dataCommand.StartsWith("PRINTDATA", StringComparison.Ordinal);
        else if (command.StartsWith("PRINTDATA", StringComparison.Ordinal)) output = false;
        if (command is "INPUT" or "INPUTS" or "TINPUT" or "TINPUTS" or "TINPUTNF" or "TINPUTSNF") Builtin("$INPUT_ALLOWED");
        int skip = -1;
        if (output) { Builtin("$DISPLAY_ENABLED"); skip = Emit(Op.JumpFalse); }
        StatementCore(text);
        if (skip >= 0) Patch(skip, code.Count);
    }
    private void StatementCore(string text)
    {
        if (dataCommand is not null) { DataStatement(text); return; }
        if (text[0] == '$')
        { if (!labels.TryAdd(text[1..].Trim().ToUpperInvariant(), code.Count)) throw new ScriptException("DUPLICATE_LABEL", "标签重复", span); return; }
        var (name, rest) = LogicalLines.StatementHead(text);
        if (!index.Options.Debug && name is "DEBUGPRINT" or "DEBUGPRINTL" or "DEBUGPRINTFORM" or "DEBUGPRINTFORML" or "DEBUGCLEAR") return;
        if (descriptor.Attributes.Any(a => a is "#FUNCTION" or "#FUNCTIONS")
            && name is "BEGIN" or "CALL" or "CALLFORM" or "CALLSTR" or "JUMP" or "JUMPFORM"
                or "RETURN" or "TRYCALL" or "TRYCALLFORM" or "TRYCCALL" or "TRYCCALLFORM"
                or "TRYCALLSTR" or "TRYCCALLSTR" or "TRYJUMP" or "TRYJUMPFORM" or "TRYCJUMP" or "TRYCJUMPFORM" or "DOTRAIN")
            throw new ScriptException("METHOD_COMMAND", "表达式函数不能使用流程指令: " + name, span);
        var args = LogicalLines.Split(rest, name.StartsWith("PRINTV", StringComparison.Ordinal));
        if (name.StartsWith("PRINTDATA", StringComparison.Ordinal) || name == "STRDATA")
        { dataCommand = name; dataTarget = name == "STRDATA" && rest.Length == 0 ? "RESULTS" : rest; dataGroups.Clear(); dataList = null; return; }
        switch (name)
        {
            case "NOSKIP":
            {
                string saved = Temporary(ValueKind.Integer); Reference(Var(saved)); Builtin("ISSKIP"); Emit(Op.Store);
                Emit(Op.Constant, value: new EraValue(0L)); Builtin("$SKIP_CONTROL");
                blocks.Push(new("NOSKIP", code.Count) { Variable = saved }); break;
            }
            case "ENDNOSKIP":
            {
                var saved = Top("NOSKIP"); blocks.Pop(); Expression(Var(saved.Variable)); Builtin("$SKIP_CONTROL"); break;
            }
            case "IF": { int branch = ConditionFalse(rest); blocks.Push(new("IF", branch) { False = branch }); break; }
            case "ELSEIF":
            {
                var block = Top("IF"); if (block.False < 0) throw new ScriptException("BLOCK", "ELSE 后不能 ELSEIF", span);
                block.Ends.Add(Emit(Op.Jump)); Patch(block.False, code.Count); block.False = ConditionFalse(rest); break;
            }
            case "ELSE":
            {
                var block = Top("IF"); if (block.False < 0) throw new ScriptException("BLOCK", "重复 ELSE", span);
                block.Ends.Add(Emit(Op.Jump)); Patch(block.False, code.Count); block.False = -1; break;
            }
            case "ENDIF":
            { var block = Top("IF"); blocks.Pop(); if (block.False >= 0) Patch(block.False, code.Count); foreach (int at in block.Ends) Patch(at, code.Count); break; }
            case "SIF": sif = ConditionFalse(args.Count == 2 && args[1].Length == 0 ? args[0] : rest); break;
            case "SELECTCASE":
            {
                var selector = Parse(rest); var kind = Kind(selector); string temp = Temporary(kind); Assign(Var(temp), selector);
                blocks.Push(new("SELECT", code.Count) { Variable = temp, StringTargets = kind == ValueKind.String ? new(StringComparer.Ordinal) : null,
                    IntegerTargets = kind == ValueKind.Integer ? new() : null }); break;
            }
            case "CASE": case "CASEELSE":
            {
                var block = Top("SELECT");
                if ((block.CaseCount++ == 0 && block.Start != code.Count) || block.DefaultTarget >= 0)
                { block.StringTargets = null; block.IntegerTargets = null; }
                if (block.Start != code.Count) block.Ends.Add(Emit(Op.Jump));
                if (block.False >= 0) Patch(block.False, code.Count);
                block.False = -1;
                if (name == "CASE")
                {
                    if (args.Count > 1 && args[^1].Length == 0) args.RemoveAt(args.Count - 1);
                    Expr? condition = null;
                    List<string>? keys = block.StringTargets is null ? null : [];
                    List<long>? integers = block.IntegerTargets is null ? null : [];
                    for (int i = 0; i < args.Count; i++)
                    {
                        Expr item;
                        int range = args[i].IndexOf(" TO ", StringComparison.OrdinalIgnoreCase);
                        if (range >= 0)
                        {
                            block.StringTargets = null;
                            var lower = Parse(args[i][..range]); var upper = Parse(args[i][(range + 4)..]);
                            if (integers is not null && TryCaseInteger(lower, out long first) && TryCaseInteger(upper, out long last)
                                && first <= last && (decimal)last - first <= 256)
                            {
                                for (long key = first; ; key++)
                                { integers.Add(key); if (key == last) break; }
                            }
                            else block.IntegerTargets = null;
                            item = new Binary("&&", new Binary(">=", Var(block.Variable), lower),
                                new Binary("<=", Var(block.Variable), upper));
                        }
                        else if (args[i].StartsWith("IS", StringComparison.OrdinalIgnoreCase) && args[i].Length > 2 && (char.IsWhiteSpace(args[i][2]) || "<>=!".Contains(args[i][2])))
                        {
                            if (Parse("0 " + args[i][2..]) is not Binary comparison || comparison.Operator is not ("==" or "!=" or "<" or ">" or "<=" or ">="))
                                throw new ScriptException("CASE", "CASE IS 需要比较运算符", span);
                            block.StringTargets = null;
                            block.IntegerTargets = null;
                            item = comparison with { Left = Var(block.Variable) };
                        }
                        else
                        {
                            var value = Parse(args[i]);
                            if (value is Literal { Value.Kind: ValueKind.String } literal) keys?.Add(literal.Value.AsString());
                            else block.StringTargets = null;
                            if (integers is not null && TryCaseInteger(value, out long integer)) integers.Add(integer);
                            else block.IntegerTargets = null;
                            item = new Binary("==", Var(block.Variable), value);
                        }
                        condition = condition is null ? item : new Binary("||", condition, item);
                    }
                    Expression(condition ?? throw new ScriptException("CASE", "CASE 条件为空", span));
                    block.False = Emit(Op.JumpFalse);
                    if (block.StringTargets is { } targets)
                        foreach (string key in keys!) targets.TryAdd(key, code.Count);
                    if (block.IntegerTargets is { } integerTargets)
                    {
                        foreach (long key in integers!) integerTargets.TryAdd(key, code.Count);
                        if (integerTargets.Count > 1024) block.IntegerTargets = null;
                    }
                }
                else block.DefaultTarget = code.Count;
                break;
            }
            case "ENDSELECT":
            {
                var block = Top("SELECT"); blocks.Pop(); if (block.False >= 0) Patch(block.False, code.Count); foreach (int at in block.Ends) Patch(at, code.Count);
                if (block.StringTargets is { Count: >= 4 } targets)
                    // 保留原指令地址，已有标签和跳转仍然有效；重复值保留首次出现的分支。
                    code[block.Start] = new(Op.SwitchLocalString, block.Variable, block.DefaultTarget >= 0 ? block.DefaultTarget : code.Count, default, code[block.Start].Span)
                    { LocalSlot = locals.FindIndex(v => v.Name == block.Variable), StringTargets = targets.ToImmutableDictionary(StringComparer.Ordinal) };
                if (block.IntegerTargets is { Count: >= 4 } integerTargets)
                    code[block.Start] = new(Op.SwitchLocalInteger, block.Variable, block.DefaultTarget >= 0 ? block.DefaultTarget : code.Count, default, code[block.Start].Span)
                    { LocalSlot = locals.FindIndex(v => v.Name == block.Variable), IntegerTargets = integerTargets.ToImmutableDictionary() };
                break;
            }
            case "WHILE":
            { int start = code.Count; int branch = ConditionFalse(rest); blocks.Push(new("WHILE", start) { False = branch }); break; }
            case "DO": blocks.Push(new("DO", code.Count)); break;
            case "LOOP":
            {
                var block = Top("DO"); blocks.Pop(); foreach (int at in block.Continues) Patch(at, code.Count);
                TypedExpression(rest, ValueKind.Integer); Emit(Op.JumpTrue, number: block.Start); foreach (int at in block.Ends) Patch(at, code.Count); break;
            }
            case "REPEAT": Statement("FOR COUNT, 0, " + rest); blocks.Peek().Kind = "REPEAT"; break;
            case "REND": Top("REPEAT").Kind = "FOR"; Statement("NEXT"); break;
            case "FOR":
            {
                if (args.Count > 4 && args[^1].Length == 0) args.RemoveAt(args.Count - 1);
                if (args.Count is < 3 or > 4) throw new ScriptException("ARGUMENT_COUNT", "FOR 需要变量、起点、终点和可选步长", span);
                if (Parse(args[0]) is not Variable variable) throw new ScriptException("REFERENCE", "FOR 需要变量", span);
                if (Kind(variable) != ValueKind.Integer) throw new ScriptException("TYPE", "FOR 计数变量需要整数", span);
                if (Resolve(variable.Name) is { ReadOnly: true } or { Scope: VariableScope.Character })
                    throw new ScriptException("REFERENCE", "FOR 不接受常量或角色变量", span);
                var initialValue = Parse(args[1].Length == 0 ? "0" : args[1]); var endValue = Parse(args[2]);
                var stepValue = Parse(args.Count == 4 && args[3].Length > 0 ? args[3] : "1");
                if (Kind(initialValue) != ValueKind.Integer || Kind(endValue) != ValueKind.Integer || Kind(stepValue) != ValueKind.Integer)
                    throw new ScriptException("TYPE", "FOR 起点、终点和步长需要整数", span);
                string bound = Temporary(), step = Temporary(), initial = Temporary();
                Assign(Var(initial), initialValue); Assign(variable, Var(initial)); Assign(Var(bound), endValue); Assign(Var(step), stepValue);
                int start = code.Count;
                int counterSlot = locals.FindIndex(v => v.Name == variable.Name);
                var counterSpec = Resolve(variable.Name);
                if (counterSlot >= 0 && !counterSpec.Reference && counterSpec.Rank == 1
                    && !descriptor.Parameters.Any(p => p.Name == variable.Name && p.Reference)
                    && variable.Indices.Count == 1 && variable.Indices[0] is Literal { Value.Kind: ValueKind.Integer } counterIndex
                    && counterIndex.Value.Integer >= 0 && counterIndex.Value.Integer < counterSpec.Length)
                {
                    int at = Emit(Op.JumpForLocalEnd);
                    code[at] = code[at] with { LoopCondition = new(counterSlot, (int)counterIndex.Value.Integer,
                        locals.FindIndex(v => v.Name == bound), locals.FindIndex(v => v.Name == step)) };
                }
                else Expression(new Conditional(new Binary(">", Var(step), new Literal(new(0L))), new Binary("<", variable, Var(bound)),
                    new Conditional(new Binary("<", Var(step), new Literal(new(0L))), new Binary(">", variable, Var(bound)), new Literal(new(0L)))));
                int endJump = code[start].Op == Op.JumpForLocalEnd ? start : Emit(Op.JumpFalse);
                blocks.Push(new("FOR", start) { False = endJump, Counter = variable, Bound = bound, Step = step }); break;
            }
            case "WEND": case "NEXT":
            {
                var block = Top(name == "NEXT" ? "FOR" : "WHILE"); blocks.Pop();
                foreach (int at in block.Continues) Patch(at, code.Count);
                if (block.Kind == "FOR") { OptimizeCharacterFill(block); OptimizeCharacterCopy(block); Increment(block.Counter!, Var(block.Step)); }
                Emit(Op.Jump, number: block.Start); Patch(block.False, code.Count); foreach (int at in block.Ends) Patch(at, code.Count); break;
            }
            case "BREAK": case "CONTINUE":
            {
                var loop = blocks.FirstOrDefault(b => b.Kind is "FOR" or "WHILE" or "DO" or "REPEAT") ?? throw new ScriptException("LOOP", "不在循环中", span);
                if (name == "BREAK" && loop.Counter is { } counter) Increment(counter, Var(loop.Step));
                (name == "BREAK" ? loop.Ends : loop.Continues).Add(Emit(Op.Jump)); break;
            }
            case "GOTO": gotos.Add((Emit(Op.Jump), rest.ToUpperInvariant())); break;
            case "RESTART":
                if (rest.Length != 0) throw new ScriptException("ARGUMENT_COUNT", "RESTART 不接受参数", span);
                Emit(Op.Jump, number: 0); break;
            case "THROW": Format(rest); Emit(Op.Throw); break;
            case "SETBIT": case "CLEARBIT": case "INVERTBIT":
                if (args.Count < 2) throw new ScriptException("ARGUMENT_COUNT", "位操作需要变量和位下标", span);
                foreach (string bit in args.Skip(1)) InvokeBuiltin(name, [Parse(args[0]), Parse(bit)]);
                break;
            case "CALLF":
            {
                var parts = LogicalLines.Split(rest);
                var expression = parts.Count > 1 ? new Invocation(parts[0].Trim().ToUpperInvariant(), parts.Skip(1).Select(Parse).ToArray()) : Parse(rest);
                if (expression is Variable { ExplicitIndices: false } method) expression = new Invocation(method.Name, []);
                if (expression is not Invocation) throw new ScriptException("CALL", "CALLF 需要函数表达式", span);
                Kind(expression); Expression(expression); Emit(Op.Pop); break;
            }
            case "VARSIZE":
            {
                if (Parse(rest) is not Variable variable) throw new ScriptException("VARIABLE", "VARSIZE 指令需要变量名称", span);
                var specification = Resolve(variable.Name); var shape = specification.Shape;
                if (specification.Reference) { ArrayArgumentReference(Var(variable.Name)); Emit(Op.Pop); }
                for (int i = 0; i < shape.Length; i++)
                    Assign(new Variable("RESULT", new Literal(new((long)i))), specification.Reference
                        ? new Invocation("VARSIZE", [new Literal(new(variable.Name)), new Literal(new((long)i))]) : new Literal(new((long)shape[i])));
                break;
            }
            case "CALLSHARP":
            {
                var (target, tail) = LogicalLines.Head(rest);
                if (target.Length == 0) throw new ScriptException("ARGUMENT_COUNT", "CALLSHARP 缺少方法名", span);
                tail = tail.TrimStart(',').Trim();
                if (tail.StartsWith('(') && tail.EndsWith(')')) tail = tail[1..^1];
                var parameters = tail.Length == 0 ? [] : LogicalLines.Split(tail);
                InvokeBuiltin("CALLSHARP", new Expr[] { new Literal(new EraValue(target)) }.Concat(parameters.Select(Parse)).ToArray());
                break;
            }
            case "CALL": case "JUMP":
            {
                var (target, tail) = LogicalLines.Head(rest);
                if (tail.StartsWith(',')) tail = tail[1..].Trim();
                if (tail.StartsWith('(') && tail.EndsWith(')')) tail = tail[1..^1];
                var arguments = tail.Length == 0 ? [] : LogicalLines.Split(tail);
                var function = index.Find(target) ?? throw new ScriptException("UNDEFINED_FUNCTION", "未定义函数: " + target, span);
                var expressions = arguments.Select(a => a.Length == 0 ? (Expr)new Omitted() : Parse(a)).ToArray();
                Call(target, expressions, name == "JUMP" ? 2 : 0); break;
            }
            case "CALLFORM": case "TRYCALLFORM": case "TRYCCALLFORM": case "CALLFORMF": case "TRYCALLFORMF":
            case "CALLSTR": case "TRYCALLSTR": case "TRYCCALLSTR":
            case "JUMPFORM": case "TRYJUMPFORM": case "TRYCJUMPFORM":
            case "TRYCALL": case "TRYCCALL": case "TRYJUMP": case "TRYCJUMP":
            {
                bool formatted = name.Contains("FORM", StringComparison.Ordinal);
                bool stringTarget = name.EndsWith("STR", StringComparison.Ordinal);
                bool methodCall = name.EndsWith("FORMF", StringComparison.Ordinal);
                if (stringTarget)
                {
                    TypedExpression(rest, ValueKind.String); Emit(Op.StringCall, name);
                    if (name == "TRYCCALLSTR") blocks.Push(new("TRYC", code.Count) { False = Emit(Op.JumpFalse) });
                    else Emit(Op.Pop);
                    break;
                }
                if (formatted) args = FormattedText.CallArguments(rest, span, IgnoreTripleSymbols);
                else if (!stringTarget)
                {
                    var (literalTarget, tail) = LogicalLines.Head(rest);
                    if (tail.StartsWith(',')) tail = tail[1..].Trim();
                    if (tail.StartsWith('(') && tail.EndsWith(')')) tail = tail[1..^1];
                    args = new List<string> { literalTarget };
                    if (tail.Length > 0) args.AddRange(LogicalLines.Split(tail));
                }
                IReadOnlyList<FormatPart> targetParts = formatted ? FormattedText.Parse(args[0], span, IgnoreTripleSymbols) : [new FormatText(args[0])];
                if (targetParts.All(p => p is FormatText) && index.Find(string.Concat(targetParts.Cast<FormatText>().Select(p => p.Text))) is { } known)
                {
                    try { ValidateKnownCall(known, args.Skip(1).Select(a => a.Length == 0 ? (Expr)new Omitted() : Parse(a)).ToArray(), methodCall); }
                    catch (ScriptException) when (name == "TRYCALLFORMF") { break; }
                }
                int missing = -1;
                bool strictMethodArguments = name == "TRYCALLFORMF" && targetParts.Any(p => p is not FormatText);
                string target = Temporary(ValueKind.String);
                Reference(Var(target)); if (stringTarget) TypedExpression(args[0], ValueKind.String); else Format(targetParts); Emit(Op.Store);
                Expression(Var(target)); Emit(Op.Constant, value: new(methodCall ? 1L : 0L)); Builtin("$CALL_TARGET");
                bool tryCall = name.StartsWith("TRY", StringComparison.Ordinal);
                if (strictMethodArguments) missing = Emit(Op.JumpFalse);
                else { if (!tryCall) Emit(Op.Unary, "call_target"); Emit(Op.Pop); }
                {
                    Expression(Var(target));
                    foreach (string arg in args.Skip(1))
                    {
                        Expr expression = arg.Length == 0 ? new Omitted() : Parse(arg);
                        int kind = CallArgumentKind(expression);
                        Emit(Op.Constant, value: new((long)kind));
                    }
                    Builtin("$CALL_ACCEPTS", args.Count);
                    if (strictMethodArguments || !tryCall) { Emit(Op.Unary, "call_arguments"); Emit(Op.Pop); } else missing = Emit(Op.JumpFalse);
                }
                Expression(Var(target));
                for (int i = 1; i < args.Count; i++)
                {
                    var arg = args[i];
                    if (arg.Length == 0) { Emit(Op.Constant, text: "$omitted", value: new(0L)); continue; }
                    Expression(Var(target)); Builtin("$CALL_ARITY"); Emit(Op.Constant, value: new((long)(i - 1))); Emit(Op.Binary, ">");
                    int ignored = Emit(Op.JumpFalse);
                    var expression = Parse(arg);
                    if (expression is Variable variable)
                    {
                        Expression(Var(target)); Emit(Op.Constant, value: new((long)(i - 1))); Builtin("$CALL_REFERENCE");
                        int byValue = Emit(Op.JumpFalse);
                        ArrayArgumentReference(variable);
                        int bound = Emit(Op.Jump); Patch(byValue, code.Count);
                        Reference(variable); Patch(bound, code.Count);
                    }
                    else Expression(expression);
                    int end = Emit(Op.Jump); Patch(ignored, code.Count);
                    Emit(Op.Constant, text: "$omitted", value: new(0L)); Patch(end, code.Count);
                }
                Emit(Op.DynamicCall, methodCall ? "CALLFORMF" : "CALLFORM", args.Count - 1, new(methodCall ? 1L : name.Contains("JUMP", StringComparison.Ordinal) ? 2L : 0L));
                if (methodCall) Emit(Op.Pop);
                if (name is "TRYCCALLFORM" or "TRYCJUMPFORM" or "TRYCCALL" or "TRYCJUMP" or "TRYCCALLSTR") blocks.Push(new("TRYC", code.Count) { False = missing });
                else if (missing >= 0) Patch(missing, code.Count);
                break;
            }
            case "CATCH":
            {
                var block = Top("TRYC"); block.Ends.Add(Emit(Op.Jump)); Patch(block.False, code.Count); block.False = -1; block.Kind = "CATCH"; break;
            }
            case "ENDCATCH":
            {
                var block = Top("CATCH"); blocks.Pop(); foreach (int at in block.Ends) Patch(at, code.Count); break;
            }
            case "RETURN": case "RETURNF":
                if (name == "RETURNF" && !descriptor.Attributes.Any(a => a is "#FUNCTION" or "#FUNCTIONS")) throw new ScriptException("FUNCTION_TYPE", "RETURNF 需要函数属性", span);
                if (name == "RETURNF" && args.Count != 1) throw new ScriptException("ARGUMENT_COUNT", "RETURNF 需要一个返回值", span);
                if (rest.Length > 0)
                    foreach (var arg in args)
                        if (Kind(Parse(arg)) != (name == "RETURNF" && descriptor.Attributes.Contains("#FUNCTIONS") ? ValueKind.String : ValueKind.Integer))
                            throw new ScriptException("TYPE", "返回值类型不匹配", span);
                if (rest.Length == 0) Emit(Op.Constant, value: name == "RETURNF" && descriptor.Attributes.Contains("#FUNCTIONS") ? new EraValue("") : new EraValue(0L)); else foreach (var arg in args) Expression(arg);
                Emit(Op.Return, name, rest.Length == 0 ? 1 : args.Count); break;
            case "PRINTV": case "PRINTVL": case "PRINTVW": case "PRINTVD": case "PRINTVDL": case "PRINTVDW":
                if (args[^1].Length == 0) args.RemoveAt(args.Count - 1);
                Emit(Op.Constant, value: new EraValue(""));
                foreach (string argument in args)
                {
                    if (argument.StartsWith('\'')) Emit(Op.Constant, value: new EraValue(StringEscapes.Decode(argument[1..], span)));
                    else { Expression(argument); Emit(Op.Unary, "text"); }
                    Emit(Op.Binary, "+");
                }
                Emit(Op.Emit, text: name.StartsWith("PRINTVD", StringComparison.Ordinal) ? "D" : "", number: name.EndsWith('L') || name.EndsWith('W') ? 1 : 0);
                if (name.EndsWith('W')) Emit(Op.Input, "WAIT"); break;
            case "PRINT": case "PRINTL": case "PRINTW": case "PRINTFORM": case "PRINTFORML": case "PRINTFORMW":
            case "PRINTK": case "PRINTKL": case "PRINTKW": case "PRINTFORMK": case "PRINTFORMKL": case "PRINTFORMKW":
            case "PRINTD": case "PRINTDL": case "PRINTDW": case "PRINTFORMD": case "PRINTFORMDL": case "PRINTFORMDW":
                if (name.Contains("FORM", StringComparison.Ordinal)) Format(rest); else Emit(Op.Constant, value: new EraValue(rest));
                if (name.Contains('K')) Builtin("$KANA");
                Emit(Op.Emit, text: name.StartsWith("PRINTD", StringComparison.Ordinal) || name.StartsWith("PRINTFORMD", StringComparison.Ordinal) ? "D" : "", number: name.EndsWith('L') || name.EndsWith('W') ? 1 : 0);
                if (name.EndsWith('W')) Emit(Op.Input, "WAIT"); break;
            case "PRINTSINGLE": case "PRINTSINGLEFORM":
                if (name == "PRINTSINGLEFORM") Format(rest); else Emit(Op.Constant, value: new EraValue(rest));
                Builtin("$PRINT_SINGLE"); break;
            case "PUTFORM":
            {
                string suffix = Temporary(ValueKind.String);
                Reference(Var(suffix)); Format(rest); Emit(Op.Store);
                Reference(Var("SAVEDATA_TEXT")); Expression(Var("SAVEDATA_TEXT")); Expression(Var(suffix)); Emit(Op.Binary, "+"); Emit(Op.Store); break;
            }
            case "INPUTS":
            {
                if (rest.Length == 0) { InvokeBuiltin(name, []); break; }
                var inputArguments = FormattedText.InputArguments(rest, span, IgnoreTripleSymbols);
                InvokeBuiltin(name, inputArguments.Select((argument, i) => i == 0 ? (Expr)new FormattedLiteral(argument)
                    : argument.Length == 0 ? new Omitted() : Parse(argument)).ToArray());
                break;
            }
            case "DT_COLUMN_OPTIONS":
            {
                if (args.Count < 4 || args.Count % 2 != 0) throw new ScriptException("ARGUMENT_COUNT", "列选项需要表名、列名及 DEFAULT 值", span);
                string column = Temporary(ValueKind.String), table = Temporary(ValueKind.String);
                Assign(Var(column), Parse(args[1])); Assign(Var(table), Parse(args[0]));
                for (int i = 2; i < args.Count; i += 2)
                {
                    if (!args[i].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)) throw new ScriptException("DT_OPTION", "未知列选项", span);
                    InvokeBuiltinCore(name, [Var(table), Var(column), Parse(args[i + 1])]);
                }
                break;
            }
            case "SETCOLORBYNAME": case "SETBGCOLORBYNAME":
                Emit(Op.Constant, value: new EraValue(rest)); Builtin(name); break;
            case "PRINTPLAIN": case "PRINTPLAINFORM":
                if (name == "PRINTPLAINFORM") Format(rest); else Emit(Op.Constant, value: new EraValue(rest));
                Builtin("PRINTPLAIN"); break;
            case "PRINTS": case "PRINTSL": case "PRINTSW": case "PRINTSD": case "PRINTSDL": case "PRINTSDW":
                TypedExpression(rest, ValueKind.String);
                Emit(Op.Emit, text: name.StartsWith("PRINTSD", StringComparison.Ordinal) ? "D" : "", number: name.EndsWith('L') || name.EndsWith('W') ? 1 : 0);
                if (name.EndsWith('W')) Emit(Op.Input, "WAIT"); break;
            case "PRINTFORMS": case "PRINTFORMSL": case "PRINTFORMSW": case "PRINTFORMSD": case "PRINTFORMSDL": case "PRINTFORMSDW":
                TypedExpression(rest, ValueKind.String); Emit(Op.Unary, "forms"); Builtin("STRFORM", 1);
                Emit(Op.Emit, text: name.StartsWith("PRINTFORMSD", StringComparison.Ordinal) ? "D" : "", number: name.EndsWith('L') || name.EndsWith('W') ? 1 : 0);
                if (name.EndsWith('W')) Emit(Op.Input, "WAIT"); break;
            case "DRAWLINEFORM":
                Format(rest); Builtin("CUSTOMDRAWLINE"); break;
            case "REUSELASTLINE": Format(rest); Builtin(name); break;
            case "PRINTC": case "PRINTLC": case "PRINTCD": case "PRINTLCD":
            case "PRINTFORMC": case "PRINTFORMLC": case "PRINTFORMCD": case "PRINTFORMLCD":
                if (name.Contains("FORM", StringComparison.Ordinal)) Format(rest); else Emit(Op.Constant, value: new EraValue(rest));
                Emit(Op.Constant, value: new EraValue(name.Contains("LC", StringComparison.Ordinal) ? 0L : 1L));
                Emit(Op.Constant, value: new EraValue(name.EndsWith('D') ? 1L : 0L));
                Builtin("$PRINT_COLUMN"); break;
            case "STRLEN": case "STRLENU": case "STRLENFORM": case "STRLENFORMU":
                Reference(Var("RESULT"));
                if (name.Contains("FORM", StringComparison.Ordinal)) Format(rest); else Emit(Op.Constant, value: new EraValue(rest));
                Builtin(name.EndsWith('U') ? "STRLENSU" : "STRLENS"); Emit(Op.Store); break;
            case "DEBUGPRINT": case "DEBUGPRINTL": case "DEBUGPRINTFORM": case "DEBUGPRINTFORML":
            {
                if (!index.Options.Debug) break;
                if (name.Contains("FORM", StringComparison.Ordinal)) Format(rest); else Emit(Op.Constant, value: new EraValue(rest));
                Builtin("$DEBUG_PRINT");
                break;
            }
            case "DEBUGCLEAR":
                if (!index.Options.Debug) break;
                if (rest.Length != 0) throw new ScriptException("ARGUMENT_COUNT", "DEBUGCLEAR 不接受参数", span);
                Builtin("$DEBUG_CLEAR");
                break;
            case "PRINTBUTTON":
                if (args.Count != 2) throw new ScriptException("ARGUMENT_COUNT", "PRINTBUTTON 需要文字和值", span);
                TypedExpression(args[0], ValueKind.String); Expression(args[1]); Emit(Op.Button); break;
            case "HTML_PRINT":
                if (args.Count is < 1 or > 2) throw new ScriptException("ARGUMENT_COUNT", "HTML_PRINT 参数错误", span);
                TypedExpression(args[0], ValueKind.String);
                if (args.Count == 2) TypedExpression(args[1], ValueKind.Integer);
                else Emit(Op.Constant, value: new EraValue(index.Options.Profile == "minimal-v1" ? 1L : 0L));
                Builtin("HTML_PRINT"); break;
            case "WAIT":
                if (rest.Length > 0) throw new ScriptException("UNSUPPORTED", "暂不支持此输入变体", span);
                Emit(Op.Input, name); break;
            case "CLEARLINE": TypedExpression(rest, ValueKind.Integer); Emit(Op.ClearLine); break;
            case "ALIGNMENT": Emit(Op.Constant, value: new(rest)); Builtin("ALIGNMENT"); break;
            case "CUSTOMDRAWLINE": Emit(Op.Constant, value: new(rest)); Builtin("CUSTOMDRAWLINE"); break;
            case "SETCOLOR":
                InvokeBuiltin(name, args.Select(Parse).ToArray()); break;
            case "POWER":
            {
                if (args.Count != 3 || Parse(args[0]) is not Variable powerTarget || Kind(powerTarget) != ValueKind.Integer)
                    throw new ScriptException("ARGUMENT_COUNT", "POWER 需要整数目标、底数和指数", span);
                if (Resolve(powerTarget.Name).ReadOnly) throw new ScriptException("REFERENCE", "POWER 不接受常量目标", span);
                string result = Temporary(); Reference(Var(result));
                TypedExpression(args[1], ValueKind.Integer); TypedExpression(args[2], ValueKind.Integer);
                Builtin("POWER"); Emit(Op.Store); Assign(powerTarget, Var(result)); break;
            }
            case "RESETCOLOR": Emit(Op.Color, "RESET"); break;
            case "SAVEDATA":
                if (args.Count != 2) throw new ScriptException("ARGUMENT_COUNT", "SAVEDATA 需要槽位和说明", span);
                TypedExpression(args[0], ValueKind.Integer); TypedExpression(args[1], ValueKind.String); Emit(Op.Save); break;
            case "LOADDATA": TypedExpression(rest, ValueKind.Integer); Emit(Op.LoadSave); break;
            case "HTML_TAGSPLIT":
                if (args.Count is < 1 or > 3) throw new ScriptException("ARGUMENT_COUNT", "HTML_TAGSPLIT 参数数目无效", span);
                InvokeBuiltin(name, [Parse(args[0]), args.Count > 1 && args[1].Length > 0 ? Parse(args[1]) : Var("RESULTS"),
                    args.Count > 2 && args[2].Length > 0 ? Parse(args[2]) : Var("RESULT")]);
                break;
            case "PRINT_RECT": case "PRINT_SPACE":
            {
                if (name == "PRINT_SPACE" ? args.Count != 1 : args.Count is not (1 or 4)) throw new ScriptException("ARGUMENT_COUNT", "形状参数数目无效", span);
                Emit(Op.Constant, value: new EraValue("<shape type='" + (name == "PRINT_SPACE" ? "space" : "rect") + "' param='"));
                for (int i = 0; i < args.Count; i++)
                {
                    bool pixels = args[i].EndsWith("px", StringComparison.OrdinalIgnoreCase);
                    TypedExpression(pixels ? args[i][..^2].TrimEnd() : args[i], ValueKind.Integer); Emit(Op.Unary, "text"); Emit(Op.Binary, "+");
                    Emit(Op.Constant, value: new EraValue((pixels ? "px" : "") + (i + 1 < args.Count ? "," : "' color='#"))); Emit(Op.Binary, "+");
                }
                Builtin("GETCOLOR"); Emit(Op.Constant, value: new EraValue("X6")); Builtin("TOSTR"); Emit(Op.Binary, "+");
                Emit(Op.Constant, value: new EraValue("' >")); Emit(Op.Binary, "+");
                Emit(Op.Constant, value: new EraValue(1L)); Builtin("HTML_PRINT"); break;
            }
            case "ARRAYSORT":
            {
                if (args.Count is < 1 or > 4) throw new ScriptException("ARGUMENT_COUNT", "ARRAYSORT 参数数目无效", span);
                string order = args.Count < 2 || args[1].Length == 0 ? "FORWARD" : args[1].ToUpperInvariant();
                if (order is not ("FORWARD" or "BACK")) throw new ScriptException("SORT_ORDER", "排序方向应为 FORWARD 或 BACK", span);
                InvokeBuiltin("ARRAYSORT", [Parse(args[0]), new Literal(new EraValue(order == "FORWARD" ? 1L : 0L)),
                    args.Count > 2 ? Parse(args[2]) : new Omitted(), args.Count > 3 ? Parse(args[3]) : new Omitted()]);
                break;
            }
            case "VARSET":
            {
                if (args.Count is < 1 or > 4 || Parse(args[0]) is not Variable variable) throw new ScriptException("ARGUMENT_COUNT", "VARSET 参数无效", span);
                if (Resolve(variable.Name).ReadOnly) throw new ScriptException("READ_ONLY", "VARSET 不接受常量目标", span);
                Reference(variable);
                string end = Temporary(ValueKind.Integer), start = Temporary(ValueKind.Integer);
                Reference(Var(end));
                if (args.Count < 4 || args[3].Length == 0) Emit(Op.Constant, value: new(-1L)); else TypedExpression(args[3], ValueKind.Integer);
                Emit(Op.Store);
                Reference(Var(start));
                if (args.Count < 3 || args[2].Length == 0) Emit(Op.Constant, value: new(0L)); else TypedExpression(args[2], ValueKind.Integer);
                Emit(Op.Store);
                if (args.Count < 2 || args[1].Length == 0) Emit(Op.Constant, value: EraValue.Zero(Kind(variable))); else TypedExpression(args[1], Kind(variable));
                Expression(Var(start)); Expression(Var(end));
                Builtin("VARSET"); break;
            }
            case "BEGIN":
                if (!Enum.TryParse<SystemMode>(rest, true, out var mode)) throw new ScriptException("FLOW_MODE", "未知系统流程", span);
                Builtin("BEGIN_" + mode.ToString().ToUpperInvariant()); break;
            case "QUIT": Emit(Op.Quit); break;
            default:
                if (builtins.Find(name) is { } commandBuiltin)
                {
                    var commandArguments = rest.Length == 0 ? [] : args.Select(a => a.Length == 0 ? new Omitted() : Parse(a)).ToArray();
                    if (commandBuiltin.ReturnType is { } resultType)
                    {
                        if (commandBuiltin.NumericResult && commandArguments.Any(a => a is not Omitted && Kind(a) == ValueKind.Float)) resultType = ValueKind.Float;
                        Reference(Var(resultType == ValueKind.String ? "RESULTS" : resultType == ValueKind.Float ? "RESULTF" : "RESULT"));
                    }
                    InvokeBuiltin(name, commandArguments);
                    if (commandBuiltin.ReturnType is not null) Emit(Op.Store);
                    break;
                }
                if (LanguageCatalog.KnownUnsupported.Contains(name) || KnownIdentifiers.Commands.Contains(name) || KnownIdentifiers.Functions.Contains(name))
                    throw new ScriptException("UNSUPPORTED_COMMAND", "未支持指令: " + name, span);
                string mutationText = text.TrimEnd();
                bool postfix = mutationText.EndsWith("++", StringComparison.Ordinal) || mutationText.EndsWith("--", StringComparison.Ordinal);
                if (!text.Contains('=') && !postfix && !text.StartsWith("++", StringComparison.Ordinal) && !text.StartsWith("--", StringComparison.Ordinal)) throw new ScriptException("UNKNOWN_COMMAND", "未知指令: " + name, span);
                var tokens = new Tokens(text, span, assignmentPrefix: true);
                Expr targetExpression = ExpressionParser.Parse(tokens, 100);
                if (targetExpression is Mutation mutation) { tokens.Need("<end>"); Expression(mutation); Emit(Op.Pop); break; }
                if (targetExpression is not Variable targetVariable) throw new ScriptException("REFERENCE", "赋值左侧需要变量", span);
                if (tokens.Current.Text == "<end>" && postfix)
                {
                    if (Parse(mutationText[..^2].TrimEnd()) is not Variable incremented) throw new ScriptException("REFERENCE", "自增减左侧需要变量", span);
                    Expression(new Mutation(incremented, mutationText.EndsWith("++", StringComparison.Ordinal) ? 1 : -1, true)); Emit(Op.Pop); break;
                }
                var assignment = tokens.Take(); string operation = assignment.Text;
                int valueStart = assignment.Start + assignment.Length;
                if (operation is not ("=" or "'=" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=" or "<<=" or ">>=")) throw new ScriptException("ASSIGNMENT", "非法赋值运算符", span);
                if (operation is "&=" or "|=" or "^=" or "<<=" or ">>=" && Kind(targetVariable) != ValueKind.Integer)
                    throw new ScriptException("TYPE", "位运算赋值需要整数", span);
                Instruction? directStore;
                if (operation == "=" && Resolve(targetVariable.Name).Kind == ValueKind.String)
                {
                    directStore = PrepareStore(targetVariable);
                    Format(text[valueStart..].TrimStart());
                }
                else
                {
                    tokens = new Tokens(text[valueStart..], span with { Start = span.Start + valueStart, Column = span.Column + valueStart });
                    var values = new List<Expr>();
                    do { values.Add(ExpressionParser.Parse(tokens)); } while (tokens.Match(",") && tokens.Current.Text != "<end>");
                    tokens.Need("<end>");
                    if (values.Count > 1 && operation is not ("=" or "'=")) throw new ScriptException("ASSIGNMENT", "复合赋值不能指定多个值", span);
                    foreach (var valueExpression in values)
                        if (Kind(valueExpression) != Resolve(targetVariable.Name).Kind && !(Resolve(targetVariable.Name).Kind == ValueKind.Float && Kind(valueExpression) == ValueKind.Integer)) throw new ScriptException("TYPE", "赋值类型不匹配", span);
                    if (values.Count > 1)
                    {
                        Reference(targetVariable);
                        foreach (var value in values) Expression(value);
                        Emit(Op.StoreArray, number: values.Count); break;
                    }
                    directStore = PrepareStore(targetVariable);
                    if (operation is not ("=" or "'=")) ReadStoreTarget(directStore);
                    Expression(values[0]);
                }
                if (operation is not ("=" or "'=")) Emit(Op.Binary, operation[..^1]);
                Store(directStore); break;
        }
    }
    private void DataStatement(string text)
    {
        var (name, rest) = LogicalLines.Head(text);
        if (name == "DATALIST")
        { if (dataList is not null) throw new ScriptException("DATA_BLOCK", "DATALIST 不能嵌套", span); dataList = []; return; }
        if (name == "ENDLIST")
        { if (dataList is null) throw new ScriptException("DATA_BLOCK", "缺少 DATALIST", span); dataGroups.Add(dataList); dataList = null; return; }
        if (name is "DATA" or "DATAFORM")
        { if (dataList is not null) dataList.Add((rest, name == "DATAFORM")); else dataGroups.Add([(rest, name == "DATAFORM")]); return; }
        if (name != "ENDDATA" || dataList is not null) throw new ScriptException("DATA_BLOCK", "数据块语句无效", span);
        string command = dataCommand!; dataCommand = null;
        if (command.StartsWith("PRINTDATAK", StringComparison.Ordinal)) throw new ScriptException("UNSUPPORTED_COMMAND", "尚未实现假名转换输出", span);
        if (dataGroups.Count == 0) return;
        string choice = Temporary(); Assign(Var(choice), new Invocation("RAND", [new Literal(new((long)dataGroups.Count))]));
        if (command != "STRDATA" && dataTarget.Length > 0)
        { if (Parse(dataTarget) is not Variable target) throw new ScriptException("REFERENCE", "PRINTDATA 需要结果变量", span); Assign(target, Var(choice)); }
        var ends = new List<int>();
        for (int i = 0; i < dataGroups.Count; i++)
        {
            Expression(new Binary("==", Var(choice), new Literal(new((long)i)))); int skip = Emit(Op.JumpFalse);
            if (command == "STRDATA")
            { if (Parse(dataTarget) is not Variable target || Kind(target) != ValueKind.String) throw new ScriptException("REFERENCE", "STRDATA 需要字符串变量", span); Reference(target); }
            Emit(Op.Constant, value: new EraValue(""));
            for (int j = 0; j < dataGroups[i].Count; j++)
            {
                if (j > 0) { Emit(Op.Constant, value: new EraValue("\n")); Emit(Op.Binary, "+"); }
                var item = dataGroups[i][j]; if (item.Format) Format(item.Text); else Emit(Op.Constant, value: new EraValue(item.Text)); Emit(Op.Binary, "+");
            }
            if (command == "STRDATA") Emit(Op.Store);
            else { Emit(Op.Emit, text: command.StartsWith("PRINTDATAD", StringComparison.Ordinal) ? "D" : "", number: command.EndsWith('L') || command.EndsWith('W') ? 1 : 0); if (command.EndsWith('W')) Emit(Op.Input, "WAIT"); }
            ends.Add(Emit(Op.Jump)); Patch(skip, code.Count);
        }
        foreach (int at in ends) Patch(at, code.Count);
    }
}
