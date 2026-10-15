using System.Text;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.FileSystem;

public sealed class TextFileService(GamePathResolver paths, CompileOptions options) : ITextFileService
{
    public ValueTask<long> SoundExistsAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return ValueTask.FromResult(File.Exists(paths.Source("sound/" + path.Replace('\\', '/'))) ? 1L : 0L); }
        catch (IOException) { return ValueTask.FromResult(0L); }
    }
    public async ValueTask<long> SaveAsync(SaveTextPayload request, CancellationToken ct)
    {
        string logical;
        if (request.Path.Kind == ValueKind.Integer)
        {
            long number = request.Path.AsInt(); if (number is < 0 or > int.MaxValue) return 0;
            logical = (request.ForceSaveDirectory || options.UseSaveFolder ? "sav/" : "") + "txt" + number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".txt";
        }
        else if (request.Path.Kind == ValueKind.String)
        {
            logical = request.Path.AsString();
            if (string.IsNullOrWhiteSpace(logical)) return 0;
            if (!options.TextExtensions.Contains(Path.GetExtension(logical).TrimStart('.'), StringComparer.OrdinalIgnoreCase)) logical = Path.ChangeExtension(logical, "txt");
        }
        else throw new ScriptException("TYPE", "SAVETEXT 需要文件名或整数编号");
        try
        {
            var encoding = new UTF8Encoding(true, true);
            if (encoding.GetByteCount(request.Text) > 64L * 1024 * 1024) throw new ScriptException("TEXT_LIMIT", "文本文件超过 64 MiB");
            string path = paths.Data(logical);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, request.Text, encoding, ct);
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }
    public async ValueTask<string> LoadAsync(LoadTextPayload request, CancellationToken ct)
    {
        string logical;
        if (request.Path.Kind == ValueKind.Integer)
        {
            long number = request.Path.AsInt(); if (number < 0 || number > int.MaxValue) return "";
            logical = (request.ForceSaveDirectory || options.UseSaveFolder ? "sav/" : "") + "txt" + number.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".txt";
        }
        else if (request.Path.Kind == ValueKind.String) logical = request.Path.AsString();
        else throw new ScriptException("TYPE", "LOADTEXT 需要文件名或整数编号");
        if (!options.TextExtensions.Contains(Path.GetExtension(logical).TrimStart('.'), StringComparer.OrdinalIgnoreCase)) return "";
        try
        {
            string file = paths.Data(logical);
            if (!File.Exists(file)) file = paths.Source(logical);
            if (!File.Exists(file)) return "";
            await using var stream = File.OpenRead(file);
            if (stream.Length > 64L * 1024 * 1024) throw new ScriptException("TEXT_LIMIT", "文本文件超过 64 MiB");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, true);
            string text;
            try { text = await reader.ReadToEndAsync(ct); }
            catch (DecoderFallbackException)
            {
                stream.Position = 0; Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                using var fallback = new StreamReader(stream, Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
                text = await fallback.ReadToEndAsync(ct);
            }
            return text.Replace("\r", "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { return ""; }
    }
}
