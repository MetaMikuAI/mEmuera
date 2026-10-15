using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using mEmuera.Contracts;

namespace mEmuera.Infrastructure.Plugins;

public sealed class ChatPluginService(HttpClient client) : IPluginService
{
    private string url = "http://127.0.0.1:9000/chat", key = "", model = "", lastError = "";
    private long lastStatus;
    public async ValueTask<PluginData> ExecuteAsync(PluginPayload request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var values = request.Arguments.Select(a => a.Value).ToImmutableArray().ToBuilder();
        string Text(int index) => values[index].Kind == ValueKind.String ? values[index].AsString() : "";
        void SetText(int index, string value) { if (values[index].Kind == ValueKind.String) values[index] = new(value); }
        switch (request.Method.ToUpperInvariant())
        {
            case "MODELSET":
                if (values.Count < 2) { lastStatus = 2; lastError = "ModelSet 至少需要 api 和 key"; break; }
                url = string.IsNullOrWhiteSpace(Text(0)) ? "http://127.0.0.1:9000/chat" : Text(0).Trim();
                key = Text(1).Trim(); model = values.Count >= 3 ? Text(2).Trim() : "";
                lastStatus = 0; lastError = ""; break;
            case "LASTSTATUS":
                if (values.Count > 0 && values[0].Kind == ValueKind.Integer) values[0] = new(lastStatus);
                break;
            case "LASTERROR":
                if (values.Count > 0) SetText(0, lastError);
                break;
            case "CHAT":
            case "CHATRAW":
                if (values.Count < 2) break;
                string raw = "", reply = "";
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, url);
                    if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                    object payload = url.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)
                        ? new { model, messages = new[] { new { role = "user", content = Text(0) } } } : new { message = Text(0) };
                    message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellation);
                    await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, cancellation);
                    raw = await response.Content.ReadAsStringAsync(cancellation);
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {raw}");
                    using var json = JsonDocument.Parse(raw);
                    reply = ExtractReply(json.RootElement) ?? throw new InvalidOperationException("响应中没有找到 response 或 choices[0] 内容");
                    lastStatus = 0; lastError = "";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or FormatException or OperationCanceledException)
                { lastStatus = 1; lastError = error.Message; }
                SetText(1, request.Method.Equals("CHATRAW", StringComparison.OrdinalIgnoreCase) ? raw : lastStatus == 0 ? reply : "");
                if (values.Count >= 7 && request.Method.Equals("CHAT", StringComparison.OrdinalIgnoreCase))
                {
                    string[] fields = ["favor", "trust", "command_exists", "command_type", "disgust"];
                    for (int i = 0; i < fields.Length; i++) SetText(i + 2, Field(raw, fields[i]));
                }
                break;
            default: throw new ScriptException("PLUGIN_METHOD", "尚未适配插件方法: " + request.Method);
        }
        return new(values.ToImmutable());
    }
    private static string? ExtractReply(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response)) return response.ValueKind == JsonValueKind.String ? response.GetString() ?? "" : response.ToString();
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)) return content.GetString() ?? content.ToString();
            if (first.TryGetProperty("text", out var text)) return text.GetString() ?? text.ToString();
        }
        return null;
    }
    private static string Field(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(name, out var field) ? field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : field.ToString() : "";
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return ""; }
    }
}
