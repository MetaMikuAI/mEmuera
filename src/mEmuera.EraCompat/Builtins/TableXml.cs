using System.Data;
using System.Globalization;
using System.Xml;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed partial class TableBuiltins
{
    private EraValue FromXml(string name, string schema, string data)
    {
        if (schema.Length + (long)data.Length > 32_000_000) throw new ScriptException("DT_LIMIT", "数据表 XML 超过容量限制");
        var candidate = new DataTable(name) { Locale = CultureInfo.InvariantCulture };
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32_000_000 };
            using (var reader = XmlReader.Create(new StringReader(schema), settings)) candidate.ReadXmlSchema(reader);
            if (candidate.Columns.Count > 1024 || candidate.Columns.Cast<DataColumn>().Any(c => !Types.Contains(c.DataType)))
                throw new ScriptException("DT_TYPE", "数据表 XML 字段类型或数量不支持");
            using (var reader = XmlReader.Create(new StringReader(data), settings)) candidate.ReadXml(reader);
            long cells = (long)candidate.Rows.Count * candidate.Columns.Count;
            if (cells + tables.Where(p => p.Key != name).Sum(p => (long)p.Value.Rows.Count * p.Value.Columns.Count) > 16_000_000)
                throw new ScriptException("DT_LIMIT", "数据表总量超限");
            foreach (var column in candidate.Columns.Cast<DataColumn>().Where(c => c.DataType == typeof(double)))
            {
                if (column.DefaultValue is double defaultValue && !double.IsFinite(defaultValue)
                    || candidate.Rows.Cast<DataRow>().Any(row => row[column] is double value && !double.IsFinite(value)))
                    throw new ScriptException("DT_DATA", "数据表浮点值必须是有限数值");
            }
        }
        catch (Exception error) when (error is XmlException or DataException or ArgumentException or InvalidOperationException or FormatException)
        { candidate.Dispose(); return new(0L); }
        catch { candidate.Dispose(); throw; }
        if (tables.Remove(name, out var previous)) previous.Dispose();
        tables.Add(name, candidate); return new(1L);
    }
}
