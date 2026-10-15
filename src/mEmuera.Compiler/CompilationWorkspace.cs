using System.Collections.Immutable;
using mEmuera.Compiler.Indexing;
using mEmuera.Compiler.Lexing;
using mEmuera.Compiler.Syntax;
using mEmuera.Compiler.Preprocessing;
using mEmuera.Contracts;

namespace mEmuera.Compiler;

public sealed partial class CompilationWorkspace(IGameSource source, IBuiltinRegistry builtins, Action<Requirement>? requirementSink = null, string? cacheDirectory = null, string? cacheVersion = null) : IGameCompiler, ICompilationService, IDisposable
{
    private ProgramIndex? index;
    private SourceId[] dependencies = [];
    private readonly Dictionary<string, CompiledFunction> cache = [];
    private readonly Queue<string> order = [];
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<SourceId, SourceText> texts = [];
    private readonly Dictionary<SourceId, ImmutableArray<ExpandedLine>> expandedTexts = [];
    private readonly Queue<SourceId> textOrder = [];
    private bool disposed;
    private readonly CancellationTokenSource lifetime = new();
    public long SourceCacheHits { get; private set; }
    public int CachedSources => texts.Count;
    public void Dispose()
    {
        if (disposed) return;
        lifetime.Cancel(); gate.Wait();
        try { disposed = true; cache.Clear(); order.Clear(); texts.Clear(); expandedTexts.Clear(); textOrder.Clear(); index = null; }
        finally { gate.Release(); }
    }
    public long CacheHits { get; private set; }
    public long CacheMisses { get; private set; }
    public bool IndexCacheHit { get; private set; }
    public double CompileMilliseconds { get; private set; }
    public long EstimatedCacheBytes => cache.Values.Sum(f => f.Code.Sum(i => 96L + i.Text.Length * 2L + (i.Value.Text?.Length ?? 0) * 2L))
        + texts.Values.Sum(t => t.Text.Length * 2L) + expandedTexts.Values.Sum(lines => lines.Sum(l => 96L + l.Line.Text.Length * 2L));
    public ValueTask<IndexResult> IndexAsync(IGameSource gameSource, CompileOptions options, CancellationToken ct = default) => IndexAsync(gameSource, options, null, ct);
    /// <param name="progress">索引脚本的进度：已完成数、总数。</param>
    public async ValueTask<IndexResult> IndexAsync(IGameSource gameSource, CompileOptions options, Action<int, int>? progress, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        if (!ReferenceEquals(source, gameSource)) throw new ArgumentException("文件源不属于此工作区");
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = await BuildIndexAsync(options, progress, ct);
            string signatureFingerprint = System.Text.Json.JsonSerializer.Serialize(builtins.Signatures);
            result = result with { Index = result.Index with { Identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(result.Index.Identity + "bytecode-v2" + signatureFingerprint))) } };
            index = result.Index;
            dependencies = index.LoadedSources.Where(f => f.Path.EndsWith(".erh", StringComparison.OrdinalIgnoreCase))
                .Concat(index.Options.PreparationSources).Concat(index.DeclarationSources.IsDefault ? [] : index.DeclarationSources).Distinct().ToArray();
            cache.Clear(); order.Clear(); texts.Clear(); expandedTexts.Clear(); textOrder.Clear(); return result;
        }
        finally { gate.Release(); }
    }
    public ValueTask<CompileResult> CompileAsync(string functionId, CancellationToken ct) => CompileAsync(index ?? throw new InvalidOperationException("尚未索引"), functionId, ct);
    public async ValueTask<CompileResult> CompileVariableAsync(DynamicVariablePayload payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await source.ValidateAsync(dependencies, ct);
            await source.ValidateAsync(payload.Caller.Span.Source, ct);
            return new FunctionCompiler(index ?? throw new InvalidOperationException("尚未索引"), payload.Caller, builtins).CompileVariable(payload);
        }
        catch (ScriptException e) { return new(null, [e.Diagnostic]); }
        finally { gate.Release(); }
    }
    public async ValueTask<CompileResult> CompileAsync(ProgramIndex program, string functionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = linked.Token;
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (index?.Identity != program.Identity) return new(null, [new("WORKSPACE", "索引不属于此工作区")]);
            var descriptor = program.ByFunctionId.GetValueOrDefault(functionId);
            if (descriptor is null) return new(null, [new("FUNCTION", "函数不存在")]);
            // 已编译函数在本次会话内复用；脚本修改在重新进入游戏后生效。
            if (cache.TryGetValue(functionId, out var hit)) { CacheHits++; return new(hit, []); }
            await source.ValidateAsync(dependencies, ct);
            await source.ValidateAsync(descriptor.Span.Source, ct);
            if (!texts.TryGetValue(descriptor.Span.Source, out var text))
            {
                text = await source.ReadAsync(descriptor.Span.Source, ct);
                while (texts.Count >= Math.Max(1, program.Options.SourceCacheCapacity))
                { var removed = textOrder.Dequeue(); texts.Remove(removed); expandedTexts.Remove(removed); }
                texts.Add(text.Id, text); textOrder.Enqueue(text.Id);
            }
            else SourceCacheHits++;
            CacheMisses++;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!expandedTexts.TryGetValue(text.Id, out var expanded))
                expandedTexts[text.Id] = expanded = Preprocessor.Read(text, new MacroEnvironment(program.Macros.Values), program.Options, ct: ct).ToImmutableArray();
            var result = new FunctionCompiler(program, descriptor, builtins).Compile(expanded, ct);
            CompileMilliseconds += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (result.Function is not null)
            {
                while (cache.Count >= Math.Max(1, program.Options.CacheCapacity)) cache.Remove(order.Dequeue());
                cache.Add(functionId, result.Function); order.Enqueue(functionId);
            }
            return result;
        }
        catch (ScriptException e) { return new(null, [e.Diagnostic]); }
        finally { gate.Release(); }
    }
}

