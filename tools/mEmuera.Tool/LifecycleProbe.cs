using System.Diagnostics;
using System.Text.Json;
using mEmuera.Compiler;
using mEmuera.Contracts;
using mEmuera.EraCompat.Builtins;
using mEmuera.EraCompat.Loading;
using mEmuera.Infrastructure;
using mEmuera.Infrastructure.FileSystem;
using mEmuera.Infrastructure.Saves;
using mEmuera.Infrastructure.Sqlite;
using mEmuera.Runtime.Execution;
using mEmuera.Runtime.Scheduling;

internal static class LifecycleProbe
{
    public static async Task<int> RunAsync(string gamePath, string root, string report)
    {
        var process = Process.GetCurrentProcess(); var samples = new List<object>(); var handles = new List<int>();
        double idleCpuMs = 0;
        for (int iteration = 0; iteration < 60; iteration++)
        {
            var source = new DirectoryGameSource(gamePath);
            using var workspace = new CompilationWorkspace(source, EraBuiltins.Library.Catalog);
            var index = await workspace.IndexAsync(source, new());
            if (!index.Success) return 3;
            var game = await GameBootstrapLoader.LoadAsync(source, index.Index);
            var paths = new GamePathResolver(gamePath, root);
            await using (var db = new SqliteDatabaseService(paths))
            {
                Guid owner = Guid.NewGuid();
                var connection = (await db.ExecuteAsync(new(owner, DbOperation.Open, Spec: new("lifecycle.db", true)))).Handle!.Value;
                await db.ExecuteAsync(new(owner, DbOperation.Query, connection, Sql: "SELECT 1"));
                var session = new GameSession(game, EraBuiltins.Library, () => SessionHost.Now(TimeProvider.System));
                await using var host = new SessionHost(session, new ServiceDispatcher(game.GameId, workspace, new JsonSaveStore(paths.DataRoot), db));
                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.Changed += state => { if (state.State == StopReason.WaitingInput) waiting.TrySetResult(); };
                host.Start(); await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
                if (iteration == 40)
                {
                    await Task.Delay(1000);
                    process.Refresh(); var before = process.TotalProcessorTime;
                    await Task.Delay(2000);
                    process.Refresh(); idleCpuMs = (process.TotalProcessorTime - before).TotalMilliseconds;
                }
            }
            if (iteration % 10 == 9)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
                handles.Add(process.HandleCount);
                samples.Add(new { iterations = iteration + 1, handles = process.HandleCount, rssBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false) });
            }
        }
        bool stable = handles[^1] <= handles[1] + 12;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { passed = stable, samples, idleCpuMs, idleWallMs = 2000,
            scope = "headless SessionHost plus real SQLite reader; no terminal", os = System.Runtime.InteropServices.RuntimeInformation.OSDescription }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"lifecycle passed={stable} handles={string.Join(',', handles)} idleCpuMs={idleCpuMs}"); return stable ? 0 : 4;
    }
}
