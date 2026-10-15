using System.Collections.Immutable;
using System.Xml;
using System.Xml.XPath;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.Builtins;

internal sealed partial class TableBuiltins
{
    private readonly Dictionary<string, XmlDocument> xmlDocuments = new(StringComparer.Ordinal);
    private sealed record PreparedXml(XmlDocument Document, bool Registered, BuiltinArgument Target, XmlNode[] Nodes);
    private readonly Dictionary<long, PreparedXml> preparedXml = [];
    private long nextXmlToken;
    public ImmutableDictionary<string, string> CaptureXml(ImmutableHashSet<string> names) => xmlDocuments
        .Where(p => names.Contains(p.Key)).ToImmutableDictionary(p => p.Key, p => p.Value.OuterXml, StringComparer.Ordinal);
    public Action PrepareRestoreXml(ImmutableDictionary<string, string> documents, ImmutableHashSet<string> names)
    {
        var candidate = new Dictionary<string, XmlDocument>(StringComparer.Ordinal);
        foreach (var pair in documents)
        {
            try { candidate.Add(pair.Key, ParseXml(pair.Value)); }
            catch (XmlException error) { throw new ScriptException("SAVE_XML", error.Message); }
        }
        return () => { foreach (string name in names) xmlDocuments.Remove(name); foreach (var pair in candidate) xmlDocuments[pair.Key] = pair.Value; };
    }
    public static void RegisterXml(List<BuiltinSignature> signatures, List<IBuiltinHandler> handlers)
    {
        void Add(string name, params BuiltinParameter[] parameters)
        {
            string id = "era." + name;
            signatures.Add(new(name, id, parameters.ToImmutableArray(), ValueKind.Integer, BuiltinEffect.Query));
            handlers.Add(new Handler(name, id));
        }
        Add("XML_DOCUMENT", new(null), new(ValueKind.String));
        Add("XML_EXIST", new BuiltinParameter(null));
        Add("XML_RELEASE", new BuiltinParameter(null));
        Add("$XML_BEGIN", new(null, CaptureReference: true), new(ValueKind.Integer), new(ValueKind.Integer));
        Add("$XML_SELECT", new(ValueKind.Integer), new(ValueKind.String));
        Add("$XML_END", new BuiltinParameter(ValueKind.Integer));
        foreach (string suffix in new[] { "", "_BYNAME" })
        {
            Add("XML_GET" + suffix, new(null), new(ValueKind.String), new(null, Default: new EraValue(0L), CaptureReference: true), new(ValueKind.Integer, Default: new EraValue(0L)));
            Add("XML_ADDNODE" + suffix, new(null, CaptureReference: true), new(ValueKind.String), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)));
            Add("XML_ADDATTRIBUTE" + suffix, new(null, CaptureReference: true), new(ValueKind.String), new(ValueKind.String), new(ValueKind.String, Default: new EraValue("")), new(ValueKind.Integer, Default: new EraValue(0L)), new(ValueKind.Integer, Default: new EraValue(0L)));
            Add("XML_REMOVENODE" + suffix, new(null, CaptureReference: true), new(ValueKind.String), new(ValueKind.Integer, Default: new EraValue(0L)));
            foreach (string name in new[] { "XML_GET", "XML_ADDNODE", "XML_ADDATTRIBUTE", "XML_REMOVENODE" })
            {
                var parameters = signatures.Single(s => s.Name == name + suffix).Parameters.SetItem(0, new(ValueKind.Integer));
                Add("$" + name + suffix, parameters.ToArray());
            }
        }
    }
    private static XmlDocument ParseXml(string text)
    {
        var document = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_000_000 });
        document.Load(reader); return document;
    }
    private EraValue Xml(string operation, ImmutableArray<BuiltinArgument> a, IBuiltinContext context)
    {
        try { return XmlCore(operation, a, context); }
        catch (Exception error) when (error is XmlException or XPathException or ArgumentException or InvalidOperationException)
        { throw new ScriptException("XML", error.Message); }
    }
    private EraValue XmlCore(string operation, ImmutableArray<BuiltinArgument> a, IBuiltinContext context)
    {
        if (operation == "$XML_BEGIN")
        {
            if (a[0].Value.Kind == ValueKind.Float) throw new ScriptException("TYPE", "XML 需要整数编号或字符串");
            bool saved = a[0].Value.Kind == ValueKind.Integer || a[1].Value.AsInt() != 0;
            XmlDocument target;
            if (saved) { if (!xmlDocuments.TryGetValue(a[0].Value.ToString(), out target!)) return new(0L); }
            else target = ParseXml(a[0].Value.AsString());
            if (!saved && a[2].Value.AsInt() != 0 && (a[0].Reference is not { } beginReference || context.VariableInfo(beginReference).ReadOnly))
                throw new ScriptException("REFERENCE", "XML 修改需要可写字符串变量");
            long token = ++nextXmlToken; preparedXml.Add(token, new(target, saved, a[0], [])); return new(token);
        }
        if (operation == "$XML_SELECT")
        {
            long token = a[0].Value.AsInt(); var staged = preparedXml[token];
            var matches = staged.Document.SelectNodes(a[1].Value.AsString())!.Cast<XmlNode>().ToArray();
            preparedXml[token] = staged with { Nodes = matches }; return new((long)matches.Length);
        }
        if (operation == "$XML_END") { preparedXml.Remove(a[0].Value.AsInt()); return new(0L); }
        PreparedXml? prepared = null;
        if (operation.StartsWith('$'))
        {
            long token = a[0].Value.AsInt(); prepared = preparedXml[token]; preparedXml.Remove(token);
            a = a.SetItem(0, prepared.Target); operation = operation[1..];
        }
        if (a[0].Value.Kind == ValueKind.Float) throw new ScriptException("TYPE", "XML 需要整数编号或字符串");
        string key = a[0].Value.ToString();
        if (operation == "XML_EXIST") return new(xmlDocuments.ContainsKey(key) ? 1L : 0L);
        if (operation == "XML_RELEASE") return new(xmlDocuments.Remove(key) ? 1L : 0L);
        if (operation == "XML_DOCUMENT")
        {
            if (xmlDocuments.ContainsKey(key)) return new(0L);
            xmlDocuments.Add(key, ParseXml(a[1].Value.AsString())); return new(1L);
        }
        bool registered = prepared?.Registered ?? (a[0].Value.Kind == ValueKind.Integer || operation.EndsWith("_BYNAME", StringComparison.Ordinal));
        XmlDocument document;
        if (prepared is not null) document = prepared.Document;
        else if (registered)
        {
            if (!xmlDocuments.TryGetValue(key, out document!)) return new(-1L);
        }
        else document = ParseXml(a[0].Value.AsString());
        bool get = operation.StartsWith("XML_GET", StringComparison.Ordinal);
        if (!get && !registered && (a[0].Reference is not { } reference || context.VariableInfo(reference).ReadOnly))
            throw new ScriptException("REFERENCE", "XML 修改需要可写字符串变量");
        var nodes = prepared?.Nodes ?? document.SelectNodes(a[1].Value.AsString())!.Cast<XmlNode>().ToArray();
        if (get)
        {
            if (!a[2].IsOmitted)
            {
                bool outputArray = a[2].Value.Kind == ValueKind.String;
                SlotReference? output = a[2].Reference;
                if (outputArray && (output is null || context.VariableInfo(output) is var spec && (spec.ReadOnly || spec.Shape.Length != 1 || spec.Scope == VariableScope.Character)))
                    throw new ScriptException("REFERENCE", "XML_GET 输出需要可写一维字符串数组");
                if (outputArray || a[2].Value.AsInt() != 0)
                {
                    int length = outputArray ? context.ArrayLength(output!) : context.VariableInfo("RESULTS").Length;
                    for (int i = 0; i < Math.Min(nodes.Length, length); i++)
                    {
                        var node = nodes[i];
                        string value = a[3].Value.AsInt() switch { 1 => node.InnerText, 2 => node.InnerXml, 3 => node.OuterXml, 4 => node.Name, _ => node.Value ?? "" };
                        if (outputArray) context.Write(output! with { Index = i }, new(value)); else context.SetStringResult(value, i);
                    }
                }
            }
            return new((long)nodes.Length);
        }
        if (nodes.Length == 0) return new(0L);
        bool remove = operation.StartsWith("XML_REMOVENODE", StringComparison.Ordinal);
        bool attribute = operation.StartsWith("XML_ADDATTRIBUTE", StringComparison.Ordinal);
        int method = remove ? 0 : unchecked((int)a[attribute ? 4 : 3].Value.AsInt());
        if (method is < 0 or > 2) method = 0;
        bool all = a[remove ? 2 : attribute ? 5 : 4].Value.AsInt() != 0;
        XmlNode? source = !remove && !attribute ? ParseXml(a[2].Value.AsString()).DocumentElement : null;
        bool Apply(XmlNode target)
        {
            if (remove)
            {
                if (target.ParentNode is null) return false;
                target.ParentNode.RemoveChild(target); return true;
            }
            if (attribute)
            {
                var item = document.CreateAttribute(a[2].Value.AsString()); item.Value = a[3].Value.AsString();
                if (method == 0 && target is XmlElement element) { element.Attributes.Append(item); return true; }
                if (method > 0 && target is XmlAttribute attr && attr.OwnerElement is { } owner)
                {
                    if (method == 1) owner.Attributes.InsertBefore(item, attr); else owner.Attributes.InsertAfter(item, attr);
                    return true;
                }
                return false;
            }
            var child = document.ImportNode(source!, true);
            if (method == 0) target.AppendChild(child);
            else if (target.ParentNode is not { } parent) return false;
            else if (method == 1) parent.InsertBefore(child, target); else parent.InsertAfter(child, target);
            return true;
        }
        if (nodes.Length == 1)
        {
            if (!Apply(nodes[0]) && (remove || method > 0)) return new(0L);
        }
        else if (all) foreach (var node in nodes) Apply(node);
        if (!registered) context.Write(a[0].Reference!, new(document.OuterXml));
        return new((long)nodes.Length);
    }
}
