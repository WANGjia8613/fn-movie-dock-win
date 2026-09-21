using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MovieDock.Core.Config;
using MovieDock.Core.Models;

namespace MovieDock.Core.Llm;

public sealed class LlmException(string message) : Exception(message);

/// <summary>OpenAI 兼容接口客户端（URL 拼接规则与 Web 版一致）。ChatAsync 为 virtual，便于测试替换。</summary>
public class LlmClient
{
    public LlmClient(LlmConfig cfg) => Config = cfg;

    public LlmConfig Config { get; }

    public static LlmClient FromInput(string? baseUrl, string? apiKey, string? model, int timeoutSeconds = 60) => new(new LlmConfig
    {
        BaseUrl = (baseUrl ?? "").TrimEnd('/'),
        ApiKey = apiKey ?? "",
        Model = model ?? "",
        TimeoutSeconds = Math.Max(5, timeoutSeconds <= 0 ? 60 : timeoutSeconds),
    });

    public string BuildUrl(string path)
    {
        var baseUrl = (Config.BaseUrl ?? "").Trim().TrimEnd('/');
        if (baseUrl.Length == 0) throw new LlmException("Base URL 不能为空");
        if (baseUrl.EndsWith("/chat/completions")) return baseUrl;
        if (baseUrl.EndsWith("/v1")) return baseUrl + path;
        if (baseUrl.EndsWith("/v1/")) return baseUrl.TrimEnd('/') + path;
        if (path.StartsWith("/chat")) return baseUrl + "/v1" + path;
        return baseUrl + path;
    }

    public virtual async Task<string> ChatAsync(
        IReadOnlyList<Dictionary<string, string>> messages,
        double temperature = 0.3,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Config.ApiKey))
            throw new LlmException("尚未配置 API Key，请先在「设置」中填写");
        if (string.IsNullOrEmpty(Config.Model))
            throw new LlmException("尚未配置模型名");

        var url = BuildUrl("/chat/completions");
        var payload = new { model = Config.Model, messages, temperature };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, Config.TimeoutSeconds)) };
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(Config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(req, ct);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            throw new LlmException($"无法连接大模型接口：{exc.Message}");
        }

        using (resp)
        {
            var bodyText = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new LlmException($"大模型接口返回 {(int)resp.StatusCode}：{bodyText.Truncate(500)}");

            string content;
            try
            {
                using var doc = JsonDocument.Parse(bodyText);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                    choices.GetArrayLength() == 0)
                    throw new KeyNotFoundException();
                var msg = choices[0];
                if (!msg.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var c))
                    throw new KeyNotFoundException();
                content = ExtractContent(c);
            }
            catch (Exception exc) when (exc is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new LlmException($"大模型响应结构异常：{bodyText.Truncate(500)}");
            }
            return content;
        }
    }

    private static string ExtractContent(JsonElement c) => c.ValueKind switch
    {
        JsonValueKind.String => c.GetString() ?? "",
        JsonValueKind.Null => "",
        JsonValueKind.Array => string.Concat(c.EnumerateArray().Select(p => p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? "",
            JsonValueKind.Object when p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String => t.GetString() ?? "",
            _ => "",
        })),
        _ => c.ToString(),
    };

    public async Task<LlmTestResult> TestAsync(CancellationToken ct = default)
    {
        try
        {
            var content = await ChatAsync(new[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = "你是连通性测试助手，只回复：OK" },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = "ping" },
            }, 0, ct);
            return new LlmTestResult(true, "连接成功", Config.Model, content.Truncate(200));
        }
        catch (LlmException exc)
        {
            return new LlmTestResult(false, exc.Message, Config.Model);
        }
        catch (Exception exc)
        {
            return new LlmTestResult(false, $"未知错误：{exc.Message}", Config.Model);
        }
    }
}
