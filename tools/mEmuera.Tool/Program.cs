using mEmuera.EraCompat.Builtins;
using System.Text.Json;
using mEmuera.Compiler;
using mEmuera.Contracts;
using mEmuera.EraCompat.Loading;
using mEmuera.Infrastructure;
using mEmuera.Infrastructure.FileSystem;
using mEmuera.Infrastructure.Saves;
using mEmuera.Host;
using mEmuera.Runtime.Execution;

return await MainAsync(args);

static async Task<int> MainAsync(string[] args)
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var ct = cancellation.Token;
    try
    {
        if (args.FirstOrDefault() == "save-probe") return await SaveProbe.RunAsync(args[1..]);
        if (args.Length == 0) { Console.Error.WriteLine("scan|check|replay|import-legacy --game PATH [--entry SYSTEM_TITLE] [--report PATH] [--trace PATH] [--game-id ID]\nimport-legacy 另需 --input OLD_SAVE --slot NEW_SLOT --data-dir PATH"); return 2; }
        string? Option(string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args[0] == "media-probe") return await MediaProbe.RunAsync(Option("--output") ?? "artifacts/media-probe", Option("--audio-player") ?? "ffplay");
        if (args[0] == "scale-probe") return await ScaleProbe.RunAsync(Option("--report") ?? "artifacts/scale.json");
        string gamePath = Option("--game") ?? throw new ArgumentException("需要 --game");
        if (args[0] == "load")
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var loaded = await LoadedGame.LoadAsync(new(gamePath, Option("--data-dir") ?? "artifacts/load-data"),
                step => Console.WriteLine($"{step}: {timer.ElapsedMilliseconds} ms"), ct);
            Console.WriteLine($"loadedMs={timer.ElapsedMilliseconds} indexCacheHit={loaded.Workspace.IndexCacheHit} readBytes={loaded.Source.ReadBytes} functions={loaded.Index.Functions.Length} managedBytes={GC.GetTotalMemory(false)}");
            if (Option("--entry") is { } requestedEntry)
            {
                var function = loaded.Index.Find(requestedEntry) ?? throw new ArgumentException("入口不存在");
                var compiled = await loaded.Workspace.CompileAsync(loaded.Index, function.Id, ct);
                foreach (var diagnostic in compiled.Diagnostics) Print(diagnostic);
                Console.WriteLine($"entryCompiled={compiled.Success}");
                return compiled.Success ? 0 : 3;
            }
            return 0;
        }
        if (args[0] == "render") return await RenderCommand.RunAsync(gamePath, Option("--data-dir") ?? "artifacts/render-data", Option("--out") ?? "artifacts/render.png", (Option("--script") ?? "").Length == 0 ? [] : Option("--script")!.Split(','), args.Contains("--dump"), int.Parse(Option("--menu-height") ?? "0"));
        if (args[0] == "cells") return await CellsCommand.RunAsync(gamePath, Option("--data-dir") ?? "artifacts/render-data", Option("--out") ?? "artifacts/cells.png", (Option("--script") ?? "").Length == 0 ? [] : Option("--script")!.Split(','),
            int.Parse(Option("--columns") ?? "120"), int.Parse(Option("--rows") ?? "40"), float.Parse(Option("--font-size") ?? "16", System.Globalization.CultureInfo.InvariantCulture));
        if (args[0] == "inspect-plugins") return await PluginInventory.RunAsync(gamePath, Option("--report") ?? "artifacts/plugins.json");
        if (args[0] == "lifecycle-probe") return await LifecycleProbe.RunAsync(gamePath, Option("--data-dir") ?? "artifacts/lifecycle-data", Option("--report") ?? "artifacts/lifecycle.json");
        var source = new DirectoryGameSource(gamePath, Option("--encoding") ?? "utf-8", scriptSnapshot: (Option("--profile") ?? "era-v1") != "minimal-v1");
        string reportPath = Option("--report") ?? "artifacts/scan.json";
        if (args[0] == "scan") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        using var occurrences = args[0] == "scan" ? new StreamWriter(new System.IO.Compression.GZipStream(
            File.Create(Path.ChangeExtension(reportPath, ".requirements.jsonl.gz")), System.IO.Compression.CompressionLevel.Fastest)) : null;
        using var workspace = new CompilationWorkspace(source, EraBuiltins.Library.Catalog,
            occurrences is null ? null : requirement => occurrences.WriteLine(JsonSerializer.Serialize(requirement)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var options = await SystemVariableLoader.PrepareAsync(source, new(Profile: Option("--profile") ?? "era-v1", Encoding: Option("--encoding") ?? "utf-8",
            CacheCapacity: Option("--function-cache") is { } capacity ? int.Parse(capacity, System.Globalization.CultureInfo.InvariantCulture) : new CompileOptions().CacheCapacity), ct);
        var indexed = await workspace.IndexAsync(source, options, ct);
        if (args[0] == "scan")
        {
            var report = new { schema = 2, version = options.Profile, manifest = indexed.Index.Manifest, loadedSources = indexed.Index.LoadedSources, coverage = indexed.Index.Coverage,
                identity = indexed.Index.Identity, options = new { options.Profile, options.Encoding, options.Debug, options.SearchSubdirectories }, functions = indexed.Index.Functions.Length,
                requirements = indexed.Index.Requirements, scriptFunctions = indexed.Index.Functions.Select(f => f.Name).Distinct().Order(), diagnostics = indexed.Diagnostics.Select(d => new { d.Code, d.Span, d.Severity }), elapsedMs = watch.ElapsedMilliseconds, source.ReadBytes, source.ReadFiles };
            string path = Option("--report") ?? "artifacts/scan.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var stream = File.Create(path))
                await JsonSerializer.SerializeAsync(stream, report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"files={indexed.Index.Manifest.Files.Length} functions={indexed.Index.Functions.Length} structured={indexed.Index.Coverage.Structured}/{indexed.Index.Coverage.Files} unknown={indexed.Index.Coverage.Unknown} elapsedMs={watch.ElapsedMilliseconds}");
            return indexed.Success ? 0 : 3;
        }
        string entry = Option("--entry") ?? "SYSTEM_TITLE";
        if (args[0] == "check")
        {
            bool all = args.Contains("--all");
            var first = indexed.Index.Find(entry); if (!all && first is null) { Console.Error.WriteLine("ENTRY: 入口不存在"); return 3; }
            var queue = new Queue<string>(all ? indexed.Index.Functions.Select(f => f.Id) : [first!.Id]);
            var seen = new HashSet<string>(); bool success = indexed.Success;
            var diagnostics = indexed.Diagnostics.ToList();
            foreach (var diagnostic in indexed.Diagnostics.Take(20)) Print(diagnostic);
            while (queue.TryDequeue(out string? id))
            {
                if (!seen.Add(id)) continue;
                CompileResult compiled;
                try { compiled = await workspace.CompileAsync(indexed.Index, id, ct); }
                catch (Exception error) when (all && error is not (OperationCanceledException or OutOfMemoryException))
                {
                    var descriptor = indexed.Index.Functions.First(f => f.Id == id);
                    diagnostics.Add(new("COMPILER_EXCEPTION", error.GetType().Name + ": " + error.Message, descriptor.Span));
                    success = false;
                    continue;
                }
                diagnostics.AddRange(compiled.Diagnostics);
                foreach (var diagnostic in compiled.Diagnostics) Print(diagnostic);
                success &= compiled.Success;
                if (seen.Count % 5000 == 0) Console.Error.WriteLine($"checked={seen.Count}/{indexed.Index.Functions.Length} diagnostics={diagnostics.Count}");
                if (!all && compiled.Function is { } function)
                    foreach (var instruction in function.Code)
                        if (instruction.Op == Op.Call) queue.Enqueue(instruction.Text);
                        else if (instruction.Op == Op.DynamicCall) Console.WriteLine("DYNAMIC_UNKNOWN: " + instruction.Span.Source.Path + ":" + instruction.Span.Line);
            }
            if (Option("--report") is { } checkOutput)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(checkOutput))!);
                await using var stream = File.Create(checkOutput);
                await JsonSerializer.SerializeAsync(stream, new { entry = all ? "*" : entry, success, checkedFunctions = seen.Count,
                    diagnostics = diagnostics.Select(d => new { d.Code, d.Message, d.Span, d.Severity }) },
                    new JsonSerializerOptions { WriteIndented = true });
            }
            return success ? 0 : 3;
        }
        if (args[0] is not ("replay" or "import-legacy")) return 2;
        if (!indexed.Success) { foreach (var diagnostic in indexed.Diagnostics) Print(diagnostic); return 3; }
        var game = await GameBootstrapLoader.LoadAsync(source, indexed.Index, Option("--game-id"), ct);
        var paths = new GamePathResolver(gamePath, Option("--data-dir") ?? "artifacts/replay-data");
        if (args[0] == "import-legacy")
        {
            string file = Option("--input") ?? throw new ArgumentException("需要 --input 旧档路径");
            int slot = int.Parse(Option("--slot") ?? throw new ArgumentException("需要 --slot 新槽位"));
            var imported = await mEmuera.Infrastructure.Saves.Legacy.LegacySaveImporter.ImportAsync(new(slot, file), game,
                new JsonSaveStore(paths.DataRoot), new GameSession(game, EraBuiltins.Library).ValidateSave, ct);
            if (!imported.Imported) throw new ScriptException("LEGACY_SLOT", "目标槽位必须不存在");
            var legacy = imported.Save!; var converted = imported.Conversion!;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { Format = 1808, legacy.GameCode, legacy.GameVersion,
                Slot = slot, Characters = legacy.Characters.Length, converted.DefaultedFields, converted.UnmappedZeroFields, RandomState = "按游戏代码和版本重新设定，不保留旧随机序列" }, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.WriteLine($"imported slot={slot} characters={legacy.Characters.Length} defaulted={converted.DefaultedFields.Length}");
            return 0;
        }
        await using var services = new GameServices(game, workspace, source, indexed.Index.Manifest, indexed.Index.Options, paths,
            new mEmuera.Infrastructure.Media.AudioProcessService(paths, Option("--audio-player") ?? "ffplay"));
        var dispatcher = services.Dispatcher;
        int next = 0;
        var functionCalls = args.Contains("--profile-calls") ? new Dictionary<string, long>() : null;
        int profileCallsFromAction = int.Parse(Option("--profile-calls-from") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        long now = 0; var session = new GameSession(game with { Entry = entry }, EraBuiltins.Library, () => now, new TerminalTextMetrics(),
            functionEntered: functionCalls is null ? null : function =>
            {
                if (next > profileCallsFromAction) functionCalls[function.Id] = functionCalls.GetValueOrDefault(function.Id) + 1;
            });
        ReplayAction[] actions = Option("--trace") is { } trace ? JsonSerializer.Deserialize<ReplayAction[]>(await File.ReadAllTextAsync(trace))! : [];
        var reportSelection = (Option("--report-variables") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => new StateSelection(name)).ToList();
        if (Option("--report-selection") is { } selectionPath)
            reportSelection.AddRange(JsonSerializer.Deserialize<StateSelection[]>(await File.ReadAllTextAsync(selectionPath)) ?? []);
        SessionSnapshot SelectedState()
        {
            int count = session.InspectSelected([]).Characters.Length;
            return session.InspectSelected(reportSelection.Where(s => s.Character is null || s.Character >= 0 && s.Character < count));
        }
        using var checkpoints = Option("--checkpoints") is { } checkpointPath ? new StreamWriter(checkpointPath, false) { AutoFlush = true } : null;
        int steps = 0; long? interactionStart = null; var latencies = new List<double>();
        long allocationStart = 0;
        var interactionAllocatedBytes = new List<long>();
        var interactionCompilations = new List<long>();
        var interactionCompileMilliseconds = new List<double>();
        long compilationStart = 0;
        double compileTimeStart = 0;
        long progressAt = 0;
        var compilationRequests = args.Contains("--profile-compilation") ? new Dictionary<string, int>() : null;
        int maxSteps = int.Parse(Option("--max-steps") ?? "1000000", System.Globalization.CultureInfo.InvariantCulture);
        while (++steps < maxSteps)
        {
            ct.ThrowIfCancellationRequested();
            if (args.Contains("--progress") && watch.ElapsedMilliseconds - progressAt >= 10000)
            {
                progressAt = watch.ElapsedMilliseconds;
                Console.Error.WriteLine($"progress elapsedMs={progressAt} steps={steps} mode={session.Mode} compiled={workspace.CacheMisses} readBytes={source.ReadBytes}");
            }
            var result = session.Step(new(1000));
            if (result.Service is { } service)
            {
                if (compilationRequests is not null && service.Payload is CompilePayload compile)
                    compilationRequests[compile.FunctionId] = compilationRequests.GetValueOrDefault(compile.FunctionId) + 1;
                session.Apply(await dispatcher.ExecuteAsync(service, ct)); continue;
            }
            if (result.Reason == StopReason.Yielded) continue;
            if (interactionStart is { } started)
            {
                latencies.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                interactionAllocatedBytes.Add(GC.GetTotalAllocatedBytes(false) - allocationStart);
                interactionCompilations.Add(workspace.CacheMisses - compilationStart);
                interactionCompileMilliseconds.Add(workspace.CompileMilliseconds - compileTimeStart);
                interactionStart = null;
            }
            if (result.Reason != StopReason.WaitingInput) break;
            if (checkpoints is not null)
            {
                var checkpoint = SelectedState();
                await checkpoints.WriteLineAsync(JsonSerializer.Serialize(new { action = next, checkpoint.Instructions, checkpoint.Mode, checkpoint.Input,
                    checkpoint.Variables, checkpoint.Characters, checkpoint.Shapes,
                    source = checkpoint.Events.LastOrDefault(e => e.Kind == TraceKind.InputRequested)?.Span,
                    lines = checkpoint.Document.Lines.TakeLast(5).Select(l => string.Concat(l.Runs.Select(r => r.Text))) }));
            }
            if (next == actions.Length)
            {
                if (!args.Contains("--follow-trace")) break;
                string tracePath = Option("--trace") ?? throw new ArgumentException("--follow-trace 需要 --trace");
                string livePath = Option("--report") ?? throw new ArgumentException("--follow-trace 需要 --report");
                await File.WriteAllTextAsync(Path.ChangeExtension(livePath, ".live.json"), JsonSerializer.Serialize(SelectedState()), ct);
                Console.Error.WriteLine($"waiting input actions={next} instructions={session.InspectPresentation().Instructions}");
                while (next == actions.Length)
                {
                    await Task.Delay(250, ct);
                    ReplayAction[] updated;
                    try { updated = JsonSerializer.Deserialize<ReplayAction[]>(await File.ReadAllTextAsync(tracePath, ct)) ?? []; }
                    catch (JsonException) { continue; }
                    if (updated.Length < actions.Length || JsonSerializer.Serialize(updated.Take(actions.Length)) != JsonSerializer.Serialize(actions)) throw new ScriptException("REPLAY_TRACE", "跟随轨迹只能追加动作");
                    actions = updated;
                }
            }
            var action = actions[next++];
            var snapshot = session.InspectSelected((action.Expect?.Selection ?? []).Concat(action.ExpectVariable is { } name ? [new StateSelection(name)] : []));
            action.Expect?.Check(snapshot);
            interactionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            allocationStart = GC.GetTotalAllocatedBytes(false);
            compilationStart = workspace.CacheMisses;
            compileTimeStart = workspace.CompileMilliseconds;
            if (action.ExpectVariable is { } variable && snapshot.Variables[variable][0].ToString() != action.ExpectValue) throw new ScriptException("REPLAY_ASSERT", "变量断言失败: " + variable);
            if (action.Stop) break;
            if (action.Advance is { } delta) { now += delta; session.Apply(new TimeoutMessage(now)); continue; }
            if (action.Save is { } slot)
            {
                var saved = session.Apply(new SaveCommand(slot, false));
                if (!saved.Accepted) throw new ScriptException("REPLAY_SAVE", saved.Reason);
                continue;
            }
            if (action.Load is { } load)
            {
                var loaded = session.Apply(new SaveCommand(load, true));
                if (!loaded.Accepted) throw new ScriptException("REPLAY_LOAD", loaded.Reason);
                continue;
            }
            var input = snapshot.Input!;
            long? button = action.ButtonValue is { } buttonValue ? input.Buttons.First(p => p.Value.ToString() == buttonValue).Key : null;
            var applied = session.Apply(new InputReply(input.Epoch, input.Id, action.Text ?? "", button, now));
            if (!applied.Accepted) throw new ScriptException("REPLAY_INPUT", applied.Reason);
        }
        ReplayExpectation? expected = Option("--expect") is { } expectationPath
            ? JsonSerializer.Deserialize<ReplayExpectation>(await File.ReadAllTextAsync(expectationPath)) : null;
        reportSelection.AddRange(expected?.Selection ?? []);
        var final = args.Contains("--full-state") ? session.Inspect() : SelectedState();
        if (Option("--report") is { } output)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(final, new JsonSerializerOptions { WriteIndented = true }));
            var ordered = latencies.Order().ToArray();
            double? Percentile(double fraction) => ordered.Length == 0 ? null : ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * fraction) - 1)];
            await File.WriteAllTextAsync(Path.ChangeExtension(output, ".metrics.json"), JsonSerializer.Serialize(new
            {
                measurement = "headless reply to next pause; excludes terminal drawing", samples = ordered.Length,
                p50Ms = Percentile(.5), p95Ms = Percentile(.95), totalMs = watch.Elapsed.TotalMilliseconds,
                interactionAllocatedBytes, interactionMilliseconds = latencies,
                interactionCompilations, interactionCompileMilliseconds,
                peakRssBytes = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64, workspace.CacheHits, workspace.CacheMisses,
                workspace.SourceCacheHits, workspace.CachedSources, workspace.CompileMilliseconds, workspace.EstimatedCacheBytes,
                source.ReadBytes, source.ReadFiles, indexed.Index.Identity, final.Mode, characterCount = final.Characters.Length,
                compilationRequests = compilationRequests?.OrderByDescending(p => p.Value).Take(40)
                    .Select(p => new { function = indexed.Index.ByFunctionId[p.Key].Name, source = indexed.Index.ByFunctionId[p.Key].Span, requests = p.Value }),
                distinctCompiledFunctions = compilationRequests?.Count,
                profileCallsFromAction,
                totalFunctionCalls = functionCalls?.Values.Sum(),
                distinctCalledFunctions = functionCalls?.Count,
                functionCalls = functionCalls?.OrderByDescending(p => p.Value).Take(50)
                    .Select(p => new { function = indexed.Index.ByFunctionId[p.Key].Name, source = indexed.Index.ByFunctionId[p.Key].Span, calls = p.Value }),
                expected, consumedActions = next, totalActions = actions.Length, acceptanceSpecified = expected is not null,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                command = args, assemblies = Directory.EnumerateFiles(AppContext.BaseDirectory, "mEmuera*.dll").ToDictionary(path => Path.GetFileName(path)!,
                    path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var diagnostic in final.Diagnostics) Print(diagnostic);
        expected?.Check(final);
        Console.WriteLine($"state={final.State} instructions={final.Instructions} actions={next}/{actions.Length} cacheHits={workspace.CacheHits}");
        if (steps >= maxSteps) throw new ScriptException("REPLAY_BUDGET", "回放步数达到 --max-steps 上限");
        return final.State == StopReason.Faulted || next != actions.Length ? 4 : 0;
    }
    catch (ArgumentException e) { Console.Error.WriteLine(args.Contains("--stack") ? e.ToString() : e.Message); return 2; }
    catch (ScriptException e) { Print(e.Diagnostic); return 4; }
    catch (OperationCanceledException) { return 130; }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(e.Message); return 5; }
}
static void Print(Diagnostic diagnostic) => Console.Error.WriteLine($"{diagnostic.Code} {diagnostic.Span?.Source.Path}:{diagnostic.Span?.Line}: {diagnostic.Message}");
internal sealed record ReplayAction(string? Text = null, string? ButtonValue = null, long? Advance = null, int? Save = null, int? Load = null, string? ExpectVariable = null, string? ExpectValue = null, bool Stop = false, ReplayExpectation? Expect = null);
