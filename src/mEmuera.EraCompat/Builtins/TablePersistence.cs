using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed partial class TableBuiltins
{
    public void ClearTableRows(ImmutableHashSet<string> names)
    {
        foreach (string name in names)
            if (tables.TryGetValue(name, out var table)) table.Clear();
    }

    public ImmutableArray<TableSnapshot> Capture(ImmutableHashSet<string> names) => tables.Where(p => names.Contains(p.Key)).Select(p =>
        new TableSnapshot(p.Key, p.Value.CaseSensitive,
            p.Value.Columns.Cast<DataColumn>().Select(c => new TableColumnSnapshot(c.ColumnName, Array.IndexOf(Types, c.DataType) + 1, c.AllowDBNull)
            { DefaultValue = CaptureTableValue(c.DefaultValue) }).ToImmutableArray(),
            p.Value.Rows.Cast<DataRow>().Select(r => r.ItemArray.Select(CaptureTableValue).ToImmutableArray()).ToImmutableArray())
        {
            PrimaryKey = p.Value.PrimaryKey.Select(c => c.ColumnName).ToImmutableArray(),
            UniqueConstraints = CaptureTableConstraints(p.Value)
        }).ToImmutableArray();

    private static ImmutableArray<TableUniqueConstraintSnapshot> CaptureTableConstraints(DataTable table)
    {
        if (table.Constraints.Cast<Constraint>().Any(c => c is not UniqueConstraint))
            throw new ScriptException("SAVE_TABLE", "数据表约束类型不受支持");
        return table.Constraints.Cast<UniqueConstraint>().Select(c => new TableUniqueConstraintSnapshot(c.ConstraintName,
            c.Columns.Select(column => column.ColumnName).ToImmutableArray(), c.IsPrimaryKey)).ToImmutableArray();
    }

    private static EraValue? CaptureTableValue(object? value) => value switch
    {
        null or DBNull => null, string text => new EraValue(text),
        double number => double.IsFinite(number) ? new EraValue(number) : throw new ScriptException("DT_DATA", "数据表浮点值必须是有限数值"),
        _ => new EraValue(Convert.ToInt64(value, CultureInfo.InvariantCulture))
    };

    private static object RestoreTableValue(DataColumn column, EraValue value)
    {
        ValueKind kind = column.DataType == typeof(string) ? ValueKind.String : column.DataType == typeof(double) ? ValueKind.Float : ValueKind.Integer;
        if (value.Kind != kind || kind == ValueKind.String && value.Text is null || kind == ValueKind.Float && !double.IsFinite(value.Float))
            throw new ScriptException("SAVE_TABLE", "存档数据表值类型错误");
        return kind switch
        {
            ValueKind.String => value.Text, ValueKind.Float => value.Float,
            _ => Convert.ChangeType(value.Integer, column.DataType, CultureInfo.InvariantCulture)
        };
    }

    private static void RestoreTableConstraints(DataTable table, TableSnapshot saved)
    {
        if (saved.PrimaryKey is not { } keys)
        {
            if (saved.UniqueConstraints is not null || table.Columns[0].ColumnName != "id" || table.Columns[0].DataType != typeof(long) || table.Columns[0].AllowDBNull)
                throw new ScriptException("SAVE_TABLE", "存档数据表主键无效");
            table.PrimaryKey = [table.Columns[0]];
            return;
        }
        if (keys.IsDefault || keys.Length > table.Columns.Count || saved.UniqueConstraints is not { } constraints || constraints.IsDefault || constraints.Length > 1024)
            throw new ScriptException("SAVE_TABLE", "存档数据表约束规格无效");
        DataColumn[] Columns(ImmutableArray<string> names)
        {
            if (names.IsDefault || names.Length > table.Columns.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new ScriptException("SAVE_TABLE", "存档数据表约束字段无效");
            return names.Select(name => table.Columns.Cast<DataColumn>().FirstOrDefault(c => c.ColumnName == name)
                ?? throw new ScriptException("SAVE_TABLE", "存档数据表约束字段不存在")).ToArray();
        }
        var primaryColumns = Columns(keys);
        if (primaryColumns.Any(c => c.AllowDBNull)) throw new ScriptException("SAVE_TABLE", "存档数据表主键允许空值");
        int primaryCount = 0;
        foreach (var constraint in constraints)
        {
            if (constraint is null || string.IsNullOrEmpty(constraint.Name) || constraint.Columns.IsDefaultOrEmpty)
                throw new ScriptException("SAVE_TABLE", "存档数据表唯一约束无效");
            var columns = Columns(constraint.Columns);
            if (constraint.PrimaryKey && (++primaryCount != 1 || !constraint.Columns.SequenceEqual(keys, StringComparer.Ordinal)))
                throw new ScriptException("SAVE_TABLE", "存档数据表主键约束不匹配");
            table.Constraints.Add(new UniqueConstraint(constraint.Name, columns));
        }
        if (primaryCount != (keys.IsEmpty ? 0 : 1)) throw new ScriptException("SAVE_TABLE", "存档数据表主键约束缺失");
        table.PrimaryKey = primaryColumns;
    }

    public Action PrepareRestore(ImmutableArray<TableSnapshot> snapshots, ImmutableHashSet<string> names)
    {
        var candidate = new Dictionary<string, DataTable>(StringComparer.Ordinal); long cells = 0;
        if (snapshots.IsDefault || snapshots.Length > names.Count) throw new ScriptException("SAVE_TABLE", "存档数据表集合无效");
        try
        {
            foreach (var saved in snapshots)
            {
                if (saved is null || !names.Contains(saved.Name) || candidate.ContainsKey(saved.Name) || saved.Columns.IsDefaultOrEmpty || saved.Rows.IsDefault
                    || saved.Columns.Length > 1024 || (cells += (long)saved.Columns.Length * saved.Rows.Length) > 16_000_000)
                    throw new ScriptException("SAVE_TABLE", "存档数据表规格无效");
                var table = new DataTable(saved.Name) { CaseSensitive = saved.CaseSensitive, Locale = CultureInfo.InvariantCulture };
                foreach (var column in saved.Columns)
                {
                    if (column is null || column.Type < 1 || column.Type > Types.Length || string.IsNullOrEmpty(column.Name)) throw new ScriptException("SAVE_TABLE", "存档数据表字段无效");
                    var restored = new DataColumn(column.Name, Types[column.Type - 1]) { AllowDBNull = column.Nullable };
                    if (column.DefaultValue is { } defaultValue)
                    {
                        restored.DefaultValue = RestoreTableValue(restored, defaultValue);
                    }
                    table.Columns.Add(restored);
                }
                RestoreTableConstraints(table, saved);
                foreach (var row in saved.Rows)
                {
                    if (row.IsDefault || row.Length != table.Columns.Count) throw new ScriptException("SAVE_TABLE", "存档数据行规格无效");
                    var values = new object[row.Length];
                    for (int i = 0; i < row.Length; i++)
                    {
                        var value = row[i];
                        if (value is null) { values[i] = DBNull.Value; continue; }
                        values[i] = RestoreTableValue(table.Columns[i], value.Value);
                    }
                    table.Rows.Add(values);
                }
                candidate.Add(saved.Name, table);
            }
            foreach (string name in names)
                if (!candidate.ContainsKey(name) && tables.TryGetValue(name, out var existing)) candidate.Add(name, existing.Clone());
        }
        catch (Exception error) when (error is DataException or ArgumentException or InvalidCastException or OverflowException)
        { throw new ScriptException("SAVE_TABLE", "存档数据表无效: " + error.Message); }
        return () => { foreach (string name in names) tables.Remove(name); foreach (var pair in candidate) tables[pair.Key] = pair.Value; };
    }
}
