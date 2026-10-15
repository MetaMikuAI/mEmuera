using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed partial class TableBuiltins : IScriptExtensions
{
    private readonly Dictionary<string, DataTable> tables = new(StringComparer.Ordinal);
    private static readonly Type[] Types = [typeof(sbyte), typeof(short), typeof(int), typeof(long), typeof(string), typeof(double)];
    private static readonly string[] TypeNames = ["int8", "int16", "int32", "int64", "string"];
    private sealed record Handler(string Name, string Id) : IBuiltinHandler
    {
        public EraValue? Execute(IBuiltinContext context, ImmutableArray<BuiltinArgument> arguments)
        {
            var result = context.Extension(Name, arguments);
            return Name == "DT_COLUMN_OPTIONS" ? null : result;
        }
    }
    public static void Register(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        void Add(string name, params BuiltinParameter[] parameters)
        {
            string id = "era." + name;
            signatures.Add(new(name, id, parameters.ToImmutableArray(), ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new Handler(name, id));
        }
        foreach (string name in new[] { "DT_CREATE", "DT_EXIST", "DT_RELEASE", "DT_CLEAR", "DT_COLUMN_LENGTH", "DT_ROW_LENGTH" }) Add(name, new BuiltinParameter(ValueKind.String));
        Add("DT_NOCASE", new(ValueKind.String), new(ValueKind.Integer));
        Add("DT_COLUMN_OPTIONS", new(ValueKind.String), new(ValueKind.String));
        signatures[^1] = signatures[^1] with { ReturnType = null, Variadic = new(null) };
        Add("DT_FROMXML", new(ValueKind.String), new(ValueKind.String), new(ValueKind.String));
        foreach (string name in new[] { "DT_COLUMN_EXIST", "DT_COLUMN_REMOVE" }) Add(name, new(ValueKind.String), new(ValueKind.String));
        Add("DT_COLUMN_ADD", new(ValueKind.String), new(ValueKind.String), new(null, Default: new EraValue("string")), new(ValueKind.Integer, Default: new EraValue(1L)));
        Add("DT_ROW_ADD", new BuiltinParameter(ValueKind.String));
        signatures[^1] = signatures[^1] with { Variadic = new(null, Default: new EraValue(0L), CaptureReference: true) };
        Add("DT_ROW_SET", new(ValueKind.String), new(ValueKind.Integer));
        signatures[^1] = signatures[^1] with { Variadic = new(null, Default: new EraValue(0L), CaptureReference: true) };
        Add("DT_ROW_REMOVE", new(ValueKind.String), new(ValueKind.Integer, CaptureReference: true), new(ValueKind.Integer, Default: new EraValue(0L)));
        foreach (string name in new[] { "DT_CELL_GET", "DT_CELL_GETS", "DT_CELL_ISNULL" })
        {
            Add(name, new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)));
            if (name == "DT_CELL_GETS") signatures[^1] = signatures[^1] with { ReturnType = ValueKind.String };
        }
        Add("DT_CELL_SET", new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.String), new(null, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)));
        Add("DT_SELECT", new(ValueKind.String), new(ValueKind.String, Default: new EraValue("")), new(ValueKind.String, Default: new EraValue("")), new(ValueKind.Integer, Default: new EraValue(0L), CaptureReference: true));
        Add("$DT_CELL_BEGIN", new BuiltinParameter(ValueKind.String));
        foreach (string name in new[] { "$DT_CELL_GET", "$DT_CELL_GETS" })
        {
            Add(name, new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)));
            if (name == "$DT_CELL_GETS") signatures[^1] = signatures[^1] with { ReturnType = ValueKind.String };
        }
        Add("$DT_CELL_SET_PREPARE", new(ValueKind.Integer), new(ValueKind.Integer), new(ValueKind.String), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("$DT_CELL_SET_COMMIT", new(ValueKind.Integer), new(null, Default: new EraValue(0L)));
    }
    public EraValue Execute(string operation, ImmutableArray<BuiltinArgument> arguments, IBuiltinContext context)
    {
        if (operation.StartsWith("XML_", StringComparison.Ordinal) || operation.StartsWith("$XML_", StringComparison.Ordinal)) return Xml(operation, arguments, context);
        if (operation == "DT_COLUMN_OPTIONS" && (!tables.TryGetValue(arguments[0].Value.AsString(), out var target) || !target.Columns.Contains(arguments[1].Value.AsString())))
        {
            long result = target is null ? -1 : 0; context.SetResult(result); return new(result);
        }
        try { return operation.StartsWith("$DT_CELL_", StringComparison.Ordinal) ? PreparedCell(operation, arguments)
            : operation.StartsWith("DT_CELL_", StringComparison.Ordinal) || operation is "DT_ROW_ADD" or "DT_ROW_SET" or "DT_ROW_REMOVE" or "DT_SELECT"
            ? Rows(operation, arguments, context) : ExecuteCore(operation, arguments); }
        catch (Exception error) when (error is DataException or ArgumentException or InvalidOperationException or FormatException or OverflowException or InvalidCastException)
        { throw new ScriptException("DT_DATA", error.Message); }
    }
    private EraValue ExecuteCore(string operation, ImmutableArray<BuiltinArgument> a)
    {
        string name = a[0].Value.AsString(); bool exists = tables.TryGetValue(name, out var table);
        if (operation == "DT_EXIST") return new(exists ? 1L : 0L);
        if (operation == "DT_FROMXML") return FromXml(name, a[1].Value.AsString(), a[2].Value.AsString());
        if (operation == "DT_RELEASE") { tables.Remove(name); return new(1L); }
        if (operation == "DT_CREATE")
        {
            if (exists) return new(0L);
            table = new DataTable(name) { CaseSensitive = true, Locale = CultureInfo.InvariantCulture };
            var id = table.Columns.Add("id", typeof(long)); id.AllowDBNull = false; id.Unique = true; table.PrimaryKey = [id];
            tables.Add(name, table); return new(1L);
        }
        if (!exists) return new(-1L);
        switch (operation)
        {
            case "DT_CLEAR": table!.Clear(); return new(1L);
            case "DT_NOCASE": table!.CaseSensitive = a[1].Value.AsInt() == 0; return new(1L);
            case "DT_COLUMN_LENGTH": return new((long)table!.Columns.Count);
            case "DT_ROW_LENGTH": return new((long)table!.Rows.Count);
        }
        string column = a[1].Value.AsString(); var existing = table!.Columns[column];
        if (operation == "DT_COLUMN_OPTIONS")
        {
            if (existing is null) return new(0L);
            foreach (var argument in a.Skip(2))
            {
                if ((existing.DataType == typeof(string)) != (argument.Value.Kind == ValueKind.String) || argument.Value.Kind == ValueKind.Float)
                    throw new ScriptException("DT_TYPE", "字段默认值类型不匹配");
                existing.DefaultValue = CellValue(existing, argument.Value);
            }
            return new(1L);
        }
        if (operation == "DT_COLUMN_EXIST") return new(existing is null ? 0L : existing.DataType == typeof(double) ? long.MaxValue : Array.IndexOf(Types, existing.DataType) + 1L);
        if (operation == "DT_COLUMN_REMOVE")
        {
            if (existing is null || column.Equals("id", StringComparison.OrdinalIgnoreCase)) return new(0L);
            table.Columns.Remove(existing); return new(1L);
        }
        if (operation == "DT_COLUMN_ADD")
        {
            if (existing is not null) return new(0L);
            long type = a[2].Value.Kind == ValueKind.String ? Array.IndexOf(TypeNames, a[2].Value.AsString()) : a[2].Value.AsInt() - 1;
            if (type < 0 || type >= TypeNames.Length) throw new ScriptException("DT_TYPE", "不支持的数据表字段类型");
            var added = table.Columns.Add(column, Types[(int)type]); added.AllowDBNull = a[3].Value.AsInt() != 0; return new(1L);
        }
        throw new ScriptException("DT_OPERATION", "未实现的数据表操作: " + operation);
    }
}
