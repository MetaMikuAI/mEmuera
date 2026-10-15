using System.Collections.Immutable;
using System.Text.RegularExpressions;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class ArrayBuiltins
{
    private sealed record PickHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            foreach (var argument in a)
            {
                bool selector = argument.Reference?.IsCharacterSelector == true;
                long value = argument.Value.AsInt();
                if (!selector && (value < 0 || value >= context.CharacterCount)) throw new ScriptException("CHARACTER_BOUNDS", "PICKUPCHARA 角色下标越界");
                if (value >= context.CharacterCount) throw new ScriptException("CHARACTER_BOUNDS", "PICKUPCHARA 角色选择器越界");
            }
            context.PickCharacters(a.Select(v => v.Value.AsInt()).ToArray()); return null;
        }
    }
    private sealed record PickArgumentHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            bool selector = a[0].Reference?.IsCharacterSelector == true;
            long value = a[0].Value.AsInt();
            if (!selector && value < 0 || value >= a[1].Value.AsInt()) throw new ScriptException("CHARACTER_BOUNDS", "PICKUPCHARA 角色下标越界");
            return null;
        }
    }
    private sealed record CharacterQueryHandler(string Id, string Name) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            bool range = Name == "INRANGECARRAY";
            int at = range ? 3 : 1;
            long start = a[at].Value.AsInt(), end = a[at + 1].IsOmitted ? context.CharacterCount : a[at + 1].Value.AsInt();
            if (start < 0 || start >= context.CharacterCount || end < 0 || end > context.CharacterCount) throw new ScriptException("BOUNDS", "角色统计范围越界");
            string variable = a[0].Value.AsString(); long[] indices = [a[at + 2].Value.AsInt()];
            bool floating = context.VariableInfo(variable).Kind == ValueKind.Float;
            if (range && start >= end) return new(0L);
            var result = context.ReadCharacter(variable, indices, start); long count = 0;
            for (long i = start; i < end; i++)
            {
                var value = context.ReadCharacter(variable, indices, i);
                if (range)
                {
                    if (floating ? value.AsFloat() >= a[1].Value.AsInt() && value.AsFloat() < a[2].Value.AsInt()
                        : value.AsInt() >= a[1].Value.AsInt() && value.AsInt() < a[2].Value.AsInt()) count++;
                }
                else if (Name == "MAXCARRAY" ? floating ? value.Float > result.Float : value.Integer > result.Integer
                    : floating ? value.Float < result.Float : value.Integer < result.Integer) result = value;
            }
            return range ? new EraValue(count) : floating ? new EraValue((long)result.Float) : result;
        }
    }
    private sealed record SortHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1 || spec.ReadOnly) throw new ScriptException("REFERENCE", "ARRAYSORT 需要可写一维数组");
            int start = unchecked((int)a[2].Value.AsInt()), count = a[3].IsOmitted ? spec.Length - start : unchecked((int)a[3].Value.AsInt());
            if (start < 0 || count < 0) throw new ScriptException("BOUNDS", "ARRAYSORT 范围不能为负");
            if (!a[3].IsOmitted && count == 0) return null;
            if (start >= spec.Length || count > spec.Length - start) throw new ScriptException("BOUNDS", "ARRAYSORT 范围越界");
            var values = new EraValue[count];
            for (int i = 0; i < count; i++) values[i] = context.Read(reference with { Index = start + i });
            Array.Sort(values, (left, right) => spec.Kind switch
            {
                ValueKind.Integer => left.Integer.CompareTo(right.Integer),
                ValueKind.Float => left.Float.CompareTo(right.Float),
                _ => string.Compare(left.Text, right.Text, StringComparison.CurrentCulture)
            });
            if (a[1].Value.AsInt() == 0) Array.Reverse(values);
            for (int i = 0; i < count; i++) context.Write(reference with { Index = start + i }, values[i]);
            return null;
        }
    }
    private sealed record QueryRangeHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1) throw new ScriptException("RANK", "数组查询需要一维数组");
            long start = a[1].Value.AsInt(), end = a[2].IsOmitted ? context.ArrayLength(reference) : a[2].Value.AsInt();
            if (start < 0 || start > context.ArrayLength(reference) || end < 0 || end > context.ArrayLength(reference))
                throw new ScriptException("BOUNDS", "数组查询范围越界");
            return null;
        }
    }
    private sealed record MatchArrayHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1 || spec.Kind != a[1].Value.Kind) throw new ScriptException("TYPE", "MATCH 需要一维数组和同类型值");
            long start = a[2].Value.AsInt(), end = a[3].IsOmitted ? spec.Length : a[3].Value.AsInt();
            if (start < 0 || start > spec.Length || end < 0 || end > spec.Length) throw new ScriptException("BOUNDS", "MATCH 范围越界");
            long count = 0;
            for (long i = start; i < end; i++) if (context.Read(reference with { Index = (int)i }) == a[1].Value) count++;
            return new(count);
        }
    }
    private sealed record JoinHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            int length = spec.Shape[^1], offset = reference.Index / length * length;
            long start = a[2].Value.AsInt(), count = a[3].IsOmitted ? length - start : a[3].Value.AsInt();
            if (start < 0 || start > length || count < 0 || count > length - start) throw new ScriptException("BOUNDS", "STRJOIN 范围越界");
            var values = new string[(int)count];
            for (int i = 0; i < count; i++) values[i] = context.Read(reference with { Index = offset + (int)start + i }).ToString();
            return new(string.Join(a[1].Value.AsString(), values));
        }
    }
    private sealed record InRangeHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1 || spec.Kind == ValueKind.String) throw new ScriptException("TYPE", "INRANGEARRAY 需要一维数值数组");
            long start = a[3].Value.AsInt(), end = a[4].IsOmitted ? spec.Length : a[4].Value.AsInt();
            if (start < 0 || start > spec.Length || end < 0 || end > spec.Length) throw new ScriptException("BOUNDS", "数组统计范围越界");
            long minimum = a[1].Value.AsInt(), maximum = a[2].Value.AsInt(), count = 0;
            for (long i = start; i < end; i++)
            {
                var value = context.Read(reference with { Index = (int)i });
                if (spec.Kind == ValueKind.Float ? value.AsFloat() >= minimum && value.AsFloat() < maximum
                    : value.AsInt() >= minimum && value.AsInt() < maximum) count++;
            }
            return new(count);
        }
    }
    private sealed record ExtremeHandler(string Id, bool Maximum) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!;
            var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1 || spec.Kind == ValueKind.String) throw new ScriptException("TYPE", "数组极值需要一维数值数组");
            long start = a[1].Value.AsInt(), end = a[2].IsOmitted ? spec.Length : a[2].Value.AsInt();
            if (start < 0 || start >= spec.Length || end < 0 || end > spec.Length) throw new ScriptException("BOUNDS", "数组极值范围越界");
            var result = context.Read(reference with { Index = (int)start });
            for (long i = start + 1; i < end; i++)
            {
                var value = context.Read(reference with { Index = (int)i });
                bool greater = spec.Kind == ValueKind.Float ? value.AsFloat() > result.AsFloat() : value.AsInt() > result.AsInt();
                if (Maximum ? greater : spec.Kind == ValueKind.Float ? value.AsFloat() < result.AsFloat() : value.AsInt() < result.AsInt()) result = value;
            }
            return result.Kind == ValueKind.Float ? new EraValue(unchecked((long)result.AsFloat())) : result;
        }
    }
    private sealed record LevelHandler(string Id, string Variable) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long value = a[0].Value.AsInt(), maximum = a[1].Value.AsInt();
            for (int i = 0; i < unchecked((int)maximum); i++)
                if (value < context.ReadGlobal(Variable, checked(i + 1)).AsInt()) return new((long)i);
            return new(maximum);
        }
    }
    private sealed record SwapHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var first = a[0].Reference!; var second = a[1].Reference!;
            var x = context.VariableInfo(first); var y = context.VariableInfo(second);
            if (x.ReadOnly || y.ReadOnly) throw new ScriptException("READONLY", "SWAP 需要可写变量");
            if (x.Kind != y.Kind || x.Kind == ValueKind.Float) throw new ScriptException("TYPE", "SWAP 需要同类型整数或字符串变量");
            var left = context.Read(first); var right = context.Read(second);
            context.Write(first, right); context.Write(second, left); return null;
        }
    }
    private sealed record SumHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            int length = spec.Shape[^1], offset = reference.Index / length * length;
            long start = a[1].Value.AsInt(), end = a[2].IsOmitted ? length : a[2].Value.AsInt();
            if (start < 0 || start > length || end < 0 || end > length) throw new ScriptException("BOUNDS", "数组求和范围越界");
            long integer = 0; double number = 0;
            for (long i = start; i < end; i++)
            {
                var value = context.Read(reference with { Index = offset + (int)i });
                if (spec.Kind == ValueKind.Float) number += value.AsFloat(); else integer = unchecked(integer + value.AsInt());
            }
            return spec.Kind == ValueKind.Float ? new EraValue(number) : new EraValue(integer);
        }
    }
    private sealed record CharacterRangeHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long start = a[0].Value.AsInt(), end = a[1].IsOmitted ? context.CharacterCount : a[1].Value.AsInt();
            if (start < 0 || start >= context.CharacterCount || end < 0 || end > context.CharacterCount)
                throw new ScriptException("BOUNDS", "角色范围越界");
            return null;
        }
    }
    private sealed record CharacterBoundHandler(string Id, bool End) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            int index = unchecked((int)a[0].Value.AsInt());
            if (index < 0 || (End ? index > context.CharacterCount : index >= context.CharacterCount))
                throw new ScriptException("CHARACTER_BOUNDS", "CVARSET 角色范围无效");
            return null;
        }
    }
    private sealed record ShiftRangeHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            int start = unchecked((int)a[0].Value.AsInt()), count = unchecked((int)a[1].Value.AsInt());
            if (start < 0 || !a[1].IsOmitted && count < 0) throw new ScriptException("BOUNDS", "ARRAYSHIFT 范围不能为负");
            return new(a[1].IsOmitted || count != 0 ? 1L : 0L);
        }
    }
    private sealed record ShiftHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1 || spec.ReadOnly) throw new ScriptException("REFERENCE", "ARRAYSHIFT 需要可写一维数组");
            if (spec.Kind != a[2].Value.Kind) throw new ScriptException("TYPE", "ARRAYSHIFT 填充值类型不匹配");
            int shift = unchecked((int)a[1].Value.AsInt()); if (shift == 0) return null;
            int length = context.ArrayLength(reference), start = unchecked((int)a[3].Value.AsInt());
            int count = a[4].IsOmitted ? length - start : unchecked((int)a[4].Value.AsInt());
            if (start < 0 || count < 0) throw new ScriptException("BOUNDS", "ARRAYSHIFT 范围不能为负");
            if (count == 0) return null;
            if (start >= length) throw new ScriptException("BOUNDS", "ARRAYSHIFT 起点越界");
            count = Math.Min(count, length - start);
            for (int i = shift > 0 ? count - 1 : 0; shift > 0 ? i >= 0 : i < count; i += shift > 0 ? -1 : 1)
            {
                long source = (long)i - shift;
                var value = source >= 0 && source < count ? context.Read(reference with { Index = start + (int)source }) : a[2].Value;
                context.Write(reference with { Index = start + i }, value);
            }
            return null;
        }
    }
    private sealed record RemoveHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var reference = a[0].Reference!; var spec = context.VariableInfo(reference);
            if (spec.Shape.Length != 1 || spec.ReadOnly) throw new ScriptException("REFERENCE", "ARRAYREMOVE 需要可写一维数组");
            int length = context.ArrayLength(reference), start = unchecked((int)a[1].Value.AsInt()), count = unchecked((int)a[2].Value.AsInt());
            if (start < 0 || start >= length) throw new ScriptException("BOUNDS", "ARRAYREMOVE 起点越界");
            if (count <= 0) count = length;
            int removed = Math.Min(count, length - start);
            for (int i = start; i < length; i++)
                context.Write(reference with { Index = i }, i < length - removed ? context.Read(reference with { Index = i + removed }) : EraValue.Zero(spec.Kind));
            return null;
        }
    }
    private sealed record SumCharacterHandler(string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long start = a[1].Value.AsInt(), end = a[2].IsOmitted ? context.CharacterCount : a[2].Value.AsInt();
            if (start < 0 || start >= context.CharacterCount || end < 0 || end > context.CharacterCount)
                throw new ScriptException("BOUNDS", "角色求和范围越界");
            string name = a[0].Value.AsString(); var indices = a.Skip(3).Select(v => v.Value.AsInt()).ToArray();
            bool floating = context.VariableInfo(name).Kind == ValueKind.Float;
            long integer = 0; double number = 0;
            for (long i = start; i < end; i++)
            {
                var value = context.ReadCharacter(name, indices, i);
                if (floating) number += value.AsFloat(); else integer = unchecked(integer + value.AsInt());
            }
            return floating ? new EraValue(number) : new EraValue(integer);
        }
    }
    private sealed record FindCharacterHandler(string Id, bool Last, bool Count = false) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            long start = a[2].Value.AsInt(), end = a[3].IsOmitted ? context.CharacterCount : a[3].Value.AsInt();
            if (start < 0 || start >= context.CharacterCount || end < 0 || end > context.CharacterCount)
                throw new ScriptException("BOUNDS", "角色查找范围越界");
            var indices = a.Skip(4).Select(v => v.Value.AsInt()).ToArray();
            long matches = 0;
            for (long i = Last ? end - 1 : start; Last ? i >= start : i < end; i += Last ? -1 : 1)
                if (context.ReadCharacter(a[0].Value.AsString(), indices, i) is { } value
                    && (value.Kind == ValueKind.Float ? value.AsFloat() == a[1].Value.AsFloat() : value == a[1].Value))
                { if (!Count) return new(i); matches++; }
            return new(Count ? matches : -1L);
        }
    }
    private sealed record FindHandler(string Id, bool Last) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            var array = a[0].Reference!;
            if (context.VariableInfo(array).Shape.Length != 1) throw new ScriptException("RANK", "FINDELEMENT 需要一维数组");
            if (a[0].Value.Kind != a[1].Value.Kind || a[0].Value.Kind == ValueKind.Float) throw new ScriptException("TYPE", "查找值和数组类型不匹配");
            int length = context.ArrayLength(array); long start = a[2].Value.AsInt(), end = a[3].IsOmitted ? length : a[3].Value.AsInt();
            if (start < 0 || start > length || end < 0 || end > length) throw new ScriptException("BOUNDS", "数组查找范围越界");
            if (start >= end) return new(-1L);
            try
            {
                Regex? regex = a[1].Value.Kind == ValueKind.String ? new(a[1].Value.AsString(), RegexOptions.None, TimeSpan.FromSeconds(1)) : null;
                for (long i = Last ? end - 1 : start; Last ? i >= start : i < end; i += Last ? -1 : 1)
                {
                    var value = context.Read(array with { Index = (int)i });
                    if (regex is null) { if (value == a[1].Value) return new(i); }
                    else
                    {
                        var match = regex.Match(value.AsString());
                        if (match.Success && (a[4].Value.AsInt() == 0 || match.Length == value.AsString().Length)) return new(i);
                    }
                }
            }
            catch (ArgumentException) { throw new ScriptException("REGEX", "数组查找正则表达式无效"); }
            catch (RegexMatchTimeoutException) { throw new ScriptException("REGEX_TIMEOUT", "数组查找正则表达式超时"); }
            return new(-1L);
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("PICKUPCHARA", "era.PICKUPCHARA", [], null, BuiltinEffect.Flow, new(ValueKind.Integer, CaptureReference: true)));
        handlers.Add(new PickHandler("era.PICKUPCHARA"));
        signatures.Add(new("$PICKUP_ARGUMENT", "era.audit.PICKUP_ARGUMENT", [new(ValueKind.Integer, CaptureReference: true), new(ValueKind.Integer)], null, BuiltinEffect.Query));
        handlers.Add(new PickArgumentHandler("era.audit.PICKUP_ARGUMENT"));
        foreach (string name in new[] { "MAXCARRAY", "MINCARRAY", "INRANGECARRAY" })
        {
            var parameters = new List<BuiltinParameter> { new(null, VariableName: true) };
            if (name == "INRANGECARRAY") { parameters.Add(new(ValueKind.Integer)); parameters.Add(new(ValueKind.Integer)); }
            parameters.Add(new(ValueKind.Integer, Default: new EraValue(0L))); parameters.Add(new(ValueKind.Integer, Default: new EraValue(0L)));
            string id = "era." + name;
            signatures.Add(new(name, id, parameters.ToImmutableArray(), ValueKind.Integer, BuiltinEffect.Query));
            parameters[0] = new(ValueKind.String); parameters.Add(new(ValueKind.Integer, Default: new EraValue(0L)));
            signatures.Add(new("$" + name, id + ".lowered", parameters.ToImmutableArray(), ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new CharacterQueryHandler(id, name)); handlers.Add(new CharacterQueryHandler(id + ".lowered", name));
        }
        signatures.Add(new("ARRAYSORT", "era.ARRAYSORT", [new(null, Reference: true), new(ValueKind.Integer, Default: new EraValue(1L)),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], null, BuiltinEffect.Query));
        handlers.Add(new SortHandler("era.ARRAYSORT"));
        signatures.Add(new("$ARRAY_QUERY_RANGE", "era.audit.ARRAY_QUERY_RANGE", [new(null, Reference: true), new(ValueKind.Integer),
            new(ValueKind.Integer, Default: new EraValue(0L))], null, BuiltinEffect.Query));
        handlers.Add(new QueryRangeHandler("era.audit.ARRAY_QUERY_RANGE"));
        signatures.Add(new("MATCH", "era.MATCH", [new(null, Reference: true), new(null), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new MatchArrayHandler("era.MATCH"));
        signatures.Add(new("STRJOIN", "era.STRJOIN", [new(null, Reference: true), new(ValueKind.String, Default: new EraValue(",")),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.String, BuiltinEffect.Query));
        handlers.Add(new JoinHandler("era.STRJOIN"));
        signatures.Add(new("INRANGEARRAY", "era.INRANGEARRAY", [new(null, Reference: true), new(ValueKind.Integer), new(ValueKind.Integer),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new InRangeHandler("era.INRANGEARRAY"));
        foreach (string name in new[] { "MAXARRAY", "MINARRAY" })
        {
            string id = "era." + name;
            signatures.Add(new(name, id, [new(null, Reference: true), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new ExtremeHandler(id, name == "MAXARRAY"));
        }
        foreach (string variable in new[] { "EXPLV", "PALAMLV" })
        {
            string name = "GET" + variable, id = "era." + name;
            signatures.Add(new(name, id, [new(ValueKind.Integer), new(ValueKind.Integer)], ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new LevelHandler(id, variable));
        }
        signatures.Add(new("SWAP", "era.SWAP", [new(null, Reference: true), new(null, Reference: true)], null, BuiltinEffect.Query, SameType: true));
        handlers.Add(new SwapHandler("era.SWAP"));
        signatures.Add(new("SUMARRAY", "era.SUMARRAY", [new(null, Reference: true), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query, NumericResult: true));
        handlers.Add(new SumHandler("era.SUMARRAY"));
        signatures.Add(new("$CHARACTER_RANGE", "era.CHARACTER_RANGE", [new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], null, BuiltinEffect.Query));
        handlers.Add(new CharacterRangeHandler("era.CHARACTER_RANGE"));
        signatures.Add(new("$CVARSET_START", "era.audit.CVARSET_START", [new(ValueKind.Integer)], null, BuiltinEffect.Query));
        handlers.Add(new CharacterBoundHandler("era.audit.CVARSET_START", false));
        signatures.Add(new("$CVARSET_END", "era.audit.CVARSET_END", [new(ValueKind.Integer)], null, BuiltinEffect.Query));
        handlers.Add(new CharacterBoundHandler("era.audit.CVARSET_END", true));
        signatures.Add(new("$ARRAYSHIFT_RANGE", "era.ARRAYSHIFT.range", [new(ValueKind.Integer),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new ShiftRangeHandler("era.ARRAYSHIFT.range"));
        signatures.Add(new("ARRAYSHIFT", "era.ARRAYSHIFT", [new(null, Reference: true), new(ValueKind.Integer), new(null),
            new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], null, BuiltinEffect.Query));
        handlers.Add(new ShiftHandler("era.ARRAYSHIFT"));
        signatures.Add(new("ARRAYREMOVE", "era.ARRAYREMOVE", [new(null, Reference: true), new(ValueKind.Integer), new(ValueKind.Integer)], null, BuiltinEffect.Query));
        handlers.Add(new RemoveHandler("era.ARRAYREMOVE"));
        signatures.Add(new("SUMCARRAY", "era.SUMCARRAY", [new(null, VariableName: true), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query, NumericResult: true));
        signatures.Add(new("$SUMCARRAY", "era.SUMCARRAY.lowered", [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query, new(ValueKind.Integer)));
        handlers.Add(new SumCharacterHandler("era.SUMCARRAY")); handlers.Add(new SumCharacterHandler("era.SUMCARRAY.lowered"));
        signatures.Add(new("$SUMCARRAYF", "era.SUMCARRAYF.lowered", [new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)),
            new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Float, BuiltinEffect.Query, new(ValueKind.Integer)));
        handlers.Add(new SumCharacterHandler("era.SUMCARRAYF.lowered"));
        foreach (string name in new[] { "FINDCHARA", "FINDLASTCHARA", "CMATCH" })
        {
            string id = "era." + name;
            signatures.Add(new(name, id, [new(null, VariableName: true), new(null), new(ValueKind.Integer, Default: new EraValue(0L)),
                new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
            signatures.Add(new("$" + name, id + ".lowered", [new(ValueKind.String), new(null), new(ValueKind.Integer, Default: new EraValue(0L)),
                new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query, new(ValueKind.Integer)));
            handlers.Add(new FindCharacterHandler(id, name == "FINDLASTCHARA", name == "CMATCH"));
            handlers.Add(new FindCharacterHandler(id + ".lowered", name == "FINDLASTCHARA", name == "CMATCH"));
        }
        foreach (string name in new[] { "FINDELEMENT", "FINDLASTELEMENT" })
        {
            string id = "era." + name;
            signatures.Add(new(name, id, [new(null, Reference: true), new(null), new(ValueKind.Integer, Default: new EraValue(0L)),
                new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new FindHandler(id, name == "FINDLASTELEMENT"));
        }
    }
}
