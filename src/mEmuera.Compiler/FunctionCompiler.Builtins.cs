using System.Collections.Immutable;
using mEmuera.Compiler.Syntax;
using mEmuera.Contracts;

namespace mEmuera.Compiler;

internal sealed partial class FunctionCompiler
{
    private void BindBuiltin(BuiltinSignature signature, IReadOnlyList<Expr> arguments)
    {
        arguments = TrimTrailingArgument(signature, arguments);
        if (arguments.Count > signature.Parameters.Length && signature.Variadic is null || signature.Parameters.Skip(arguments.Count).Any(p => p.Default is null))
            throw new ScriptException("ARGUMENT_COUNT", "内建参数数目不符", span);
        if (signature.Name is "SETCOLOR" or "SETBGCOLOR" && arguments.Count is not (1 or 3)
            || signature.Name == "GCLEAR" && arguments.Count is not (2 or 6)
            || signature.Name == "SPRITECREATE" && arguments.Count is not (2 or 6 or 8 or 10)
            || signature.Name == "GDRAWSPRITE" && arguments.Count is not (2 or 4 or 6 or 7))
            throw new ScriptException("ARGUMENT_COUNT", signature.Name + " 参数数量错误", span);
        for (int i = 0; i < arguments.Count; i++)
        {
            var parameter = signature.Parameter(i);
            if (signature.SameType && Kind(arguments[i]) != Kind(arguments[0])) throw new ScriptException("TYPE", "参数类型必须一致", span);
            if (signature.NumericResult && Kind(arguments[i]) == ValueKind.String) throw new ScriptException("TYPE", "参数需要数值", span);
            if (arguments[i] is Omitted && parameter.Default is not null) continue;
            if (parameter.VariableName)
            {
                if (arguments[i] is not Variable variable) throw new ScriptException("VARIABLE", "参数需要变量名称", span);
                var specification = Resolve(variable.Name);
                if (signature.Name == "CVARSET" && specification.ReadOnly) throw new ScriptException("READ_ONLY", "内建输出参数不可为常量", span);
                continue;
            }
            if (parameter.Reference && arguments[i] is not Variable) throw new ScriptException("REFERENCE", "内建参数需要变量引用", span);
            if (parameter.Kind is { } expected && Kind(arguments[i]) != expected) throw new ScriptException("TYPE", "内建参数类型不匹配", span);
            if (arguments[i] is Variable reference)
            {
                bool write = i == 0 && signature.Name is "SETBIT" or "CLEARBIT" or "INVERTBIT" or "TIMES" or "VARSET" or "ARRAYSHIFT" or "ARRAYREMOVE" or "ARRAYSORT"
                    || signature.Name == "SWAP" || signature.Name == "SPLIT" && i is 2 or 3 || signature.Name == "HTML_TAGSPLIT" && i is 1 or 2
                    || signature.Name == "REGEXPMATCH" && arguments.Count == 4 && i is 2 or 3
                    || signature.Name is "XML_GET" or "XML_GET_BYNAME" && i == 2 && Kind(reference) == ValueKind.String;
                bool matrix = signature.Name == "GDRAWG" && i == 10 || signature.Name == "GDRAWSPRITE" && i == 6 || signature.Name == "SETIMAGELAYERL" && i == 7;
                bool vectorOutput = signature.Name == "HTML_TAGSPLIT" && i == 1 || signature.Name == "REGEXPMATCH" && arguments.Count == 4 && i == 3
                    || signature.Name is "XML_GET" or "XML_GET_BYNAME" && i == 2 && Kind(reference) == ValueKind.String;
                if (!write && !matrix && !vectorOutput) continue;
                if (reference.Name is "RAND" or "CHARANUM" or "LINECOUNT" or "DRAWLINESTR") throw new ScriptException("REFERENCE", "计算变量不能作为内建输出引用", span);
                var specification = Resolve(reference.Name);
                if (write && specification.ReadOnly) throw new ScriptException("READ_ONLY", "内建输出参数不可为常量", span);
                if (matrix && specification.Shape.Length is not (2 or 3)) throw new ScriptException("RANK", "颜色矩阵需要二维或三维整数数组", span);
                if (vectorOutput && (specification.Shape.Length != 1 || specification.Scope == VariableScope.Character))
                    throw new ScriptException("REFERENCE", "内建输出需要非角色一维字符串数组", span);
            }
            else if (signature.Name == "REGEXPMATCH" && arguments.Count == 4 && i is 2 or 3)
                throw new ScriptException("REFERENCE", "REGEXPMATCH 输出需要变量", span);
        }
    }
    private void InvokeBuiltin(string name, IReadOnlyList<Expr> arguments)
    {
        var signature = builtins.Find(name)!; arguments = TrimTrailingArgument(signature, arguments); BindBuiltin(signature, arguments);
        if (InvokeGraphicsBuiltin(name, arguments)) return;
        if (name is "GETKEY" or "GETKEYTRIGGERED")
        {
            InvokeBuiltinCore("$KEY_INPUT_ACTIVE", []); int inactive = Emit(Op.JumpFalse);
            InvokeBuiltinCore(name, arguments); int end = Emit(Op.Jump);
            Patch(inactive, code.Count); Emit(Op.Constant, value: new(0L)); Patch(end, code.Count); return;
        }
        if (name == "GETSOUNDORBGMINFO")
        {
            var lowered = LowerBuiltinArguments(arguments, 0);
            InvokeBuiltinCore("$MEDIA_CHANNEL", [lowered[0]]); int invalid = Emit(Op.JumpFalse);
            InvokeBuiltinCore(name, lowered); int end = Emit(Op.Jump);
            Patch(invalid, code.Count); Emit(Op.Constant, value: new(0L)); Patch(end, code.Count); return;
        }
        if (name == "HTML_STRINGLINES")
        {
            string text = Temporary(ValueKind.String), remainder = Temporary(ValueKind.String), count = Temporary();
            Assign(Var(text), arguments[0]); Assign(Var(count), new Literal(new(0L))); int loop = code.Count;
            Expression(new Binary("!=", Var(text), new Literal(new("")))); int end = Emit(Op.JumpFalse);
            Reference(Var(remainder)); InvokeBuiltinCore("$HTML_LINE_SPLIT", [Var(text), arguments[1]]); Emit(Op.Store);
            InvokeBuiltinCore("$HTML_LINE_PROGRESS", [Var(text), Var(remainder), Var(count)]);
            Assign(Var(text), Var(remainder)); Assign(Var(count), new Binary("+", Var(count), new Literal(new(1L))));
            Emit(Op.Jump, number: loop); Patch(end, code.Count); Expression(Var(count)); return;
        }
        if (name == "HTML_STRINGLEN")
        {
            string width = Temporary(); Reference(Var(width)); InvokeBuiltinCore("$HTML_MEASURE_PIXELS", [arguments[0]]); Emit(Op.Store);
            InvokeBuiltinCore("$HTML_MEASURE_UNITS", [Var(width), arguments.Count > 1 && arguments[1] is not Omitted ? arguments[1] : new Literal(new(0L))]); return;
        }
        if (name == "VARSETEX")
        {
            var lowered = new List<Expr> { arguments[0] };
            for (int i = 1; i < 5; i++) lowered.Add(new Literal(new EraValue(i < arguments.Count && arguments[i] is not Omitted ? BuiltinExpressionText(arguments[i]) : "")));
            InvokeBuiltinCore("$VARSETEX_EXPRESSIONS", lowered); return;
        }
        if (name is "ADDCHARA" or "ADDCOPYCHARA" or "ADDCHARACOPY")
        {
            foreach (var argument in arguments) InvokeBuiltinCore(name, [argument]);
            return;
        }
        if (name == "PICKUPCHARA")
        {
            string count = Temporary(); Assign(Var(count), new Invocation("CHARANUM", []));
            foreach (var argument in arguments)
            {
                if (argument is Variable variable && variable.Name is not ("RAND" or "CHARANUM" or "LINECOUNT" or "DRAWLINESTR")) Reference(variable);
                else Expression(argument);
                Emit(Op.Duplicate); Expression(Var(count)); Builtin("$PICKUP_ARGUMENT");
            }
            Builtin(name, arguments.Count); return;
        }
        if (name is "SETBIT" or "CLEARBIT" or "INVERTBIT" or "TIMES")
        {
            var lowered = LowerBuiltinArguments(arguments, 1);
            if (name != "TIMES") InvokeBuiltinCore("$BIT_INDEX", [lowered[1]]);
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name == "COLOR_FROMRGB")
        {
            var lowered = arguments.ToArray();
            for (int i = 0; i < lowered.Length; i++)
            {
                string component = Temporary(); Assign(Var(component), lowered[i]); lowered[i] = Var(component);
                InvokeBuiltinCore("$RGB_COMPONENT", [lowered[i]]);
            }
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name == "CONVERT")
        {
            var lowered = LowerBuiltinArguments(arguments, 1);
            InvokeBuiltinCore("$CONVERT_BASE", [lowered[1]]);
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name == "ENCODETOUNI")
        {
            string text = Temporary(ValueKind.String); Assign(Var(text), arguments[0]);
            Expression(new Binary("!=", Var(text), new Literal(new("")))); int empty = Emit(Op.JumpFalse);
            InvokeBuiltinCore(name, [Var(text), arguments.Count > 1 ? arguments[1] : new Omitted()]);
            int end = Emit(Op.Jump); Patch(empty, code.Count); Emit(Op.Constant, value: new(-1L)); Patch(end, code.Count); return;
        }
        if (name is "GETNUM" or "ERDNAME")
        { InvokeBuiltinCore(name, LowerBuiltinArguments(arguments, 2)); return; }
        if (name is "GETVAR" or "GETVARS")
        { InvokeBuiltinCore(name, LowerBuiltinArguments(arguments, 1)); return; }
        if (name == "STRCOUNT")
        {
            var lowered = LowerBuiltinArguments(arguments, 1);
            InvokeBuiltinCore("$REGEX_PATTERN", [lowered[1]]);
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name is "MAP_GET" or "MAP_HAS" or "MAP_SET" or "MAP_REMOVE")
        {
            string map = Temporary(ValueKind.String); Assign(Var(map), arguments[0]);
            InvokeBuiltinCore("MAP_EXIST", [Var(map)]); int missing = Emit(Op.JumpFalse);
            var lowered = arguments.ToArray(); lowered[0] = Var(map); InvokeBuiltinCore(name, lowered);
            int end = Emit(Op.Jump); Patch(missing, code.Count);
            Emit(Op.Constant, value: name == "MAP_GET" ? new EraValue("") : new EraValue(-1L)); Patch(end, code.Count); return;
        }
        if (name == "LOADTEXT")
        { InvokeBuiltinCore(name, LowerBuiltinArguments(arguments, 1, 2)); return; }
        if (name == "SAVETEXT")
        { InvokeBuiltinCore(name, LowerBuiltinArguments(arguments, 0, 2, 3)); return; }
        if (name == "STRJOIN")
        { InvokeBuiltinCore(name, LowerBuiltinArguments(arguments, 1, 2, 3)); return; }
        if (name == "REPLACE")
        {
            var prepared = LowerBuiltinArguments(arguments, 0, 3).ToList();
            Expr rawMode = prepared.Count > 3 && prepared[3] is not Omitted ? prepared[3] : new Literal(new(0L));
            string normalized = Temporary(); Assign(Var(normalized), new Invocation("$INT32_MODE", [rawMode]));
            if (prepared.Count > 3) prepared[3] = Var(normalized); else prepared.Add(Var(normalized));
            var lowered = LowerBuiltinArguments(prepared, 1); Expr mode = Var(normalized);
            Expression(new Binary("==", mode, new Literal(new(2L)))); int regex = Emit(Op.JumpFalse);
            int literal = Emit(Op.Jump); Patch(regex, code.Count); InvokeBuiltinCore("$REGEX_PATTERN", [lowered[1]]); Patch(literal, code.Count);
            if (lowered[2] is Variable replacement)
            {
                Expression(new Binary("==", mode, new Literal(new(1L)))); int scalar = Emit(Op.JumpFalse);
                Expression(lowered[0]); Expression(lowered[1]); ArrayArgumentReference(replacement); Expression(mode); Builtin(name);
                int end = Emit(Op.Jump); Patch(scalar, code.Count); InvokeBuiltinCore(name, lowered); Patch(end, code.Count);
            }
            else InvokeBuiltinCore(name, lowered);
            return;
        }
        if (name == "REGEXPMATCH")
        {
            var lowered = LowerBuiltinArguments(arguments, 0, 1);
            InvokeBuiltinCore("$REGEXPMATCH_CHECK", [lowered[0], lowered[1]]);
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name is "XML_GET" or "XML_GET_BYNAME" or "XML_ADDNODE" or "XML_ADDNODE_BYNAME" or "XML_ADDATTRIBUTE" or "XML_ADDATTRIBUTE_BYNAME" or "XML_REMOVENODE" or "XML_REMOVENODE_BYNAME")
        {
            bool get = name.StartsWith("XML_GET", StringComparison.Ordinal), attribute = name.StartsWith("XML_ADDATTRIBUTE", StringComparison.Ordinal),
                remove = name.StartsWith("XML_REMOVENODE", StringComparison.Ordinal);
            var lowered = get || remove ? arguments.ToArray() : LowerBuiltinArguments(arguments, attribute ? 4 : 3, attribute ? 5 : 4);
            string token = Temporary(); Reference(Var(token));
            InvokeBuiltinCore("$XML_BEGIN", [lowered[0], new Literal(new(name.EndsWith("_BYNAME", StringComparison.Ordinal) ? 1L : 0L)), new Literal(new(get ? 0L : 1L))]); Emit(Op.Store);
            Expression(Var(token)); int missing = Emit(Op.JumpFalse);
            InvokeBuiltinCore("$XML_SELECT", [Var(token), lowered[1]]);
            int empty = -1;
            if (get) Emit(Op.Pop); else empty = Emit(Op.JumpFalse);
            lowered[0] = Var(token); lowered[1] = new Literal(new(""));
            if (get) lowered = LowerBuiltinArguments(lowered, 3);
            InvokeBuiltinCore("$" + name, lowered); int end = Emit(Op.Jump);
            if (empty >= 0)
            {
                Patch(empty, code.Count); InvokeBuiltinCore("$XML_END", [Var(token)]); int noNodes = Emit(Op.Jump);
                Patch(missing, code.Count); Emit(Op.Constant, value: new(-1L)); Patch(noNodes, code.Count);
            }
            else { Patch(missing, code.Count); Emit(Op.Constant, value: new(-1L)); }
            Patch(end, code.Count); return;
        }
        if (name == "SETVAR")
        {
            bool hasFallback = arguments.Count > 2 && arguments[2] is not Omitted;
            string fallback = Temporary(), target = Temporary(ValueKind.String);
            Assign(Var(fallback), hasFallback ? arguments[2] : new Literal(new EraValue(0L)));
            Assign(Var(target), arguments[0]);
            InvokeBuiltinCore("$SETVAR_CHECK", [Var(target), new Literal(new EraValue((long)Kind(arguments[1]))), new Literal(new EraValue(hasFallback ? 1L : 0L))]);
            int invalid = Emit(Op.JumpFalse);
            InvokeBuiltinCore(name, [Var(target), arguments[1]]);
            int end = Emit(Op.Jump); Patch(invalid, code.Count); Expression(Var(fallback)); Patch(end, code.Count); return;
        }
        if (name == "GETCHARA" && !index.Options.AllowSpCharacters)
        { InvokeBuiltinCore(name, [arguments[0]]); return; }
        if (name is "DT_CELL_GET" or "DT_CELL_GETS" or "DT_CELL_SET")
        {
            string token = Temporary(); Reference(Var(token)); InvokeBuiltinCore("$DT_CELL_BEGIN", [arguments[0]]); Emit(Op.Store);
            Expression(Var(token)); int missing = Emit(Op.JumpFalse);
            var lowered = arguments.ToArray();
            foreach (int at in new[] { name == "DT_CELL_SET" ? 4 : 3, 1, 2 })
            {
                if (at >= lowered.Length || lowered[at] is Omitted) continue;
                string value = Temporary(Kind(lowered[at])); Assign(Var(value), lowered[at]); lowered[at] = Var(value);
            }
            Expr mode = lowered.Length > (name == "DT_CELL_SET" ? 4 : 3) ? lowered[name == "DT_CELL_SET" ? 4 : 3] : new Omitted();
            if (name == "DT_CELL_SET")
            {
                Expr value = lowered.Length > 3 ? lowered[3] : new Omitted();
                InvokeBuiltinCore("$DT_CELL_SET_PREPARE", [Var(token), lowered[1], lowered[2], mode is Omitted ? new Literal(new(0L)) : mode,
                    new Literal(new EraValue(value is Omitted ? -1L : (long)Kind(value)))]);
                Emit(Op.Duplicate); Emit(Op.Constant, value: new(1L)); Emit(Op.Binary, "=="); int refused = Emit(Op.JumpFalse);
                Emit(Op.Pop); InvokeBuiltinCore("$DT_CELL_SET_COMMIT", [Var(token), value]); Patch(refused, code.Count);
            }
            else InvokeBuiltinCore("$" + name, [Var(token), lowered[1], lowered[2], mode]);
            int finished = Emit(Op.Jump); Patch(missing, code.Count);
            Emit(Op.Constant, value: name == "DT_CELL_GETS" ? new EraValue("") : new EraValue(name == "DT_CELL_SET" ? -1L : 0L));
            Patch(finished, code.Count); return;
        }
        if (name is "SUMARRAY" or "MAXARRAY" or "MINARRAY")
        {
            var lowered = arguments.ToArray();
            for (int i = 1; i < lowered.Length; i++)
            {
                if (lowered[i] is Omitted) continue;
                string bound = Temporary(ValueKind.Integer); Assign(Var(bound), lowered[i]); lowered[i] = Var(bound);
            }
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name == "INRANGEARRAY")
        {
            var lowered = LowerBuiltinArguments(arguments, 3, 4);
            Expr start = lowered.Length > 3 && lowered[3] is not Omitted ? lowered[3] : new Literal(new(0L)), end = lowered.Length > 4 ? lowered[4] : new Omitted();
            Reference((Variable)arguments[0]); Emit(Op.Duplicate);
            Expression(start);
            if (end is Omitted) Emit(Op.Constant, text: "$omitted", value: new(0L)); else Expression(end);
            Builtin("$ARRAY_QUERY_RANGE");
            Expression(arguments[1]); Expression(arguments[2]); Expression(start);
            if (end is Omitted) Emit(Op.Constant, text: "$omitted", value: new(0L)); else Expression(end);
            Builtin(name); return;
        }
        if (name == "CVARSET")
        {
            var variable = (Variable)arguments[0];
            for (int i = 0; i < variable.Indices.Count; i++)
            { Expression(Subscript(variable, variable.Indices[i], i)); Emit(Op.Pop); }
            var lowered = arguments.ToArray();
            foreach (int at in new[] { 1, 3, 4 })
            {
                if (at >= lowered.Length || lowered[at] is Omitted) continue;
                string value = Temporary(Kind(lowered[at])); Assign(Var(value), lowered[at]); lowered[at] = Var(value);
                if (at is 3 or 4) InvokeBuiltinCore(at == 3 ? "$CVARSET_START" : "$CVARSET_END", [lowered[at]]);
            }
            InvokeBuiltinCore(name, lowered); return;
        }
        if (name is "MATCH" or "FINDELEMENT" or "FINDLASTELEMENT")
        {
            var lowered = arguments.ToArray();
            for (int i = 2; i < lowered.Length; i++)
            {
                if (lowered[i] is Omitted) continue;
                string bound = Temporary(ValueKind.Integer); Assign(Var(bound), lowered[i]); lowered[i] = Var(bound);
            }
            Expr start = lowered.Length > 2 ? lowered[2] : new Omitted();
            Expr end = lowered.Length > 3 ? lowered[3] : new Omitted();
            Reference((Variable)arguments[0]); Emit(Op.Duplicate);
            if (start is Omitted) Emit(Op.Constant, value: new(0L)); else Expression(start);
            if (end is Omitted) Emit(Op.Constant, text: "$omitted", value: new(0L)); else Expression(end);
            Builtin("$ARRAY_QUERY_RANGE");
            Expression(arguments[1]);
            if (start is Omitted) Emit(Op.Constant, value: new(0L)); else Expression(start);
            if (end is Omitted) Emit(Op.Constant, text: "$omitted", value: new(0L)); else Expression(end);
            if (name != "MATCH")
            {
                if (lowered.Length <= 4 || lowered[4] is Omitted) Emit(Op.Constant, value: new(0L)); else Expression(lowered[4]);
            }
            Builtin(name); return;
        }
        if (name == "ARRAYSHIFT")
        {
            var variable = (Variable)arguments[0]; var spec = Resolve(variable.Name);
            if (spec.Shape.Length != 1 || spec.ReadOnly) throw new ScriptException("REFERENCE", "ARRAYSHIFT 需要可写一维数组", span);
            if (Kind(arguments[2]) != spec.Kind) throw new ScriptException("TYPE", "ARRAYSHIFT 填充值类型不匹配", span);
            Reference(variable);
            string shift = Temporary(), start = Temporary(), count = Temporary();
            Assign(Var(shift), arguments[1]);
            Expression(Var(shift)); int zeroShift = Emit(Op.JumpFalse);
            Assign(Var(start), arguments.Count > 3 && arguments[3] is not Omitted ? arguments[3] : new Literal(new(0L)));
            bool omitted = arguments.Count < 5 || arguments[4] is Omitted;
            if (!omitted) Assign(Var(count), arguments[4]);
            Expr amount = omitted ? new Omitted() : Var(count);
            InvokeBuiltinCore("$ARRAYSHIFT_RANGE", [Var(start), amount]); int zeroCount = Emit(Op.JumpFalse);
            Expression(Var(shift)); Expression(arguments[2]); Expression(Var(start));
            if (omitted) Emit(Op.Constant, text: "$omitted", value: new(0L)); else Expression(Var(count));
            Builtin(name); int end = Emit(Op.Jump);
            Patch(zeroShift, code.Count); Patch(zeroCount, code.Count); Emit(Op.Pop);
            Patch(end, code.Count); return;
        }
        if (name == "ALLSAMES")
        {
            string first = Temporary(Kind(arguments[0])); Assign(Var(first), arguments[0]);
            Expr result = new Literal(new(1L));
            foreach (var argument in arguments.Skip(1)) result = new Binary("&&", result, new Binary("==", Var(first), argument));
            Expression(result); return;
        }
        if (name is "MAXCARRAY" or "MINCARRAY" or "INRANGECARRAY")
        {
            var variable = (Variable)arguments[0]; var spec = Resolve(variable.Name);
            if (spec.Scope != VariableScope.Character || spec.Kind == ValueKind.String || spec.Shape.Length != 1)
                throw new ScriptException("REFERENCE", "角色统计需要一维数值角色数组", span);
            int range = name == "INRANGECARRAY" ? 3 : 1;
            var lowered = new List<Expr> { new Literal(new EraValue(variable.Name)) };
            for (int i = 1; i < range + 2; i++) lowered.Add(i < arguments.Count ? arguments[i] : new Omitted());
            lowered = LowerBuiltinArguments(lowered, range, range + 1).ToList();
            int skip = variable.Indices.Count == 2 ? 1 : 0;
            if (skip == 1) { Expression(variable.Indices[0]); Emit(Op.Pop); }
            Expr element = variable.Indices.Count == 0 ? new Literal(new EraValue(0L)) : Subscript(variable, variable.Indices[skip], skip);
            string position = Temporary(); Assign(Var(position), element); lowered.Add(Var(position));
            InvokeBuiltinCore("$CHARACTER_RANGE", [lowered[range], lowered[range + 1]]);
            InvokeBuiltinCore("$" + name, lowered); return;
        }
        if (name is "FINDCHARA" or "FINDLASTCHARA")
        {
            var variable = (Variable)arguments[0]; var spec = Resolve(variable.Name);
            if (spec.Scope != VariableScope.Character) throw new ScriptException("REFERENCE", "查找需要角色变量", span);
            if (Kind(arguments[1]) != spec.Kind) throw new ScriptException("TYPE", "角色查询值类型不匹配", span);
            var lowered = new List<Expr> { new Literal(new(variable.Name)), arguments[1],
                arguments.Count > 2 ? arguments[2] : new Omitted(), arguments.Count > 3 ? arguments[3] : new Omitted() };
            int skip = variable.Indices.Count == spec.Shape.Length + 1 ? 1 : 0;
            if (spec.CharacterScalar || variable.Indices.Count == 0) lowered.AddRange(spec.Shape.Select(_ => (Expr)new Literal(new(0L))));
            else lowered.AddRange(variable.Indices.Skip(skip).Select((e, i) => Subscript(variable, e, i + skip)));
            if (lowered.Count != 4 + spec.Shape.Length) throw new ScriptException("RANK", "角色查找下标维数不匹配", span);
            lowered = LowerBuiltinArguments(lowered, Enumerable.Range(4, spec.Shape.Length).Concat(new[] { 2, 3 }).ToArray()).ToList();
            InvokeBuiltinCore("$CHARACTER_RANGE", [lowered[2], lowered[3]]);
            InvokeBuiltinCore("$" + name, lowered); return;
        }
        if (name is "SUMCARRAY" or "CMATCH")
        {
            var variable = (Variable)arguments[0]; var spec = Resolve(variable.Name);
            if (spec.Scope != VariableScope.Character) throw new ScriptException("REFERENCE", "查找需要角色变量", span);
            bool sum = name == "SUMCARRAY";
            if (sum ? spec.Kind == ValueKind.String : Kind(arguments[1]) != spec.Kind) throw new ScriptException("TYPE", "角色查询值类型不匹配", span);
            var lowered = new List<Expr> { new Literal(new(variable.Name)) };
            if (!sum) lowered.Add(arguments[1]);
            int range = sum ? 1 : 2;
            lowered.Add(arguments.Count > range ? arguments[range] : new Omitted());
            lowered.Add(arguments.Count > range + 1 ? arguments[range + 1] : new Omitted());
            int skip = variable.Indices.Count == spec.Shape.Length + 1 ? 1 : 0;
            bool selector = skip == 1 || spec.CharacterScalar && variable.Indices.Count > 0;
            if (sum && selector || name == "CMATCH")
            {
                for (int i = sum ? 1 : 2; i < lowered.Count; i++)
                {
                    if (lowered[i] is Omitted) continue;
                    string bound = Temporary(ValueKind.Integer); Assign(Var(bound), lowered[i]); lowered[i] = Var(bound);
                }
                if (selector) { Expression(variable.Indices[0]); Emit(Op.Pop); }
            }
            if (spec.CharacterScalar || variable.Indices.Count == 0)
                lowered.AddRange(spec.Shape.Select(_ => (Expr)new Literal(new(0L))));
            else
                lowered.AddRange(variable.Indices.Skip(skip).Select((e, i) => Subscript(variable, e, i + skip)));
            if (lowered.Count != range + 2 + spec.Shape.Length) throw new ScriptException("RANK", "角色查找下标维数不匹配", span);
            if (name == "CMATCH")
            {
                for (int i = 4; i < lowered.Count; i++)
                { string element = Temporary(ValueKind.Integer); Assign(Var(element), lowered[i]); lowered[i] = Var(element); }
                InvokeBuiltinCore("$CHARACTER_RANGE", [lowered[2], lowered[3]]);
            }
            InvokeBuiltinCore("$" + name + (sum && spec.Kind == ValueKind.Float ? "F" : ""), lowered); return;
        }
        if (name is "GETMETH" or "GETMETHS")
        {
            string target = Temporary(ValueKind.String); Assign(Var(target), arguments[0]);
            Expression(Var(target)); Builtin("$METHOD_EXISTS"); int missing = Emit(Op.JumpFalse);
            var kinds = new List<Expr> { Var(target) };
            for (int i = 2; i < arguments.Count; i++) kinds.Add(new Literal(new((long)CallArgumentKind(arguments[i]))));
            InvokeBuiltinCore("$CALL_ACCEPTS", kinds); Emit(Op.Unary, "call_arguments"); Emit(Op.Pop);
            InvokeBuiltinCore("$METHOD_RETURN", [Var(target), new Literal(new(name == "GETMETHS" ? 3L : 2L))]);
            var forwarded = new List<Expr> { Var(target), new Omitted() };
            for (int i = 2; i < arguments.Count; i++) forwarded.Add(arguments[i]);
            InvokeBuiltinCore(name, forwarded);
            int end = Emit(Op.Jump); Patch(missing, code.Count);
            InvokeBuiltinCore(name, [Var(target), arguments.Count > 1 ? arguments[1] : new Omitted()]);
            Patch(end, code.Count); return;
        }
        InvokeBuiltinCore(name, arguments);
    }
    private static IReadOnlyList<Expr> TrimTrailingArgument(BuiltinSignature signature, IReadOnlyList<Expr> arguments)
        => signature.Variadic is { Default: null } && arguments.Count > signature.Parameters.Length && arguments[^1] is Omitted
            ? arguments.Take(arguments.Count - 1).ToArray() : arguments;
    private Expr[] LowerBuiltinArguments(IReadOnlyList<Expr> arguments, params int[] order)
    {
        var lowered = arguments.ToArray();
        foreach (int at in order)
        {
            if (at >= lowered.Length || lowered[at] is Omitted) continue;
            string value = Temporary(Kind(lowered[at])); Assign(Var(value), lowered[at]); lowered[at] = Var(value);
        }
        return lowered;
    }
    private bool InvokeGraphicsBuiltin(string name, IReadOnlyList<Expr> arguments)
    {
        bool sprite = name is "SPRITECREATE" or "SPRITEANIMECREATE" or "SPRITEANIMEADDFRAME";
        bool create = name is "GCREATE" or "GCREATEFROMFILE";
        if (!sprite && !create && name is not ("GCLEAR" or "GDRAWG" or "GDRAWGWITHMASK" or "GDRAWSPRITE" or "GGETCOLOR" or "GSETCOLOR" or "GSETBRUSH" or "GFILLRECTANGLE" or "CBGSETG")) return false;
        var lowered = arguments.ToArray(); var refused = new List<int>();
        void Evaluate(int at)
        {
            if (at >= lowered.Length || lowered[at] is Omitted) return;
            string slot = Temporary(Kind(lowered[at])); Assign(Var(slot), lowered[at]); lowered[at] = Var(slot);
        }
        void Created(int at)
        { Evaluate(at); InvokeBuiltinCore("GCREATED", [lowered[at]]); refused.Add(Emit(create ? Op.JumpTrue : Op.JumpFalse)); }
        void Coordinate(int at, bool size = false)
        {
            if (at >= lowered.Length || lowered[at] is Omitted) return;
            Evaluate(at); InvokeBuiltinCore("$GRAPHICS_INT", [lowered[at], new Literal(new(size ? 1L : 0L))]);
        }
        if (sprite)
        {
            Evaluate(0); Expression(new Binary("!=", lowered[0], new Literal(new("")))); refused.Add(Emit(Op.JumpFalse));
            InvokeBuiltinCore(name == "SPRITEANIMEADDFRAME" ? "$SPRITE_ANIMATION" : "$SPRITE_ACTIVE", [lowered[0]]);
            refused.Add(Emit(name == "SPRITEANIMEADDFRAME" ? Op.JumpFalse : Op.JumpTrue));
            if (name == "SPRITEANIMECREATE") { Coordinate(1); Coordinate(2); }
            else
            {
                Created(1);
                if (lowered.Length >= 6)
                {
                    Coordinate(2); Coordinate(3); Coordinate(4, true); Coordinate(5, true);
                    InvokeBuiltinCore("$SPRITE_RECT_READY", [lowered[1], lowered[2], lowered[3], lowered[4], lowered[5], new Literal(new(name == "SPRITEANIMEADDFRAME" ? 1L : 0L))]);
                    if (name == "SPRITEANIMEADDFRAME") refused.Add(Emit(Op.JumpFalse)); else Emit(Op.Pop);
                }
                if (name == "SPRITEANIMEADDFRAME") { Coordinate(6); Coordinate(7); }
            }
        }
        else
        {
            if (name == "GCLEAR")
            {
                Evaluate(0); InvokeBuiltinCore("$GRAPHICS_ID", [lowered[0]]);
                Evaluate(1); InvokeBuiltinCore("$GRAPHICS_COLOR", [lowered[1]]);
                InvokeBuiltinCore("GCREATED", [lowered[0]]); refused.Add(Emit(Op.JumpFalse));
            }
            else Created(0);
            if (name == "GDRAWSPRITE")
            {
                Evaluate(1); InvokeBuiltinCore("$SPRITE_ACTIVE", [lowered[1]]); refused.Add(Emit(Op.JumpFalse));
                Coordinate(2); Coordinate(3); Coordinate(4, true); Coordinate(5, true);
            }
            else if (name is "GDRAWG" or "GDRAWGWITHMASK")
            {
                Created(1);
                if (name == "GDRAWGWITHMASK")
                {
                    Created(2); InvokeBuiltinCore("$GDRAW_MASK_SIZE", [lowered[1], lowered[2]]); refused.Add(Emit(Op.JumpFalse));
                    Coordinate(3); Coordinate(4);
                }
                else for (int at = 2; at < 10; at++) Coordinate(at, at is 4 or 5 or 8 or 9);
            }
            else if (name is "GCLEAR" or "GSETBRUSH" or "GSETCOLOR")
            {
                if (name != "GCLEAR") { Evaluate(1); InvokeBuiltinCore("$GRAPHICS_COLOR", [lowered[1]]); }
                if (name == "GCLEAR") { Coordinate(2); Coordinate(3); Coordinate(4); Coordinate(5); }
                else if (name == "GSETCOLOR") { Coordinate(2); Coordinate(3); }
            }
            else if (name == "GFILLRECTANGLE") { Coordinate(1); Coordinate(2); Coordinate(3, true); Coordinate(4, true); }
            else if (name is "GGETCOLOR" or "CBGSETG" or "GCREATE") { Coordinate(1); Coordinate(2); }
        }
        InvokeBuiltinCore(name, lowered); int end = Emit(Op.Jump);
        foreach (int at in refused) Patch(at, code.Count);
        Emit(Op.Constant, value: new(name == "GGETCOLOR" ? -1L : 0L)); Patch(end, code.Count); return true;
    }
    private static string BuiltinExpressionText(Expr expression) => expression switch
    {
        Omitted => "",
        Literal { Value.Kind: ValueKind.String } literal => "\"" + literal.Value.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"",
        Literal { Value.Kind: ValueKind.Float } literal => literal.Value.Float.ToString("F340", System.Globalization.CultureInfo.InvariantCulture),
        Literal literal => literal.Value.ToString(),
        FormattedLiteral formatted => "@\"" + formatted.Text + "\"",
        Variable variable => variable.Name + (variable.ExplicitIndices ? string.Concat(variable.Indices.Select(i => ":(" + BuiltinExpressionText(i) + ")")) : ""),
        Unary unary => "(" + unary.Operator + BuiltinExpressionText(unary.Operand) + ")",
        Binary binary => "(" + BuiltinExpressionText(binary.Left) + binary.Operator + BuiltinExpressionText(binary.Right) + ")",
        Conditional conditional => "(" + BuiltinExpressionText(conditional.Test) + "?" + BuiltinExpressionText(conditional.Yes) + "#" + BuiltinExpressionText(conditional.No) + ")",
        Invocation invocation => invocation.Name + "(" + string.Join(",", invocation.Arguments.Select(BuiltinExpressionText)) + ")",
        Mutation mutation => "(" + (mutation.Postfix ? BuiltinExpressionText(mutation.Variable) + (mutation.Delta > 0 ? "++" : "--")
            : (mutation.Delta > 0 ? "++" : "--") + BuiltinExpressionText(mutation.Variable)) + ")",
        _ => throw new ScriptException("SYNTAX", "无法编译动态填充参数", expression.Span)
    };
    private void InvokeBuiltinCore(string name, IReadOnlyList<Expr> arguments)
    {
        var signature = builtins.Find(name)!;
        int count = Math.Max(signature.Parameters.Length, arguments.Count);
        for (int i = 0; i < count; i++)
        {
            int ignored = -1;
            bool tableArray = name == "DT_ROW_ADD" && arguments.Count == 4 && i is 1 or 2
                || name == "DT_ROW_REMOVE" && arguments.Count > 2 && arguments[2] is not Omitted && i == 1
                || name == "DT_SELECT" && i == 3
                || name == "SPLIT" && i == 2 || name == "HTML_TAGSPLIT" && i == 1
                || name == "REGEXPMATCH" && i == 3
                || name is "XML_GET" or "XML_GET_BYNAME" or "$XML_GET" or "$XML_GET_BYNAME" && i == 2 && i < arguments.Count && arguments[i] is Variable && Kind(arguments[i]) == ValueKind.String;
            if (name is "GETMETH" or "GETMETHS" && i >= 2)
            {
                Expression(arguments[0]); Builtin("$CALL_ARITY"); Emit(Op.Constant, value: new((long)(i - 2))); Emit(Op.Binary, ">");
                ignored = Emit(Op.JumpFalse);
            }
            if (i >= arguments.Count || arguments[i] is Omitted) Emit(Op.Constant, text: "$omitted", value: signature.Parameter(i).Default!.Value);
            else if (signature.Parameter(i).VariableName) Emit(Op.Constant, value: new EraValue(((Variable)arguments[i]).Name));
            else if (signature.Parameter(i).Reference)
            {
                if (tableArray || name is "ARRAYMSORT" or "ARRAYMSORTEX" or "$ARRAYMSORTEX") ArrayArgumentReference((Variable)arguments[i]);
                else Reference((Variable)arguments[i]);
            }
            else if (signature.Parameter(i).CaptureReference && arguments[i] is Variable variable && variable.Name is not ("RAND" or "CHARANUM" or "LINECOUNT" or "DRAWLINESTR")
                && (name != "CALLSHARP" || !Resolve(variable.Name).ReadOnly))
            {
                if (name is "GETMETH" or "GETMETHS" && i >= 2)
                {
                    Expression(arguments[0]); Emit(Op.Constant, value: new((long)(i - 2))); Builtin("$CALL_REFERENCE");
                    int valueParameter = Emit(Op.JumpFalse); ArrayArgumentReference(variable); int end = Emit(Op.Jump);
                    Patch(valueParameter, code.Count); Reference(variable); Patch(end, code.Count);
                }
                else if (tableArray || name is "ARRAYMSORT" or "ARRAYMSORTEX" or "$ARRAYMSORTEX") ArrayArgumentReference(variable);
                else Reference(variable);
            }
            else Expression(arguments[i]);
            if (ignored >= 0)
            {
                int end = Emit(Op.Jump); Patch(ignored, code.Count);
                Emit(Op.Constant, text: "$omitted", value: new(0L)); Patch(end, code.Count);
            }
        }
        Builtin(name, count);
    }
}
