using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;
using static MovieDock.Core.Search.JsonExt;

namespace MovieDock.Core.Search;

/// <summary>
/// qBittorrent WebUI 搜索源：复用 qB 客户端自带搜索插件（yts/bt4g/…）的 /api/v2/search 接口。
/// 自 Python 版 app/search/qbittorrent.py 移植。
/// provider.options：username / password / plugins（逗号分隔）/ category / limit / search_timeout。
/// </summary>
public sealed class QBittorrentProvider : ISearchProvider
{
    // 推荐插件：响应快的几个；避免默认全量插件（容易卡住）
    public const string DefaultPluginHint = "piratebay,yts,yts_am,bt4g,limetorrents,kickass_torrent,dmhyorg";
    // 首次搜索（超时/无结果）后自动重试的“短名单”
    public const string FallbackPlugins = "piratebay,dmhyorg";

    private readonly ProviderConfig _pc;
    private readonly string _base;
    private readonly string _username = "";
    private readonly string _password = "";
    private readonly string _plugins = "";
    private readonly string _category = "all";
    private readonly int _limit = 100;
    private readonly double _searchTimeoutSeconds = 45;

    public string Name { get; }

    public QBittorrentProvider(ProviderConfig pc)
    {
        _pc = pc;
        Name = string.IsNullOrEmpty(pc.Name) ? "qBittorrent 搜索" : pc.Name;
        _base = (string.IsNullOrEmpty(pc.Url) ? "http://127.0.0.1:8085" : pc.Url).TrimEnd('/');
        _username = SearchCommon.OptionString(pc.Options, "username");
        _password = SearchCommon.OptionString(pc.Options, "password");
        _plugins = SearchCommon.OptionString(pc.Options, "plugins").Trim();
        _category = SearchCommon.OptionString(pc.Options, "category", "all");
        if (!int.TryParse(SearchCommon.OptionString(pc.Options, "limit"), out var lim) || lim <= 0)
            lim = 100;
        _limit = lim;
        if (!double.TryParse(SearchCommon.OptionString(pc.Options, "search_timeout"), out var st) || st <= 0)
            st = 45;
        _searchTimeoutSeconds = st;
    }

