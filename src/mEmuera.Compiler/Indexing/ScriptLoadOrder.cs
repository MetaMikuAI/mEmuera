using mEmuera.Contracts;

namespace mEmuera.Compiler.Indexing;

internal static class ScriptLoadOrder
{
    private sealed class DirectoryNode(string name, string path)
    {
        public string Name = name, Path = path;
        public Dictionary<string, DirectoryNode> Children = new(StringComparer.OrdinalIgnoreCase);
        public List<SourceFile> Files = [];
    }
    public static SourceFile[] Select(SourceManifest manifest, CompileOptions options)
    {
        bool layout = manifest.Files.Any(f => f.Id.Path.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase));
        var files = manifest.Files.Where(f => f.Category is "ERH" or "ERB");
        if (options.Profile == "minimal-v1")
            return files.Where(f => !layout || f.Id.Path.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase) || f.Id.Path.StartsWith("ERH/", StringComparison.OrdinalIgnoreCase))
                .Where(f => options.SearchSubdirectories || f.Id.Path.Count(c => c == '/') <= (layout ? 1 : 0))
                .OrderBy(f => f.Category == "ERH" ? 0 : 1).ThenBy(f => f.Id.Path.Split('/').SkipLast(1).Any(p => p.Contains('#')) ? 0 : 1)
                .ThenBy(f => f.Id.Path.Count(c => c == '/')).ThenBy(f => f.Id.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (layout) files = files.Where(f => f.Id.Path.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase));
        var root = new DirectoryNode("", ""); var nodes = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        DirectoryNode AddDirectory(string path)
        {
            var node = root; string current = "";
            foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Length == 0 ? part : current + "/" + part;
                if (!node.Children.TryGetValue(part, out var child)) { child = new(part, current); node.Children.Add(part, child); nodes[current] = child; }
                node = child;
            }
            return node;
        }
        foreach (string path in manifest.DirectoryOrder) AddDirectory(path);
        foreach (var file in files.OrderBy(f => f.DiscoveryOrder)) AddDirectory(file.Id.Path.Contains('/') ? file.Id.Path[..file.Id.Path.LastIndexOf('/')] : "").Files.Add(file);
        if (layout) root = nodes.TryGetValue("ERB", out var erb) ? erb : AddDirectory("ERB");
        IEnumerable<SourceFile> Walk(DirectoryNode node, string category)
        {
            var current = node.Files.Where(f => f.Category == category);
            foreach (var file in options.SortFiles ? current.OrderBy(f => f.Id.Path, StringComparer.InvariantCulture) : current) yield return file;
            if (!options.SearchSubdirectories) yield break;
            IEnumerable<DirectoryNode> children = node.Children.Values;
            if (options.SortFiles) children = children.OrderBy(n => n.Name, StringComparer.InvariantCulture);
            foreach (var child in children) foreach (var file in Walk(child, category)) yield return file;
        }
        var result = Walk(root, "ERH").ToList(); var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priority = options.SearchSubdirectories ? nodes.Values.Where(n => n != root && n.Name.Contains('#') && (root.Path.Length == 0 || n.Path.StartsWith(root.Path + "/", StringComparison.OrdinalIgnoreCase)))
            : root.Children.Values.Where(n => n.Name.Contains('#'));
        foreach (var node in priority) foreach (var file in Walk(node, "ERB")) if (loaded.Add(file.Id.Path)) result.Add(file);
        foreach (var file in Walk(root, "ERB")) if (loaded.Add(file.Id.Path)) result.Add(file);
        return result.ToArray();
    }
}
