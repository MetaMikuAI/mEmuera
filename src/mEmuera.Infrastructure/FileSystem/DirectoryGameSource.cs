using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.FileSystem;

public sealed class GamePathResolver
{
    public string SourceRoot { get; }
    public string DataRoot { get; }
    public GamePathResolver(string sourceRoot, string dataRoot, bool allowSharedRoot = false)
    {
        SourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        DataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (IsInside(SourceRoot, DataRoot) && !(allowSharedRoot && SourceRoot.Equals(DataRoot, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("data-dir 必须在游戏源目录之外");
    }
    private static bool IsInside(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string Resolve(string root, string logical)
    {
        logical = logical.Replace('\\', '/');
        if (Path.IsPathRooted(logical) || logical.Split('/').Any(x => x == ".."))
            throw new IOException("路径越界: " + logical);
        string path = Path.GetFullPath(Path.Combine(root, logical));
        if (!IsInside(root, path)) throw new IOException("路径越界: " + logical);
        string current = root;
        foreach (string part in logical.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) throw new IOException("不接受链接路径: " + logical);
        }
        if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("不接受链接根目录");
        return path;
    }
    public string Source(string logical) => Resolve(SourceRoot, logical);
    public string Data(string logical) => Resolve(DataRoot, logical);
    public string Database(string logical, bool writable)
    {
        string overlay = Data(logical);
        if (File.Exists(overlay)) return overlay;
        string source = Source(logical);
        if (!writable) return source;
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
        if (File.Exists(source)) File.Copy(source, overlay, false);
        return overlay;
    }
}

public sealed class DirectoryGameSource(string root, string encoding = "utf-8", bool scriptSnapshot = false, bool excludeRuntimeDirectories = false) : IGameSource
{
    private readonly Dictionary<SourceId, byte[]> snapshot = [];
    private readonly Dictionary<SourceId, (long Length, long Modified)> stamps = [];
    private long readBytes, readFiles;
    public long ReadBytes => Interlocked.Read(ref readBytes);
    public long ReadFiles => Interlocked.Read(ref readFiles);
    private readonly string root = Path.GetFullPath(root);
    private readonly Encoding decoder = CreateEncoding(encoding);
    private static Encoding CreateEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public async ValueTask<SourceManifest> EnumerateAsync(CancellationToken ct = default)
    {
        snapshot.Clear(); stamps.Clear(); long snapshotBytes = 0;
        var files = ImmutableArray.CreateBuilder<SourceFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 链接单独拒绝，避免枚举时进入根目录外。
        var directories = new Queue<string>();
        var directoryOrder = ImmutableArray.CreateBuilder<string>();
        directories.Enqueue(root);
        var names = new List<string>();
        while (directories.TryDequeue(out string? directory))
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                // 同目录存档时不把运行中写入的数据纳入游戏索引。
                if (excludeRuntimeDirectories && directory == this.root && entry is DirectoryInfo
                    && (entry.Name.Equals("saves", StringComparison.OrdinalIgnoreCase) || entry.Name.Equals("diagnostics", StringComparison.OrdinalIgnoreCase)
                        || entry.Name.Equals("cache", StringComparison.OrdinalIgnoreCase))) continue;
                if (entry.LinkTarget is not null) throw new IOException("源目录包含链接: " + Path.GetRelativePath(root, entry.FullName));
                if (entry is DirectoryInfo) { directories.Enqueue(entry.FullName); directoryOrder.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/')); }
                else names.Add(entry.FullName);
            }
        }
        var discovery = names.Select((path, order) => (path, order)).ToDictionary(p => p.path, p => p.order, StringComparer.OrdinalIgnoreCase);
        foreach (string path in names.OrderBy(p => Path.GetRelativePath(root, p), StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!paths.Add(relative)) throw new IOException("路径大小写冲突: " + relative);
            string category = Path.GetExtension(relative).TrimStart('.').ToUpperInvariant();
            var info = new FileInfo(path);
            // 资源只登记路径和元数据，内容由图片、音频等服务按需读取。
            if (category is not ("ERB" or "ERH" or "CSV" or "ERD" or "ALS" or "CONFIG"))
            {
                files.Add(new(new(relative, $"metadata:{info.Length}:{info.LastWriteTimeUtc.Ticks}"), info.Length, category) { DiscoveryOrder = discovery[path] });
                continue;
            }
            await using var stream = File.OpenRead(GamePathResolver.Resolve(root, relative));
            byte[]? frozen = null; string hash;
            // 声明与配置频繁参与依赖验证；函数脚本不保留整包快照。
            if (scriptSnapshot && category is "ERH" or "CSV" or "ERD" or "ALS" or "CONFIG")
            {
                snapshotBytes = checked(snapshotBytes + stream.Length);
                if (snapshotBytes > 512L * 1024 * 1024) throw new ScriptException("SOURCE_LIMIT", "脚本文本快照超过 512 MiB");
                frozen = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(frozen, ct);
                hash = Convert.ToHexString(SHA256.HashData(frozen));
            }
            else hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            Interlocked.Add(ref readBytes, stream.Length); Interlocked.Increment(ref readFiles);
            var id = new SourceId(relative, hash);
            stamps[id] = (info.Length, info.LastWriteTimeUtc.Ticks);
            if (frozen is not null) snapshot.Add(id, frozen);
            files.Add(new(id, stream.Length, category) { DiscoveryOrder = discovery[path] });
        }
        return new(files.ToImmutable()) { DirectoryOrder = directoryOrder.ToImmutable() };
    }
    public async ValueTask<SourceText> ReadAsync(SourceId source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (source.Hash.StartsWith("metadata:", StringComparison.Ordinal)) await ValidateAsync(source, ct);
        byte[] bytes;
        if (!snapshot.TryGetValue(source, out bytes!))
        {
            bytes = await File.ReadAllBytesAsync(GamePathResolver.Resolve(root, source.Path), ct);
            Interlocked.Add(ref readBytes, bytes.Length); Interlocked.Increment(ref readFiles);
        }
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!source.Hash.StartsWith("metadata:", StringComparison.Ordinal) && hash != source.Hash) throw new ScriptException("SOURCE_CHANGED", "源文件已改变，需要重建索引", new(source, 0, 0, 1));
        int offset = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        return new(source, (offset == 3 ? new UTF8Encoding(false, true) : decoder).GetString(bytes, offset, bytes.Length - offset));
    }
    public async ValueTask ValidateAsync(SourceId source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.ContainsKey(source)) return;
        if (source.Hash.StartsWith("metadata:", StringComparison.Ordinal))
        {
            var info = new FileInfo(GamePathResolver.Resolve(root, source.Path));
            if (info.Exists && source.Hash == $"metadata:{info.Length}:{info.LastWriteTimeUtc.Ticks}") return;
            throw new ScriptException("SOURCE_CHANGED", "源文件已改变，需要重建索引", new(source, 0, 0, 1));
        }
        if (stamps.TryGetValue(source, out var stamp))
        {
            var info = new FileInfo(GamePathResolver.Resolve(root, source.Path));
            if (info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc.Ticks == stamp.Modified) return;
        }
        await using var stream = File.OpenRead(GamePathResolver.Resolve(root, source.Path));
        Interlocked.Add(ref readBytes, stream.Length); Interlocked.Increment(ref readFiles);
        if (Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != source.Hash)
            throw new ScriptException("SOURCE_CHANGED", "源文件已改变，需要重建索引", new(source, 0, 0, 1));
    }
    public async ValueTask ValidateAsync(IReadOnlyList<SourceId> sources, CancellationToken ct = default)
    {
        await Parallel.ForEachAsync(sources, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 8 },
            async (source, token) => await ValidateAsync(source, token));
    }
}
