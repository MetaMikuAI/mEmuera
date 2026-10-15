using mEmuera.Contracts;
using mEmuera.Infrastructure.Saves;

internal static class SaveProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3) return 2;
        string mode = args[0], root = Path.GetFullPath(args[1]); long value = long.Parse(args[2]);
        Directory.CreateDirectory(root);
        var snapshot = new SaveSnapshot(1, "process-probe", "save-probe", DateTimeOffset.UnixEpoch,
            [new("ARRAY", ValueKind.Integer, 3, [new(value), new(long.MinValue), new(long.MaxValue)]),
             new("TEXT", ValueKind.String, 2, [new("中文\n"), new("")])], 123);
        if (mode == "read")
        {
            var read = await new JsonSaveStore(root).ReadAsync(new("process-probe", 0));
            var actual = read.Snapshot;
            bool equal = actual is not null && actual.RandomState == 123 && actual.Variables.Length == snapshot.Variables.Length
                && actual.Variables.Zip(snapshot.Variables).All(p => p.First.Name == p.Second.Name && p.First.Kind == p.Second.Kind
                    && p.First.Length == p.Second.Length && p.First.Values.SequenceEqual(p.Second.Values));
            Console.WriteLine(equal ? "verified" : "mismatch"); return equal ? 0 : 4;
        }
        string? stop = args.Length > 3 ? args[3] : null;
        var store = new JsonSaveStore(root, stage =>
        {
            if (stage != stop) return;
            File.WriteAllText(Path.Combine(root, "checkpoint.ready"), stage);
            Thread.Sleep(Timeout.Infinite);
        });
        return (await store.WriteAsync(new("process-probe", 0), snapshot)).Committed ? 0 : 5;
    }
}
