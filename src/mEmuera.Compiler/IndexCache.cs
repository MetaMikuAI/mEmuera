using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using mEmuera.Compiler.Indexing;
using mEmuera.Contracts;

namespace mEmuera.Compiler;

public sealed partial class CompilationWorkspace
{
    private sealed record CacheHeader(IndexResult Result, int Functions);
    private async Task<IndexResult> BuildIndexAsync(CompileOptions options, Action<int, int>? progress, CancellationToken ct)
    {
        IndexCacheHit = false;
        if (cacheDirectory is null || requirementSink is not null)
            return await ProgramIndexer.BuildAsync(source, options, ct, requirementSink, builtins, progress);

        var manifest = options.PreparedManifest ?? await source.EnumerateAsync(ct);
        options = options with { PreparedManifest = manifest };
        using var hash = SHA256.Create();
        await using (var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        {
            await JsonSerializer.SerializeAsync(output, new
            {
                Format = 3, Options = options, Manifest = manifest, Host = cacheVersion,
                Compiler = typeof(CompilationWorkspace).Module.ModuleVersionId,
                Contracts = typeof(ProgramIndex).Module.ModuleVersionId,
                Builtins = builtins.GetType().Module.ModuleVersionId,
                builtins.Signatures
            }, IdentityJson.Options, ct);
            await output.FlushFinalBlockAsync(ct);
        }
        string key = Convert.ToHexString(hash.Hash!);
        string path = Path.Combine(cacheDirectory, "index.json.gz");
        try
        {
            await using var file = File.OpenRead(path);
            byte[] header = new byte[64];
            await file.ReadExactlyAsync(header, ct);
            if (System.Text.Encoding.ASCII.GetString(header) == key)
            {
                await using var zip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new StreamReader(zip);
                var saved = JsonSerializer.Deserialize<CacheHeader>(await reader.ReadLineAsync(ct) ?? throw new InvalidDataException());
                if (saved?.Result?.Index is not null && saved.Result.Success && saved.Functions is >= 0 and <= 1000000)
                {
                    var functions = System.Collections.Immutable.ImmutableArray.CreateBuilder<FunctionDescriptor>(saved.Functions);
                    for (int i = 0; i < saved.Functions; i++)
                        functions.Add(JsonSerializer.Deserialize<FunctionDescriptor>(await reader.ReadLineAsync(ct) ?? throw new InvalidDataException()) ?? throw new InvalidDataException());
                    if (await reader.ReadLineAsync(ct) is not null) throw new InvalidDataException();
                    IndexCacheHit = true;
                    var index = saved.Result.Index;
                    return saved.Result with { Index = index with
                    {
                        Options = options, Manifest = manifest, Functions = functions.MoveToImmutable(),
                        Macros = index.Macros.WithComparers(StringComparer.OrdinalIgnoreCase)
                    } };
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }

        var result = await ProgramIndexer.BuildAsync(source, options, ct, requirementSink, builtins, progress);
        if (!result.Success) return result;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            await using (var file = File.Create(temporary))
            {
                await file.WriteAsync(System.Text.Encoding.ASCII.GetBytes(key), ct);
                await using var zip = new GZipStream(file, CompressionLevel.Fastest);
                await using var writer = new StreamWriter(zip);
                // 函数逐条读写，避免反序列化时为整份索引保留巨大的 JSON 缓冲区。
                var metadata = result with { Index = result.Index with { Functions = [], Options = new(), Manifest = new([]) } };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new CacheHeader(metadata, result.Index.Functions.Length)).AsMemory(), ct);
                foreach (var function in result.Index.Functions)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(function).AsMemory(), ct);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