    // ---------- 基础请求 ----------
    private async Task<List<string>> LoginAsync(HttpClient client)
    {
        var warnings = new List<string>();
        if (_username.Length == 0)
            return warnings;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = _username,
                ["password"] = _password,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v2/auth/login") { Content = content };
            req.Headers.TryAddWithoutValidation("Referer", _base);
            using var resp = await client.SendAsync(req);
            if ((int)resp.StatusCode == 403)
                return new List<string> { $"「{Name}」登录被拒绝（IP 被临时封禁，稍后再试）" };
            var text = (await resp.Content.ReadAsStringAsync()).Trim();
            if (text != "Ok.")
                warnings.Add($"「{Name}」登录未通过（返回 {Truncate(text, 40)}），将尝试匿名访问");
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            return new List<string> { $"「{Name}」登录失败：{exc.Message}" };
        }
        return warnings;
    }

    private async Task<List<string>> GetPluginNamesAsync(HttpClient client)
    {
        try
        {
            using var resp = await client.GetAsync($"{_base}/api/v2/search/plugins");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var names = new List<string>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return names;
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var name = JsonStr(p, "name");
                if (name.Length == 0) name = JsonStr(p, "fullName");
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }
        catch (Exception) { return new List<string>(); }
    }

    // ---------- 主流程 ----------
    public async Task<(IReadOnlyList<SourceItem> Items, IReadOnlyList<string> Warnings)> SearchAsync(
        SearchRequest req, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var pattern = (req.Query ?? "").Trim();
        if (req.Year is int year)
            pattern = $"{pattern} {year}";
        if (!string.IsNullOrEmpty(req.Quality))
            pattern = $"{pattern} {req.Quality}";

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        warnings.AddRange(await LoginAsync(client));

        var plugins = _plugins;
        if (plugins.Length == 0)
        {
            var available = await GetPluginNamesAsync(client);
            if (available.Count == 0)
            {
                return (new List<SourceItem>(), new List<string>
                {
                    $"「{Name}」未检测到搜索插件：请在 qBittorrent「搜索」页安装插件"
                    + $"（推荐 {DefaultPluginHint}），或在设置里指定 plugins",
                });
            }
            plugins = "all";
        }

        var (results, statusRaw, err) = await SearchOnceAsync(client, pattern, plugins, ct);

        // 首次无结果（尤其超时）多为插件站点不可达：换“短名单”再试一次
        if (results.Count == 0 && plugins != FallbackPlugins)
        {
            warnings.Add($"「{Name}」插件 {plugins} 无结果/超时，已自动改用短名单 {FallbackPlugins} 重试");
            var (results2, status2, err2) = await SearchOnceAsync(client, pattern, FallbackPlugins, ct);
            results = results2;
            statusRaw = status2;
            err = err2.Length > 0 ? err2 : err;
        }

        if (err.Length > 0)
            warnings.Add(err);
        if (statusRaw.ToLowerInvariant() is "running" or "queued")
        {
            if (results.Count > 0)
            {
                warnings.Add("qBittorrent 搜索超时，已展示部分结果（可在 qBittorrent 搜索页手动重试）");
            }
            else
            {
                return (new List<SourceItem>(), new List<string>
                {
                    "qBittorrent 搜索超时且无结果：多为插件站点不可达，"
                    + $"建议在设置里只保留可用的几个插件（如 {FallbackPlugins}），"
                    + "或先在 qBittorrent「搜索」页手动试一下哪些插件能用",
                });
            }
        }
        if (results.Count == 0)
            warnings.Add("qBittorrent 搜索完成但无结果（换关键词或换插件）");

        var items = results.Select((raw, idx) => ToItem(raw, idx))
            .Where(i => i.Url.Length > 0).ToList();
        if (!string.IsNullOrEmpty(req.Quality))
        {
            var q = req.Quality.ToLowerInvariant();
            var filtered = items.Where(i =>
            {
                var bucket = string.IsNullOrEmpty(i.Resolution) ? i.Quality : i.Resolution;
                return (bucket ?? "").ToLowerInvariant().Contains(q);
            }).ToList();
            if (filtered.Count > 0)
                items = filtered;
        }
        return (items.Take(_limit).ToList(), warnings);
    }

    /// <summary>跑一次搜索；返回 (结果, 状态, 错误提示)。</summary>
    private async Task<(List<JsonElement> Results, string Status, string Error)> SearchOnceAsync(
        HttpClient client, string pattern, string plugins, CancellationToken ct)
    {
        int searchId;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["pattern"] = pattern,
                ["plugins"] = plugins,
                ["category"] = _category,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v2/search/start") { Content = content };
            using var resp = await client.SendAsync(req, ct);
            if ((int)resp.StatusCode == 409)
                return (new List<JsonElement>(), "", $"「{Name}」搜索任务数已达上限，请稍后重试或清空 qBittorrent 搜索页");
            if ((int)resp.StatusCode >= 400)
            {
                var body = Truncate((await resp.Content.ReadAsStringAsync(ct)).Trim(), 120);
                return (new List<JsonElement>(), "", $"「{Name}」启动搜索失败：HTTP {(int)resp.StatusCode} {body}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            searchId = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) ? id : 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (new List<JsonElement>(), "", $"「{Name}」连接失败：请求超时（确认 WebUI 地址与端口）");
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            return (new List<JsonElement>(), "", $"「{Name}」连接失败：{exc.Message}（确认 WebUI 地址与端口）");
        }
        catch (JsonException)
        {
            return (new List<JsonElement>(), "", $"「{Name}」返回异常：非 JSON 响应");
        }

        var results = new List<JsonElement>();
        var statusRaw = "";
        var deadline = Environment.TickCount64 + (long)(_searchTimeoutSeconds * 1000);
        try
        {
            while (Environment.TickCount64 < deadline)
            {
                using var resp = await client.GetAsync(
                    $"{_base}/api/v2/search/results?id={searchId}&limit={_limit}&offset=0", ct);
                if ((int)resp.StatusCode == 200)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("results", out var arrEl) &&
                        arrEl.ValueKind == JsonValueKind.Array)
                        results = arrEl.EnumerateArray().ToList();
                    if (doc.RootElement.TryGetProperty("status", out var stEl))
                        statusRaw = stEl.GetString() ?? "";
                }
                if (statusRaw.ToLowerInvariant() is not ("running" or "queued"))
                    break;
                await Task.Delay(results.Count > 0 ? 2000 : 1500, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* 轮询超时，带部分结果返回 */ }
        catch (Exception) { /* 网络异常按已有结果处理 */ }
        finally
        {
            try
            {
                using var stopContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = searchId.ToString() });
                using var stopReq = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v2/search/stop") { Content = stopContent };
                await client.SendAsync(stopReq, ct);
                using var delContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = searchId.ToString() });
                using var delReq = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v2/search/delete") { Content = delContent };
                await client.SendAsync(delReq, ct);
            }
            catch (Exception) { /* 清理失败不影响主流程 */ }
        }
        return (results, statusRaw, "");
    }

    internal SourceItem ToItem(JsonElement raw, int idx)
    {
        var url = FirstNonEmpty(JsonStr(raw, "fileUrl"), JsonStr(raw, "magnet")).Trim();
        var title = FirstNonEmpty(JsonStr(raw, "fileName"), JsonStr(raw, "name")).Trim();
        var sizeBytes = JsonLong(raw, "fileSize");
        var size = sizeBytes is null ? JsonStr(raw, "fileSize") : SearchCommon.HumanSize(sizeBytes);
        var (resolution, quality) = SourceParser.DetectQuality(title);

        var noteBits = new List<string>();
        var siteUrl = JsonStr(raw, "siteUrl");
        if (siteUrl.Length > 0) noteBits.Add(siteUrl);
        var pubDate = JsonStr(raw, "pubDate");
        if (pubDate.Length > 0) noteBits.Add(pubDate.Length > 10 ? pubDate[..10] : pubDate);
        if (!url.StartsWith("magnet", StringComparison.OrdinalIgnoreCase))
        {
            var descrLink = JsonStr(raw, "descrLink");
            if (descrLink.Length > 0) noteBits.Add(descrLink);
        }

        return new SourceItem
        {
            Id = $"qbt-{idx}-{StableId(url)}",
            Title = title.Length > 0 ? title : $"结果 {idx + 1}",
            Quality = quality,
            Resolution = resolution,
            Size = size ?? "",
            Seeds = int.TryParse(JsonStr(raw, "nbSeeders"), out var seeds) ? seeds : null,
            Peers = int.TryParse(JsonStr(raw, "nbLeechers"), out var peers) ? peers : null,
            Source = Name,
            Url = url,
            UrlType = SourceParser.ClassifyUrl(url),
            Note = string.Join(" · ", noteBits),
            Raw = new Dictionary<string, object?>(),
        };
    }

    private static long StableId(string url)
    {
        unchecked
        {
            var h = 17L;
            foreach (var c in url) h = h * 31 + c;
            return Math.Abs(h) % 10_000_000_000L;
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
