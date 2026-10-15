using System.Text;
using SkiaSharp;

namespace mEmuera.Rendering;

/// <summary>字体名到字形的解析。游戏自带的 font/ 优先；缺字时依次尝试已登记字体和系统回退。</summary>
public sealed class FontCatalog : IDisposable
{
    // 原版在 Windows 上直接取系统字体；其他平台没有这些名字，指向游戏常带的同类字体。
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["黑体"] = ["simhei", "stheitisc-light", "stheitisclight", "heitisc"],
        ["黑体-简"] = ["stheitisc-light", "stheitisclight"],
        ["ms明朝"] = ["msmincho"], ["msp明朝"] = ["mspmincho"],
        ["msゴシック"] = ["msgothic"], ["mspゴシック"] = ["mspgothic"],
        ["ms ui gothic"] = ["msuigothic"],
        ["monospace"] = ["msgothic", "dejavusansmono"],
    };
    private readonly Dictionary<string, SKTypeface> byName = new(StringComparer.Ordinal);
    private readonly List<SKTypeface> faces = [];
    private readonly Dictionary<int, SKTypeface?> fallbackByRune = [];
    private readonly object gate = new();
    private bool disposed;

    public IReadOnlyList<SKTypeface> Faces => faces;
    public IEnumerable<string> Names => byName.Keys;

    public static string Key(string name) => new string(name.Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '_')).ToArray()).ToLowerInvariant();

    /// <summary>登记目录里的 ttf/otf/ttc；ttc 里的每个字体都登记。返回登记数量。</summary>
    public int AddDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        int added = 0;
        foreach (string file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            if (Path.GetExtension(file).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc") added += AddFile(file);
        return added;
    }
    public int AddFile(string path)
    {
        int added = 0;
        for (int index = 0; index < 32; index++)
        {
            var face = SKTypeface.FromFile(path, index);
            if (face is null) break;
            Register(face, Path.GetFileNameWithoutExtension(path)); added++;
        }
        return added;
    }
    public void Add(SKTypeface face, string? alias = null) => Register(face, alias);
    private void Register(SKTypeface face, string? alias)
    {
        faces.Add(face);
        byName.TryAdd(Key(face.FamilyName), face);
        if (alias is not null) byName.TryAdd(Key(alias), face);
    }

    /// <summary>按名称取字体；找不到时返回第一个登记字体，再不行用系统默认。</summary>
    public SKTypeface Resolve(string? name)
    {
        lock (gate) return ResolveUnlocked(name);
    }
    private SKTypeface ResolveUnlocked(string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            string key = Key(name);
            if (byName.TryGetValue(key, out var exact)) return exact;
            if (Aliases.TryGetValue(key, out var targets) || Aliases.TryGetValue(name, out targets))
                foreach (string target in targets) if (byName.TryGetValue(target, out var aliased)) return aliased;
            var system = SKTypeface.FromFamilyName(name);
            if (system is not null && Key(system.FamilyName) == key) { Register(system, name); return system; }
            system?.Dispose();
        }
        return faces.Count > 0 ? faces[0] : SKTypeface.Default;
    }

    /// <summary>首选字体缺这个字形时的替代字体；全都没有则返回首选字体。</summary>
    public SKTypeface ForRune(SKTypeface preferred, int rune)
    {
        lock (gate) return ForRuneUnlocked(preferred, rune);
    }
    private SKTypeface ForRuneUnlocked(SKTypeface preferred, int rune)
    {
        if (preferred.ContainsGlyph(rune)) return preferred;
        if (fallbackByRune.TryGetValue(rune, out var cached)) return cached ?? preferred;
        SKTypeface? found = faces.FirstOrDefault(face => face.ContainsGlyph(rune));
        if (found is null)
        {
            try { found = SKFontManager.Default.MatchCharacter(rune); }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException) { found = null; }
        }
        fallbackByRune[rune] = found;
        return found ?? preferred;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var face in faces) face.Dispose();
        faces.Clear(); byName.Clear();
    }
}
