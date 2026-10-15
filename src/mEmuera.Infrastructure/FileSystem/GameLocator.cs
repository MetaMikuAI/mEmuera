using System.Collections.Immutable;

namespace mEmuera.Infrastructure.FileSystem;

/// <summary>各平台的默认位置；前端只填写这些值，查找规则共用。</summary>
public sealed record GameLocations(string DataRoot, string? ExecutableDirectory = null, string? ConventionalRoot = null)
{
    /// <summary>桌面：程序放在游戏目录或其子目录中，数据放在系统本地应用数据目录。</summary>
    public static GameLocations Desktop() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "mEmuera"),
        AppContext.BaseDirectory);
}

public static class GameLocator
{
    public static bool IsGame(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var names = Directory.EnumerateDirectories(directory).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return names.Contains("ERB") && names.Contains("CSV");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>按优先级列出游戏目录：程序目录、其上一级、约定目录；约定目录本身不是游戏时再找它的直接子目录。</summary>
    public static ImmutableArray<string> Candidates(GameLocations locations)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        bool Add(string? directory)
        {
            if (directory is null) return false;
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (!IsGame(directory)) return false;
            if (!result.Contains(directory)) result.Add(directory);
            return true;
        }
        if (locations.ExecutableDirectory is { } executable)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executable));
            Add(full); Add(Path.GetDirectoryName(full));
        }
        if (locations.ConventionalRoot is { } root && !Add(root))
        {
            try
            {
                if (Directory.Exists(root))
                    foreach (string child in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal)) Add(child);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result.ToImmutable();
    }
    /// <summary>每个游戏一个数据目录，以游戏目录名命名；游戏更新覆盖原目录时存档仍在。</summary>
    public static string DataDirectory(GameLocations locations, string gameDirectory)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)));
        name = string.Concat(name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return Path.Combine(locations.DataRoot, name.Length == 0 ? "game" : name);
    }
}
