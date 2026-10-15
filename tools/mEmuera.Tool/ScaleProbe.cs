using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using mEmuera.Compiler;
using mEmuera.Contracts;
using mEmuera.EraCompat.Builtins;
using mEmuera.Runtime.Execution;

internal static class ScaleProbe
{
    private sealed class Source(string text) : IGameSource
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes(text);
        private SourceId Id => new("ERB/规模样例.ERB", Convert.ToHexString(SHA256.HashData(bytes)));
        public long ReadBytes, ReadFiles;
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PausedRead { get; set; }
        public ValueTask<SourceManifest> EnumerateAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); ReadBytes += bytes.Length; ReadFiles++; return ValueTask.FromResult(new SourceManifest([new(Id, bytes.Length, "ERB")])); }
        public ValueTask<SourceText> ReadAsync(SourceId id, CancellationToken ct = default)
        {
            if (PausedRead is { } paused) return PauseRead(paused, ct);
            ct.ThrowIfCancellationRequested(); FirstRead.TrySetResult(); ReadBytes += bytes.Length; ReadFiles++;
            if (Id != id) throw new ScriptException("SOURCE_CHANGED", "样例摘要改变");
            return ValueTask.FromResult(new SourceText(id, text));
        }
        private static async ValueTask<SourceText> PauseRead(TaskCompletionSource paused, CancellationToken ct)
        {
            paused.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("取消探针不应继续读取");
        }
    }
    public static async Task<int> RunAsync(string report)
    {
        var samples = new List<object>();
        foreach (int count in new[] { 10_000, 100_000 })
        {
            var text = new StringBuilder("#DIM SAVEDATA BIG,100000\n#DIM SAVEDATA GRID,100,100\n#DIM CHARADATA SAVEDATA V,1024\n@SYSTEM_TITLE\n");
            for (int i = 0; i < 8; i++) text.AppendLine("ADDCHARA 1");
            text.Append("CALL F").Append(count - 1).AppendLine("\nINPUT\nQUIT");
            for (int i = 0; i < count; i++) text.Append("@F").Append(i).AppendLine("\nRETURN 7");
            var source = new Source(text.ToString());
            using var workspace = new CompilationWorkspace(source, EraBuiltins.Library.Catalog);
            var watch = Stopwatch.StartNew();
            var options = new CompileOptions("era-v1");
            var indexed = await workspace.IndexAsync(source, options);
            if (!indexed.Success) throw new ScriptException("SCALE_INDEX", "规模样例索引失败");
            double indexMs = watch.Elapsed.TotalMilliseconds;
            var index = indexed.Index;
            index.Find("F0"); var lookupSamples = new List<double>();
            for (int round = 0; round < 20; round++)
            {
                watch.Restart();
                for (int i = 0; i < 10000; i++) if (index.Find("F" + (i * 97 % count)) is null) throw new ScriptException("SCALE_LOOKUP", "查找失败");
                lookupSamples.Add(watch.Elapsed.TotalMilliseconds / 10000);
            }
            var game = new GameDefinition("scale", "规模样例", "era-v1", index)
            { CharacterTemplates = ImmutableDictionary<long, CharacterTemplate>.Empty.Add(1, new(1, ImmutableDictionary<string, ImmutableArray<EraValue>>.Empty)) };
            var session = new GameSession(game, EraBuiltins.Library);
            watch.Restart();
            while (true)
            {
                var step = session.Step(new(1000));
                if (step.Service is { Payload: CompilePayload compile } request)
                {
                    var result = await workspace.CompileAsync(compile.FunctionId, CancellationToken.None);
                    session.Apply(result.Function is { } function ? new ServiceResult(request.Epoch, request.Id, new CompiledData(function))
                        : new ServiceResult(request.Epoch, request.Id, null, result.Diagnostics[0]));
                    continue;
                }
                if (step.Reason == StopReason.Yielded) continue;
                if (step.Reason != StopReason.WaitingInput) throw new ScriptException("SCALE_RUNTIME", "规模样例运行失败");
                break;
            }
            double runMs = watch.Elapsed.TotalMilliseconds;
            session.InspectPresentation(); long beforeUi = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
            for (int i = 0; i < 1000; i++)
            {
                var ui = session.InspectPresentation();
                if (ui.Variables.Count != 0 || ui.Characters.Length != 0) throw new ScriptException("SCALE_UI", "界面快照复制了变量或角色");
            }
            double uiMs = watch.Elapsed.TotalMilliseconds; long uiBytes = GC.GetAllocatedBytesForCurrentThread() - beforeUi;
            long beforeFull = GC.GetAllocatedBytesForCurrentThread(); var full = session.Inspect(); long fullBytes = GC.GetAllocatedBytesForCurrentThread() - beforeFull;
            if (full.Characters.Length != 8 || full.Variables["RESULT"][0].AsInt() != 7) throw new ScriptException("SCALE_STATE", "规模样例语义错误");
            var cancelSource = new Source(text.ToString());
            using var cancelWorkspace = new CompilationWorkspace(cancelSource, EraBuiltins.Library.Catalog);
            using var cancellation = new CancellationTokenSource();
            var pending = Task.Run(async () => await cancelWorkspace.IndexAsync(cancelSource, options, cancellation.Token));
            await cancelSource.FirstRead.Task; watch.Restart(); cancellation.Cancel(); bool cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            double cancelMs = watch.Elapsed.TotalMilliseconds;
            if (!cancelled) throw new ScriptException("SCALE_CANCEL", "索引取消未生效");
            source.PausedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var compileCancellation = new CancellationTokenSource();
            var compilePending = Task.Run(async () => await workspace.CompileAsync(index.Find("F1")!.Id, compileCancellation.Token));
            await source.PausedRead.Task; watch.Restart(); compileCancellation.Cancel(); cancelled = false;
            try { await compilePending; } catch (OperationCanceledException) { cancelled = true; }
            double compileReadCancelMs = watch.Elapsed.TotalMilliseconds; source.PausedRead = null;
            if (!cancelled || !(await workspace.CompileAsync(index.Find("F1")!.Id, CancellationToken.None)).Success)
                throw new ScriptException("SCALE_CANCEL", "编译取消或取消后恢复失败");
            var ordered = lookupSamples.Order().ToArray();
            samples.Add(new { functions = count, indexMs, runMs, lookupMeanPerCallP95Ms = ordered[18], ui1000Ms = uiMs,
                ui1000Bytes = uiBytes, fullSnapshotBytes = fullBytes, source.ReadBytes, source.ReadFiles, indexCancelMs = cancelMs, compileReadCancelMs,
                peakRssBytes = Process.GetCurrentProcess().PeakWorkingSet64, workspace.EstimatedCacheBytes });
            Console.WriteLine($"scale functions={count} indexMs={indexMs:F1} uiBytes={uiBytes} fullBytes={fullBytes} cancelMs={cancelMs:F1}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { scope = "synthetic in-memory source; no terminal drawing", samples,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
