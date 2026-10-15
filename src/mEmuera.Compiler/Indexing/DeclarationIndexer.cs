using System.Collections.Immutable;
using mEmuera.Compiler.Lexing;
using mEmuera.Compiler.Preprocessing;
using mEmuera.Compiler.Syntax;
using mEmuera.Contracts;

namespace mEmuera.Compiler.Indexing;

internal static class DeclarationIndexer
{
    public static EraValue Constant(string expression, IReadOnlyList<VariableDescriptor> variables, SourceSpan span, IBuiltinRegistry? builtins = null, CompileOptions? options = null, MacroEnvironment? macros = null)
    {
        int formatDepth = 0;
        bool ignoreTripleSymbols = options is not null && (options.SystemIgnoreTripleSymbol
            || options.ConfigValues.TryGetValue("SystemIgnoreTripleSymbol", out var configured) && configured.AsInt() != 0);
        EraValue Evaluate(Expr expr) => expr switch
        {
            Literal literal => literal.Value,
            FormattedLiteral format => new(Format(format.Text)),
            Variable variable => Read(variable),
            Unary unary => UnaryValue(unary),
            Conditional choice => Evaluate(Evaluate(choice.Test).AsInt() != 0 ? choice.Yes : choice.No),
            Binary binary => BinaryValue(binary),
            Invocation { Name: "VARSIZE", Arguments.Count: 1 or 2 } call => VariableSize(call),
            Invocation { Name: "GETNUM", Arguments.Count: 2 or 3 } call => NameIndex(call),
            Invocation { Name: "ERDNAME", Arguments.Count: 2 or 3 } call => ReverseName(call),
            Invocation { Name: "STRFORM", Arguments.Count: 1 } call => new(Format(Evaluate(call.Arguments[0]).AsString())),
            Invocation call when builtins is not null => builtins.EvaluateConstant(call.Name,
                ConstantArguments(call, builtins.Find(call.Name)).Select(Evaluate).ToImmutableArray()),
            _ => throw new ScriptException("CONSTANT", "声明需要常量表达式", span)
        };
        IReadOnlyList<Expr> ConstantArguments(Invocation call, BuiltinSignature? signature)
        {
            if (signature is null) return call.Arguments;
            if (call.Arguments.Count > signature.Parameters.Length && signature.Variadic is null) throw new ScriptException("ARGUMENT_COUNT", "常量函数参数数量不符", span);
            var arguments = new Expr[Math.Max(call.Arguments.Count, signature.Parameters.Length)];
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i < call.Arguments.Count && call.Arguments[i] is not Omitted) { arguments[i] = call.Arguments[i]; continue; }
                arguments[i] = signature.Parameter(i).Default is { } value ? new Literal(value)
                    : throw new ScriptException("ARGUMENT_COUNT", "常量函数参数不能省略", span);
            }
            return arguments;
        }
        EraValue VariableSize(Invocation call)
        {
            string name = Evaluate(call.Arguments[0]).AsString();
            var target = variables.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ScriptException("CONSTANT_UNDEFINED", "变量尺寸尚不可用: " + name, span);
            if (target.Reference) throw new ScriptException("CONSTANT", "引用数组尺寸不能用于常量", span);
            long axis = call.Arguments.Count == 2 && call.Arguments[1] is not Omitted ? Evaluate(call.Arguments[1]).AsInt() : 0;
            if (axis < 0 || axis >= target.Shape.Length) throw new ScriptException("RANK", "VARSIZE 维数越界", span);
            return new((long)target.Shape[(int)axis]);
        }
        string Format(string text)
        {
            if (++formatDepth > 64) throw new ScriptException("FORMAT", "格式字符串嵌套过深", span);
            try { return Parts(FormattedText.Parse(text, span, ignoreTripleSymbols)); }
            finally { formatDepth--; }
        }
        string Parts(IReadOnlyList<FormatPart> parts)
        {
            var result = new System.Text.StringBuilder();
            foreach (var part in parts)
            {
                if (part is FormatText text) { result.Append(text.Text); continue; }
                if (part is FormatChoice choice)
                {
                    result.Append(Parts(Evaluate(ExpressionParser.Parse(macros?.Expand(choice.Condition, span) ?? choice.Condition, span)).AsInt() != 0 ? choice.Yes : choice.No));
                    continue;
                }
                var field = (FormatField)part;
                var arguments = FormattedText.FieldArguments(field, span, fragment => macros?.Expand(fragment, span) ?? fragment);
                var value = Evaluate(ExpressionParser.Parse(arguments.Expression, span));
                if (field.String ? value.Kind != ValueKind.String : value.Kind == ValueKind.String)
                    throw new ScriptException("TYPE", "格式字段类型不匹配", span);
                string output = value.ToString();
                if (arguments.Width is { } widthExpression)
                {
                    long width = Evaluate(ExpressionParser.Parse(widthExpression, span)).AsInt();
                    if (field.String) width = Math.Max(0, width);
                    output = (builtins ?? throw new ScriptException("CONSTANT", "常量插值宽度需要文本度量", span)).EvaluateConstant("$FORMAT_FIELD",
                        [new(output), new(width), new(arguments.Left ? 1L : 0L)]).AsString();
                }
                result.Append(output);
            }
            return result.ToString();
        }
        EraValue NameIndex(Invocation call)
        {
            string name = (call.Arguments[0] is Variable variable ? variable.Name : Evaluate(call.Arguments[0]).AsString()).ToUpperInvariant();
            var target = variables.FirstOrDefault(v => v.Name == name) ?? throw new ScriptException("CONSTANT_UNDEFINED", "变量名称表尚不可用: " + name, span);
            long? dimension = call.Arguments.Count == 3 && call.Arguments[2] is not Omitted ? Evaluate(call.Arguments[2]).AsInt() : null;
            string key = Evaluate(call.Arguments[1]).AsString();
            if (key.Length == 0) return new(-1L);
            var system = target.Local ? null : options?.SystemVariables.FirstOrDefault(v => v.Name == name);
            if (system is not null)
            {
                if (NameQuerySupport.IsCharacterTable(name) && options!.CharacterNameIndices.TryGetValue(key, out long character)) return new(character);
                if (NameQuerySupport.HasSystemTable(name) && system.NameIndices.TryGetValue(key, out int number)) return new((long)number);
                return new(-1L);
            }
            if (target.Reference) return new(-1L);
            int axis;
            if (target.Shape.Length == 1)
            {
                if (dimension is not null) return new(-1L);
                axis = 0;
            }
            else
            {
                if (dimension is not { } d || d < 1 || d > target.Shape.Length) return new(-1L);
                axis = (int)d - 1;
            }
            var names = target.NamesAt(axis);
            if (options is not null)
            {
                string table = dimension is { } d ? name + "@" + d.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
                if (!options.UserNameTables.TryGetValue(table, out names)) return new(-1L);
            }
            if (names.TryGetValue(key, out int index) && index >= 0 && index < target.Shape[axis]) return new((long)index);
            return new(-1L);
        }
        EraValue ReverseName(Invocation call)
        {
            string name = (call.Arguments[0] is Variable variable ? variable.Name : Evaluate(call.Arguments[0]).AsString()).ToUpperInvariant();
            var target = variables.FirstOrDefault(v => v.Name == name) ?? throw new ScriptException("CONSTANT_UNDEFINED", "变量名称表尚不可用: " + name, span);
            long? dimension = call.Arguments.Count == 3 && call.Arguments[2] is not Omitted ? Evaluate(call.Arguments[2]).AsInt() : null;
            long index = Evaluate(call.Arguments[1]).AsInt();
            if (index < 0 || target.Reference || !target.Local && options?.SystemVariables.Any(v => v.Name == name) == true) return new("");
            int axis;
            if (target.Shape.Length == 1)
            {
                if (dimension is not null) return new("");
                axis = 0;
            }
            else
            {
                if (dimension is not { } d || d < 1 || d > target.Shape.Length) return new("");
                axis = (int)d - 1;
            }
            if (index >= target.Shape[axis]) return new("");
            var names = target.NamesAt(axis);
            if (options is not null)
            {
                string table = dimension is { } d ? name + "@" + d.ToString(System.Globalization.CultureInfo.InvariantCulture) : name;
                if (!options.UserNameTables.TryGetValue(table, out names)) return new("");
            }
            return new(names.FirstOrDefault(pair => pair.Value == index).Key ?? "");
        }
        EraValue Read(Variable variable)
        {
            var target = variables.FirstOrDefault(v => v.Name == variable.Name);
            if (target is null || !target.ReadOnly) throw new ScriptException("CONSTANT_UNDEFINED", "常量未定义: " + variable.Name, span);
            int at = ConstantOffset(variable, target);
            return target.Initial.IsDefaultOrEmpty || at >= target.Initial.Length ? EraValue.Zero(target.Kind) : target.Initial[at];
        }
        int ConstantOffset(Variable variable, VariableDescriptor target)
            => target.Offset(variable.Indices.Select((subscript, dimension) =>
            {
                if (target.Scope == VariableScope.Character && variable.Indices.Count == target.Shape.Length + 1) dimension--;
                var names = target.NamesAt(dimension);
                if (dimension >= 0 && subscript is Variable name && !name.ExplicitIndices && !variables.Any(v => v.Name == name.Name)
                    && names.TryGetValue(name.Keyword ?? name.Name, out int keyword)) return (long)keyword;
                var kind = ConstantKind(subscript);
                if (kind is not (ValueKind.Integer or ValueKind.String) || dimension < 0 && kind != ValueKind.Integer) throw new ScriptException("TYPE", "数组下标需要整数或名称", span);
                var value = Evaluate(subscript);
                if (kind == ValueKind.Integer) return value.AsInt();
                return names.TryGetValue(value.AsString(), out int index) ? index : throw new ScriptException("BOUNDS", "数组名称下标不存在: " + target.Name, span);
            }).ToArray());
        EraValue UnaryValue(Unary expr)
        {
            var value = Evaluate(expr.Operand);
            if (value.Kind == ValueKind.Float && expr.Operator is "+" or "-") return new(expr.Operator == "+" ? value.AsFloat() : -value.AsFloat());
            long number = value.AsInt();
            return new(expr.Operator switch { "+" => number, "-" => checked(-number), "~" => ~number, "!" => number == 0 ? 1L : 0L,
                _ => throw new ScriptException("CONSTANT", "常量运算无效", span) });
        }
        ValueKind ConstantKind(Expr expr)
        {
            switch (expr)
            {
                case Literal literal: return literal.Value.Kind;
                case Variable variable:
                {
                    var target = variables.FirstOrDefault(v => v.Name == variable.Name);
                    if (target is null || !target.ReadOnly) throw new ScriptException("CONSTANT_UNDEFINED", "常量未定义: " + variable.Name, span);
                    ConstantOffset(variable, target);
                    return target.Kind;
                }
                case Unary unary:
                {
                    var kind = ConstantKind(unary.Operand);
                    if (kind == ValueKind.Integer || kind == ValueKind.Float && unary.Operator is "+" or "-") return kind;
                    throw new ScriptException("TYPE", "常量一元运算类型无效", span);
                }
                case Binary binary:
                {
                    var left = ConstantKind(binary.Left); var right = ConstantKind(binary.Right);
                    if (binary.Operator == "*" && (left, right) is (ValueKind.String, ValueKind.Integer) or (ValueKind.Integer, ValueKind.String)) return ValueKind.String;
                    bool comparison = binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=";
                    if (left == ValueKind.String || right == ValueKind.String)
                    {
                        if (left == right && (binary.Operator == "+" || comparison)) return comparison ? ValueKind.Integer : ValueKind.String;
                        throw new ScriptException("TYPE", "常量运算类型不匹配", span);
                    }
                    if (left == ValueKind.Float || right == ValueKind.Float)
                    {
                        if (comparison) return ValueKind.Integer;
                        if (binary.Operator is "+" or "-" or "*" or "/" or "%") return ValueKind.Float;
                        throw new ScriptException("TYPE", "浮点常量运算符无效", span);
                    }
                    return ValueKind.Integer;
                }
                case Conditional conditional:
                {
                    if (ConstantKind(conditional.Test) != ValueKind.Integer) throw new ScriptException("TYPE", "条件需要整数", span);
                    return ConstantKind(Evaluate(conditional.Test).AsInt() != 0 ? conditional.Yes : conditional.No);
                }
                case FormattedLiteral format: return FormatKind(FormattedText.Parse(format.Text, span, ignoreTripleSymbols));
                case Invocation { Name: "GETNUM" or "ERDNAME", Arguments.Count: 2 or 3 } call:
                {
                    if (call.Arguments[0] is Variable name)
                    {
                        if (!variables.Any(v => v.Name == name.Name)) throw new ScriptException("CONSTANT_UNDEFINED", "变量名称表尚不可用: " + name.Name, span);
                    }
                    else if (ConstantKind(call.Arguments[0]) != ValueKind.String) throw new ScriptException("TYPE", "变量名称需要字符串", span);
                    if (ConstantKind(call.Arguments[1]) != (call.Name == "GETNUM" ? ValueKind.String : ValueKind.Integer)) throw new ScriptException("TYPE", "名称查询参数类型不匹配", span);
                    if (call.Arguments.Count == 3 && call.Arguments[2] is not Omitted && ConstantKind(call.Arguments[2]) != ValueKind.Integer) throw new ScriptException("TYPE", "名称查询维数需要整数", span);
                    return call.Name == "GETNUM" ? ValueKind.Integer : ValueKind.String;
                }
                case Invocation { Name: "STRFORM", Arguments.Count: 1 } call:
                    if (ConstantKind(call.Arguments[0]) != ValueKind.String) throw new ScriptException("TYPE", "STRFORM 需要字符串", span);
                    return FormatKind(FormattedText.Parse(Evaluate(call.Arguments[0]).AsString(), span, ignoreTripleSymbols));
                case Invocation { Name: "VARSIZE", Arguments.Count: 1 or 2 } call:
                    if (ConstantKind(call.Arguments[0]) != ValueKind.String) throw new ScriptException("TYPE", "VARSIZE 需要变量名称", span);
                    if (call.Arguments.Count == 2 && call.Arguments[1] is not Omitted && ConstantKind(call.Arguments[1]) != ValueKind.Integer) throw new ScriptException("TYPE", "VARSIZE 维数需要整数", span);
                    VariableSize(call);
                    return ValueKind.Integer;
                case Invocation call when builtins?.CanEvaluateConstant(call.Name) == true && builtins!.Find(call.Name) is { ReturnType: { } result } signature:
                {
                    var kinds = ConstantArguments(call, signature).Select(ConstantKind).ToArray();
                    for (int i = 0; i < kinds.Length; i++)
                        if (signature.Parameter(i).Kind is { } expected && kinds[i] != expected) throw new ScriptException("TYPE", "常量函数参数类型不匹配", span);
                    if (signature.SameType && kinds.Any(k => k != kinds[0]) || signature.NumericResult && kinds.Any(k => k == ValueKind.String)) throw new ScriptException("TYPE", "常量函数参数类型不匹配", span);
                    return signature.NumericResult && kinds.Contains(ValueKind.Float) ? ValueKind.Float : result;
                }
                default: throw new ScriptException("CONSTANT", "声明需要常量表达式", span);
            }
        }
        ValueKind FormatKind(IReadOnlyList<FormatPart> parts)
        {
            if (++formatDepth > 64) throw new ScriptException("FORMAT", "格式字符串嵌套过深", span);
            try
            {
                foreach (var part in parts)
                {
                    if (part is FormatText) continue;
                    if (part is FormatChoice choice)
                    {
                        var condition = ExpressionParser.Parse(macros?.Expand(choice.Condition, span) ?? choice.Condition, span);
                        if (ConstantKind(condition) != ValueKind.Integer) throw new ScriptException("TYPE", "条件需要整数", span);
                        FormatKind(Evaluate(condition).AsInt() != 0 ? choice.Yes : choice.No); continue;
                    }
                    var field = (FormatField)part;
                    var arguments = FormattedText.FieldArguments(field, span, fragment => macros?.Expand(fragment, span) ?? fragment);
                    var kind = ConstantKind(ExpressionParser.Parse(arguments.Expression, span));
                    if (field.String ? kind != ValueKind.String : kind == ValueKind.String) throw new ScriptException("TYPE", "格式字段类型不匹配", span);
                    if (arguments.Width is { } width && ConstantKind(ExpressionParser.Parse(width, span)) != ValueKind.Integer) throw new ScriptException("TYPE", "插值宽度需要整数", span);
                }
                return ValueKind.String;
            }
            finally { formatDepth--; }
        }
        EraValue BinaryValue(Binary expr)
        {
            if (expr.Operator is "&&" or "||" or "^^" or "!&" or "!|")
            {
                // 常量资格与类型先校验；逻辑值仍按运行入口的短路规则计算。
                if (ConstantKind(expr.Left) != ValueKind.Integer || ConstantKind(expr.Right) != ValueKind.Integer) throw new ScriptException("TYPE", "逻辑运算需要整数", span);
                bool left = Evaluate(expr.Left).AsInt() != 0;
                bool result = expr.Operator switch
                {
                    "&&" => left && Evaluate(expr.Right).AsInt() != 0,
                    "||" => left || Evaluate(expr.Right).AsInt() != 0,
                    "!&" => !left || Evaluate(expr.Right).AsInt() == 0,
                    "!|" => !left && Evaluate(expr.Right).AsInt() == 0,
                    _ => left ^ (Evaluate(expr.Right).AsInt() != 0)
                };
                return new(result ? 1L : 0L);
            }
            var a = Evaluate(expr.Left); var b = Evaluate(expr.Right);
            if (expr.Operator == "*" && a.Kind == ValueKind.Integer && b.Kind == ValueKind.String) (a, b) = (b, a);
            if (expr.Operator == "*" && a.Kind == ValueKind.String && b.Kind == ValueKind.Integer)
            {
                long count = b.AsInt();
                if (count < 0 || count > 16_000_000 || a.Text.Length * count > 16_000_000) throw new ScriptException("STRING_LIMIT", "字符串重复长度超限", span);
                return new(string.Concat(Enumerable.Repeat(a.Text, (int)count)));
            }
            if (a.Kind != ValueKind.String && b.Kind != ValueKind.String && (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float))
            { a = a.ConvertTo(ValueKind.Float); b = b.ConvertTo(ValueKind.Float); }
            if (a.Kind != b.Kind) throw new ScriptException("TYPE", "常量运算类型不匹配", span);
            if (a.Kind == ValueKind.String)
            {
                if (expr.Operator == "+") return new(a.Text + b.Text);
                int order = string.Compare(a.Text, b.Text, StringComparison.Ordinal);
                return new(expr.Operator switch { "==" => order == 0 ? 1L : 0L, "!=" => order != 0 ? 1L : 0L, ">" => order > 0 ? 1L : 0L,
                    "<" => order < 0 ? 1L : 0L, ">=" => order >= 0 ? 1L : 0L, "<=" => order <= 0 ? 1L : 0L,
                    _ => throw new ScriptException("TYPE", "字符串常量运算符无效", span) });
            }
            if (a.Kind == ValueKind.Float)
            {
                double left = a.AsFloat(), right = b.AsFloat();
                if (expr.Operator is "==" or "!=" or ">" or "<" or ">=" or "<=")
                    return new(expr.Operator switch { "==" => left == right ? 1L : 0L, "!=" => left != right ? 1L : 0L, ">" => left > right ? 1L : 0L,
                        "<" => left < right ? 1L : 0L, ">=" => left >= right ? 1L : 0L, _ => left <= right ? 1L : 0L });
                double result = expr.Operator switch { "+" => left + right, "-" => left - right, "*" => left * right, "/" => left / right, "%" => left % right,
                    _ => throw new ScriptException("TYPE", "浮点常量运算符无效", span) };
                if (!double.IsFinite(result)) throw new OverflowException();
                return new(result);
            }
            long x = a.AsInt(), y = b.AsInt();
            return new(expr.Operator switch { "+" => checked(x + y), "-" => checked(x - y), "*" => checked(x * y), "/" => x / y, "%" => x % y,
                "<<" => x << (int)y, ">>" => x >> (int)y, "&" => x & y, "|" => x | y, "^" => x ^ y,
                "==" => x == y ? 1L : 0L, "!=" => x != y ? 1L : 0L, ">" => x > y ? 1L : 0L, "<" => x < y ? 1L : 0L,
                ">=" => x >= y ? 1L : 0L, "<=" => x <= y ? 1L : 0L,
                _ => throw new ScriptException("CONSTANT", "不支持的常量运算", span) });
        }
        try { return Evaluate(ExpressionParser.Parse(expression, span)); }
        catch (Exception e) when (e is OverflowException or DivideByZeroException) { throw new ScriptException("CONSTANT_RANGE", "声明常量溢出或除零", span); }
    }
    public static VariableDescriptor Parse(string text, bool local, SourceSpan span, IReadOnlyList<VariableDescriptor> variables, bool legacy = false, IBuiltinRegistry? builtins = null, CompileOptions? options = null, MacroEnvironment? macros = null)
    {
        var (kind, rest) = LogicalLines.Head(text);
        var attributes = new HashSet<string>();
        while (true)
        {
            var (word, tail) = LogicalLines.Head(rest);
            if (word is not ("SAVEDATA" or "REF" or "STATIC" or "DYNAMIC" or "CONST" or "GLOBAL" or "CHARADATA")) break;
            if (!attributes.Add(word)) throw new ScriptException("DECLARATION_ATTRIBUTE", "声明属性重复", span);
            rest = tail;
        }
        if (attributes.Contains("STATIC") && attributes.Contains("DYNAMIC") || attributes.Contains("REF") && attributes.Overlaps(["STATIC", "CONST", "SAVEDATA", "GLOBAL", "CHARADATA"]))
            throw new ScriptException("DECLARATION_ATTRIBUTE", "声明属性冲突", span);
        if (!legacy && (local && attributes.Overlaps(["SAVEDATA", "GLOBAL", "CHARADATA"])
            || !local && attributes.Overlaps(["STATIC", "DYNAMIC"])
            || attributes.Contains("CONST") && attributes.Overlaps(["SAVEDATA", "GLOBAL", "CHARADATA", "DYNAMIC"])
            || attributes.Contains("CHARADATA") && attributes.Contains("GLOBAL")))
            throw new ScriptException("DECLARATION_ATTRIBUTE", "声明属性不适用于此作用域或组合", span);
        int equal = AssignmentOffset(rest, span); string declaration = equal < 0 ? rest : rest[..equal];
        var parts = LogicalLines.Split(declaration); string name = parts[0].Trim().ToUpperInvariant();
        if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c < 128 && !char.IsLetterOrDigit(c) && c != '_')) throw new ScriptException("DECLARATION", "变量名称无效", span);
        ValueKind type = kind switch { "#DIMS" => ValueKind.String, "#DIMF" => ValueKind.Float, _ => ValueKind.Integer };
        var initial = equal < 0 ? ImmutableArray<EraValue>.Empty : LogicalLines.Split(rest[(equal + 1)..].TrimEnd().TrimEnd(',')).Select(p => Constant(p, variables, span, builtins, options, macros)).ToImmutableArray();
        initial = initial.Select(v => v.ConvertTo(type)).ToImmutableArray();
        var dimensions = parts.Count == 1 ? ImmutableArray.Create(Math.Max(1, initial.Length)) : parts.Skip(1).Select(p => p.Length == 0 && attributes.Contains("REF") ? 0 : checked((int)Constant(p, variables, span, builtins, options, macros).AsInt())).ToImmutableArray();
        if (attributes.Contains("REF"))
        {
            if (dimensions.Any(n => n != 0) && parts.Count > 1 || initial.Length > 0) throw new ScriptException("DECLARATION_SIZE", "引用不能指定容量或初始值", span);
            dimensions = dimensions.Select(_ => 1).ToImmutableArray();
        }
        if (dimensions.Length > 3 || dimensions.Any(n => n <= 0)) throw new ScriptException("DECLARATION_SIZE", "数组维度或长度无效", span);
        long length = 1;
        foreach (int dimension in dimensions) { length *= dimension; if (length > 16_000_000) throw new ScriptException("DECLARATION_SIZE", "数组总长度超限", span); }
        if (initial.Length > length) throw new ScriptException("DECLARATION_SIZE", "初始值超过数组容量", span);
        var scope = attributes.Contains("CHARADATA") ? VariableScope.Character : !local ? VariableScope.Global : attributes.Contains("DYNAMIC") || attributes.Contains("REF") || legacy && !attributes.Contains("STATIC") ? VariableScope.CallLocal : VariableScope.FunctionStatic;
        return new(name, name, type, (int)length, attributes.Contains("SAVEDATA"), local, attributes.Contains("REF"), dimensions, initial, scope, attributes.Contains("CONST"), attributes.Contains("GLOBAL"))
        { ReferenceRank = attributes.Contains("REF") ? dimensions.Length : null };
    }
    private static int AssignmentOffset(string text, SourceSpan span)
    {
        var tokens = new Tokens(text, span, assignmentPrefix: true);
        int depth = 0;
        while (tokens.Current.Text != "<end>")
        {
            var token = tokens.Take();
            if (token.Text == "(") depth++;
            else if (token.Text == ")") depth--;
            else if (token.Text == "=" && depth == 0) return token.Start;
        }
        return -1;
    }
}
