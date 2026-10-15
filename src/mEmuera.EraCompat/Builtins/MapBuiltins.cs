using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class MapBuiltins
{
    private sealed record AssignmentHandler(string Id, bool Check) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> a)
        {
            context.DynamicAssignment(a[0].Value.AsString(), Check ? (ValueKind)a[1].Value.AsInt() : a[1].Value.Kind,
                Check ? null : a[1].Value, Check && a[2].Value.AsInt() != 0);
            return null;
        }
    }
    private sealed record MethodHandler(string Id, ValueKind Kind) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
            => context.Method(arguments[0].Value.AsString(), Kind, arguments[1].IsOmitted ? null : arguments[1].Value, arguments.RemoveRange(0, 2));
    }
    private sealed record VariableHandler(string Id, ValueKind Kind) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
        {
            context.DynamicVariable(arguments[0].Value.AsString(), Kind, arguments[1].IsOmitted ? null : arguments[1].Value);
            return null;
        }
    }
    private sealed record Handler(string Name, string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
        {
            string name = arguments[0].Value.AsString();
            var maps = context.StringMaps;
            bool exists = maps.TryGetValue(name, out var map);
            if (Name == "MAP_CREATE") { if (exists) return new(0L); maps.Add(name, new(StringComparer.Ordinal)); return new(1L); }
            if (Name == "MAP_EXIST") return new(exists ? 1L : 0L);
            if (Name == "MAP_RELEASE") { maps.Remove(name); return new(1L); }
            if (Name == "MAP_GET") return new(map?.GetValueOrDefault(arguments[1].Value.AsString()) ?? "");
            if (!exists) return new(-1L);
            switch (Name)
            {
                case "MAP_SIZE": return new((long)map!.Count);
                case "MAP_CLEAR": map!.Clear(); return new(1L);
                case "MAP_HAS": return new(map!.ContainsKey(arguments[1].Value.AsString()) ? 1L : 0L);
                case "MAP_SET": map![arguments[1].Value.AsString()] = arguments[2].Value.AsString(); return new(1L);
                case "MAP_REMOVE": map!.Remove(arguments[1].Value.AsString()); return new(1L);
                default: throw new ScriptException("MAP_OPERATION", "未知映射操作");
            }
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        signatures.Add(new("SETVAR", "era.SETVAR", [new(ValueKind.String), new(null), new(ValueKind.Integer, Default: new EraValue(0L))], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new AssignmentHandler("era.SETVAR", false));
        signatures.Add(new("$SETVAR_CHECK", "era.SETVAR_CHECK", [new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer)], ValueKind.Integer, BuiltinEffect.Query));
        handlers.Add(new AssignmentHandler("era.SETVAR_CHECK", true));
        foreach (var kind in new[] { ValueKind.Integer, ValueKind.String })
        {
            string name = kind == ValueKind.Integer ? "GETVAR" : "GETVARS", id = "era." + name;
            signatures.Add(new(name, id, [new(ValueKind.String), new(kind, Default: EraValue.Zero(kind))], kind, BuiltinEffect.Query));
            handlers.Add(new VariableHandler(id, kind));
            name = kind == ValueKind.Integer ? "GETMETH" : "GETMETHS"; id = "era." + name;
            signatures.Add(new(name, id, [new(ValueKind.String), new(kind, Default: EraValue.Zero(kind))], kind, BuiltinEffect.Query,
                new(null, Default: new EraValue(0L), CaptureReference: true)));
            handlers.Add(new MethodHandler(id, kind));
        }
        foreach (string name in new[] { "MAP_CREATE", "MAP_EXIST", "MAP_RELEASE", "MAP_CLEAR", "MAP_SIZE", "MAP_HAS", "MAP_SET", "MAP_REMOVE", "MAP_GET" })
        {
            int count = name == "MAP_SET" ? 3 : name is "MAP_HAS" or "MAP_REMOVE" or "MAP_GET" ? 2 : 1;
            string id = "era." + name;
            signatures.Add(new(name, id, Enumerable.Repeat(new BuiltinParameter(ValueKind.String), count).ToImmutableArray(),
                name == "MAP_GET" ? ValueKind.String : ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new Handler(name, id));
        }
    }
}
