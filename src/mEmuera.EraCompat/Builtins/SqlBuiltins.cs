using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal static class SqlBuiltins
{
    private sealed record Handler(string Name, string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
        {
            context.Defer(ServiceKind.Database, new ScriptDatabasePayload(Name,
                arguments.Select(a => a.Value).ToImmutableArray(), arguments.Select(a => a.IsOmitted).ToImmutableArray()));
            return null;
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        void Add(string name, ValueKind kind, params BuiltinParameter[] parameters)
        {
            string id = "era." + name;
            signatures.Add(new(name, id, parameters.ToImmutableArray(), kind, BuiltinEffect.Query));
            handlers.Add(new Handler(name, id));
        }
        Add("SQL_CONNECT", ValueKind.Integer, new(ValueKind.String), new(ValueKind.String, Default: new EraValue("Data Source=:memory:")));
        Add("SQL_DISCONNECT", ValueKind.Integer, new BuiltinParameter(ValueKind.String));
        foreach (string suffix in new[] { "NONQUERY", "READER", "SCALAR_LONG", "SCALAR_STRING", "SCALAR_FLOAT" })
        {
            var kind = suffix == "SCALAR_STRING" ? ValueKind.String : suffix == "SCALAR_FLOAT" ? ValueKind.Float : ValueKind.Integer;
            Add("SQL_EXECUTE_" + suffix, kind, new(ValueKind.String), new(ValueKind.String));
            Add("SQL_P_EXECUTE_" + suffix, kind, new(ValueKind.String), new(ValueKind.String));
            signatures[^1] = signatures[^1] with { Variadic = new(ValueKind.String, Default: new EraValue("")) };
        }
        foreach (string name in new[] { "SQL_READER_READ", "SQL_READER_CLOSE" }) Add(name, ValueKind.Integer, new BuiltinParameter(ValueKind.Integer));
        foreach (string suffix in new[] { "GET_LONG", "GET_STRING", "GET_FLOAT", "IS_NULL", "ISNULL" })
            Add("SQL_READER_" + suffix, suffix == "GET_STRING" ? ValueKind.String : suffix == "GET_FLOAT" ? ValueKind.Float : ValueKind.Integer,
                new(ValueKind.Integer), new(ValueKind.Integer));
        Add("SQL_IMPORT_MAP_XML", ValueKind.Integer, new(ValueKind.String), new(ValueKind.String), new(ValueKind.String));
    }
}
