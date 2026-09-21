using System.Text;
using System.Text.Json;

namespace MovieDock.Core.Download;

/// <summary>aria2 JSON-RPC 客户端（addUri / tellStatus / getVersion）。方法为 virtual，便于测试替换为假实现。</summary>
public class Aria2Client(string rpcUrl, string secret = "")
{
    private int _id;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    // 轮询每 2 秒一次调用：共享静态 HttpClient，避免每调用新建导致 TIME_WAIT 堆积
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string RpcUrl { get; } = rpcUrl;
    public string Secret { get; } = secret;

    public virtual async Task<JsonElement> CallAsync(string method, IReadOnlyList<object>? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _id);
        var ps = new List<object>();
        if (!string.IsNullOrEmpty(Secret)) ps.Add($"token:{Secret}");
        if (parameters != null) ps.AddRange(parameters);
        var payload = new { jsonrpc = "2.0", id = id.ToString(), method, @params = ps };

        using var resp = await SharedClient.PostAsync(
            RpcUrl,
            new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json"),
            ct);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement.Clone();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            throw new InvalidOperationException(err.ToString());
        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    public virtual async Task<string> AddUriAsync(IReadOnlyList<string> uris, IReadOnlyDictionary<string, string> options, CancellationToken ct = default)
    {
        var result = await CallAsync("aria2.addUri", new object[] { uris.ToList(), options }, ct);
        return result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.ToString();
    }

    private static readonly string[] StatusKeys =
        { "gid", "status", "totalLength", "completedLength", "downloadSpeed", "files", "errorMessage", "dir",
          // 磁力链接：元数据下载会派生真正的下载任务
          "followedBy", "following", "belongsTo" };

    public virtual async Task<Dictionary<string, JsonElement>> TellStatusAsync(string gid, CancellationToken ct = default)
    {
        var result = await CallAsync("aria2.tellStatus", new object[] { gid, StatusKeys }, ct);
        var dict = new Dictionary<string, JsonElement>();
        if (result.ValueKind == JsonValueKind.Object)
            foreach (var p in result.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
        return dict;
    }

    public virtual async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var ver = await CallAsync("aria2.getVersion", null, ct);
            return ver.ValueKind is JsonValueKind.Object or JsonValueKind.String;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public virtual async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var ver = await CallAsync("aria2.getVersion", null, ct);
            if (ver.ValueKind == JsonValueKind.Object && ver.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
            return ver.ToString();
        }
        catch (Exception)
        {
            return "";
        }
    }
}
