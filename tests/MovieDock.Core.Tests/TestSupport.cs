using System.Text.Json;
using MovieDock.Core.Config;
using MovieDock.Core.Download;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;
using MovieDock.Core.Search;

namespace MovieDock.Core.Tests;

/// <summary>测试共用工具：临时目录、aria2 假实现、aria2 状态字典构造、假索引源、假大模型。</summary>
internal static class TestSupport
{
    private static int _seq;

    /// <summary>独立临时目录（每个用例一个，避免 xUnit 并行互相踩）。</summary>
    public static string NewTempDir(string prefix = "moviedock-test-")
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            prefix + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Interlocked.Increment(ref _seq) + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把 JSON 对象文本转成 aria2 tellStatus 返回形状的字典。</summary>
    public static Dictionary<string, JsonElement> Aria2State(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in doc.RootElement.EnumerateObject())
            dict[p.Name] = p.Value.Clone();
        return dict;
    }

    /// <summary>测试用 AppConfig：下载根/状态目录都指向临时目录，字幕关闭。</summary>
    public static AppConfig NewConfig(string root)
    {
        var cfg = new AppConfig();
        cfg.Paths.DownloadRoot = Path.Combine(root, "dl");
        cfg.Paths.StateDir = Path.Combine(root, "data");
        cfg.Subtitle.Enabled = false;
        cfg.Organize.LibraryRoot = "";
        return cfg;
    }
}

/// <summary>假 aria2：按 gid 返回预设状态，记录 call 调用（对齐 Python _FakeAria2/_FakeAria2Calls）。</summary>
internal sealed class FakeAria2 : Aria2Client
{
    public Dictionary<string, Dictionary<string, JsonElement>> States { get; } = new();
    public List<(string Method, IReadOnlyList<object>? Params)> Calls { get; } = new();
    public HashSet<string> FailMethods { get; } = new();

    public FakeAria2() : base("", "") { }

    public override Task<Dictionary<string, JsonElement>> TellStatusAsync(string gid, CancellationToken ct = default)
        => Task.FromResult(States.TryGetValue(gid, out var s) ? s : new Dictionary<string, JsonElement>());

    public override Task<JsonElement> CallAsync(string method, IReadOnlyList<object>? parameters = null, CancellationToken ct = default)
    {
        if (FailMethods.Contains(method))
            throw new InvalidOperationException("boom");
        Calls.Add((method, parameters));
        return Task.FromResult(JsonSerializer.SerializeToElement("OK"));
    }

    /// <summary>是否记录过「方法 + 第一个参数为 gid」的调用。</summary>
    public bool CalledWith(string method, string gid) =>
        Calls.Any(c => c.Method == method && c.Params is { Count: > 0 } p && p[0]?.ToString() == gid);
}

/// <summary>假索引源：返回固定结果，不走 HTTP（对齐 Python _FakeSource/_GarbageSource）。</summary>
internal class FakeBuiltinSource : BuiltinSource
{
    private readonly Func<string, (List<SourceItem> Items, string Error)> _handler;

    public FakeBuiltinSource(string key, string label, Func<string, (List<SourceItem>, string)> handler)
    {
        _handler = handler;
        Key = key;
        Label = label;
    }

    public override string Key { get; }
    public override string Label { get; }
    protected override IReadOnlyList<string> Endpoints => new[] { "http://x" };
    protected override string BuildUrl(string baseUrl, string query) => baseUrl;
    public override List<SourceItem> Parse(string payload, string baseUrl) => new();

    public override Task<(List<SourceItem> Items, string Error)> SearchAsync(
        HttpClient client, string query, string sourceName, CancellationToken ct)
        => Task.FromResult(_handler(query));
}

/// <summary>记录收到过的搜索词的假源（验证中文 → 英文翻译后再搜）。</summary>
internal sealed class RecordingSource : FakeBuiltinSource
{
    public List<string> Seen { get; } = new();

    public RecordingSource() : base("rec", "记录源", _ => (new List<SourceItem>(), "无结果"))
    {
    }

    public Func<string, (List<SourceItem> Items, string Error)>? OnQuery { get; set; }

    public override Task<(List<SourceItem> Items, string Error)> SearchAsync(
        HttpClient client, string query, string sourceName, CancellationToken ct)
    {
        Seen.Add(query);
        return Task.FromResult(OnQuery?.Invoke(query) ?? (new List<SourceItem>(), "无结果"));
    }
}

/// <summary>假大模型：固定返回同一文本（默认 "WALL-E"）。</summary>
internal sealed class FakeLlm : LlmClient
{
    private readonly string _reply;

    public FakeLlm(string reply = "WALL-E")
        : base(new LlmConfig { ApiKey = "sk-test", Model = "fake" })
    {
        _reply = reply;
    }

    public override Task<string> ChatAsync(
        IReadOnlyList<Dictionary<string, string>> messages,
        double temperature = 0.3,
        CancellationToken ct = default)
        => Task.FromResult(_reply);
}
