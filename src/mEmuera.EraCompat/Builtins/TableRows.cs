using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed partial class TableBuiltins
{
    private readonly long timeBase = DateTime.Now.Ticks;
    private readonly Stopwatch time = Stopwatch.StartNew();
    private sealed class CellOperation(DataTable table)
    {
        public DataTable Table { get; } = table;
        public DataRow? Row { get; set; }
        public string Column { get; set; } = "";
        public Type Type { get; set; } = typeof(long);
    }
    private readonly Dictionary<long, CellOperation> cellOperations = [];
    private long nextCellOperation;
    private EraValue PreparedCell(string operation, ImmutableArray<BuiltinArgument> a)
    {
        if (operation == "$DT_CELL_BEGIN")
        {
            if (!tables.TryGetValue(a[0].Value.AsString(), out var table)) return new(0L);
            if (cellOperations.Count >= 1024) throw new ScriptException("DT_LIMIT", "未完成的数据表操作超限");
            long token = ++nextCellOperation; cellOperations.Add(token, new(table)); return new(token);
        }
        long id = a[0].Value.AsInt();
        if (!cellOperations.TryGetValue(id, out var pending)) throw new ScriptException("DT_OPERATION", "数据表操作状态无效");
        if (operation == "$DT_CELL_SET_PREPARE")
        {
            string name = a[2].Value.AsString(); long result = 1;
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase)) result = 0;
            else
            {
                long index = a[1].Value.AsInt(); bool byId = a[3].Value.AsInt() != 0;
                var row = byId ? pending.Table.Rows.Find(index) : index >= 0 && index < pending.Table.Rows.Count ? pending.Table.Rows[(int)index] : null;
                var column = pending.Table.Columns[name];
                long kind = a[4].Value.AsInt();
                if (row is null || column is null) result = -3;
                else if (kind >= 0 && !CellTypeMatches(column.DataType, (ValueKind)kind)) result = -2;
                else { pending.Row = row; pending.Column = name; pending.Type = column.DataType; }
            }
            if (result != 1) cellOperations.Remove(id);
            return new(result);
        }
        cellOperations.Remove(id);
        if (operation is "$DT_CELL_GET" or "$DT_CELL_GETS") return Cell(operation[1..], a, pending.Table);
        if (operation != "$DT_CELL_SET_COMMIT" || pending.Row is null) throw new ScriptException("DT_OPERATION", "数据表写入状态无效");
        object value = a[1].IsOmitted ? DBNull.Value : pending.Type == typeof(string) ? a[1].Value.AsString()
            : pending.Type == typeof(double) ? FiniteTableFloat(a[1].Value.AsFloat())
            : IntegerCellValue(a[1].Value.AsInt(), pending.Table.Columns[pending.Column]!.DataType);
        pending.Row[pending.Column] = value; return new(1L);
    }
    private static bool CellTypeMatches(Type type, ValueKind kind) => type == typeof(double)
        ? kind is ValueKind.Integer or ValueKind.Float : kind == (type == typeof(string) ? ValueKind.String : ValueKind.Integer);
    private static double FiniteTableFloat(double value) => double.IsFinite(value) ? value
        : throw new ScriptException("DT_DATA", "数据表浮点值必须是有限数值");
    private static object IntegerCellValue(long number, Type type)
    {
        if (type == typeof(sbyte)) return (sbyte)Math.Clamp(number, sbyte.MinValue, sbyte.MaxValue);
        if (type == typeof(short)) return (short)Math.Clamp(number, short.MinValue, short.MaxValue);
        if (type == typeof(int)) return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
        return number;
    }
    private static object CellValue(DataColumn column, EraValue? value)
    {
        if (value is null) return DBNull.Value;
        if (column.DataType == typeof(string)) return value.Value.AsString();
        if (column.DataType == typeof(double)) return FiniteTableFloat(value.Value.AsFloat());
        return IntegerCellValue(value.Value.AsInt(), column.DataType);
    }
    private static SlotReference TableArray(BuiltinArgument argument, IBuiltinContext context, ValueKind? kind = null, bool writable = false)
    {
        var reference = argument.Reference ?? throw new ScriptException("REFERENCE", "数据表参数需要数组");
        var specification = context.VariableInfo(reference);
        if (specification.Shape.Length != 1) throw new ScriptException("RANK", "数据表参数需要一维数组");
        if (kind is { } expected && specification.Kind != expected) throw new ScriptException("TYPE", "数据表数组类型不匹配");
        if (writable && specification.ReadOnly) throw new ScriptException("READ_ONLY", "查询结果数组不可写");
        return reference;
    }
    private EraValue Rows(string operation, ImmutableArray<BuiltinArgument> a, IBuiltinContext context)
    {
        if (!tables.TryGetValue(a[0].Value.AsString(), out var table))
            return operation == "DT_CELL_GETS" ? new("") : new(operation == "DT_CELL_GET" ? 0L : -1L);
        if (operation is "DT_ROW_ADD" or "DT_ROW_SET")
        {
            bool add = operation == "DT_ROW_ADD"; int first = add ? 1 : 2;
            var row = add ? table.NewRow() : table.Rows.Find(a[1].Value.AsInt());
            if (row is null) return new(-2L);
            if (add) row[0] = timeBase + time.Elapsed.Ticks;
            void Set(string name, EraValue? value)
            {
                if (name == "id") throw new ScriptException("DT_COLUMN", "不能修改数据表主键");
                var column = table.Columns[name] ?? throw new ScriptException("DT_COLUMN", "数据表字段不存在");
                if (value is { } supplied && supplied.Kind != (column.DataType == typeof(string) ? ValueKind.String : ValueKind.Integer))
                    throw new ScriptException("DT_TYPE", "数据表值类型不匹配");
                row[column] = CellValue(column, value);
            }
            long count;
            if (a.Length == first + 3)
            {
                var names = TableArray(a[first], context, ValueKind.String);
                var values = TableArray(a[first + 1], context);
                count = Math.Min(a[first + 2].Value.AsInt(), Math.Min(context.ArrayLength(names), context.ArrayLength(values)));
                for (int i = 0; i < count; i++) Set(context.Read(names with { Index = i }).AsString(), context.Read(values with { Index = i }));
            }
            else
            {
                if ((a.Length - first) % 2 != 0) throw new ScriptException("ARGUMENT_COUNT", "数据表字段和值必须成对");
                count = (a.Length - first) / 2;
                for (int i = first; i < a.Length; i += 2) Set(a[i].Value.AsString(), a[i + 1].IsOmitted ? null : a[i + 1].Value);
            }
            if (!add) return new(count);
            if (tables.Values.Sum(t => (long)t.Rows.Count * t.Columns.Count) + table.Columns.Count > 16_000_000) throw new ScriptException("DT_LIMIT", "数据表总量超限");
            table.Rows.Add(row); return new((long)row[0]);
        }
        if (operation == "DT_ROW_REMOVE")
        {
            var rows = new HashSet<DataRow>();
            if (a[2].IsOmitted) { if (table.Rows.Find(a[1].Value.AsInt()) is { } found) rows.Add(found); }
            else
            {
                var reference = TableArray(a[1], context, ValueKind.Integer);
                long count = Math.Min(a[2].Value.AsInt(), context.ArrayLength(reference));
                for (int i = 0; i < count; i++) if (table.Rows.Find(context.Read(reference with { Index = i }).AsInt()) is { } found) rows.Add(found);
            }
            foreach (var row in rows) table.Rows.Remove(row);
            return new((long)rows.Count);
        }
        if (operation == "DT_SELECT")
        {
            var rows = table.Select(a[1].Value.AsString(), a[2].Value.AsString());
            if (a[3].IsOmitted)
            {
                int count = Math.Min(rows.Length, context.VariableInfo("RESULT").Length - 1);
                for (int i = 0; i < count; i++) context.SetResult((long)rows[i][0], i + 1);
                context.SetResult(rows.Length);
            }
            else
            {
                var output = TableArray(a[3], context, ValueKind.Integer, writable: true);
                int count = Math.Min(rows.Length, context.ArrayLength(output));
                for (int i = 0; i < count; i++) context.Write(output with { Index = i }, new((long)rows[i][0]));
            }
            return new((long)rows.Length);
        }
        return Cell(operation, a, table);
    }
    private static EraValue Cell(string operation, ImmutableArray<BuiltinArgument> a, DataTable table)
    {
        bool set = operation == "DT_CELL_SET", byId = a[set ? 4 : 3].Value.AsInt() != 0;
        long index = a[1].Value.AsInt(); string columnName = a[2].Value.AsString();
        var target = byId ? table.Rows.Find(index) : index >= 0 && index < table.Rows.Count ? table.Rows[(int)index] : null;
        var field = table.Columns[columnName];
        if (set)
        {
            if (columnName.Equals("id", StringComparison.OrdinalIgnoreCase)) return new(0L);
            if (target is null || field is null) return new(-3L);
            if (!a[3].IsOmitted && !CellTypeMatches(field.DataType, a[3].Value.Kind)) return new(-2L);
            target[field] = CellValue(field, a[3].IsOmitted ? null : a[3].Value); return new(1L);
        }
        if (target is null || field is null) return operation == "DT_CELL_GETS" ? new("") : new(operation == "DT_CELL_ISNULL" ? -2L : 0L);
        object cell = target[field];
        if (operation == "DT_CELL_ISNULL") return new(cell is DBNull ? 1L : 0L);
        if (operation == "DT_CELL_GETS") return new(cell is DBNull ? "" : byId ? (string)cell : Convert.ToString(cell, CultureInfo.InvariantCulture)!);
        return new(cell is DBNull ? 0L : Convert.ToInt64(cell, CultureInfo.InvariantCulture));
    }
}
