using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;
using static MovieDock.Core.Search.JsonExt;

namespace MovieDock.Core.Search;

/// <summary>
/// 内置直连索引源聚合（TPB / YTS / DMHY，不依赖 qBittorrent）。
/// 每个源都带镜像列表，按顺序尝试；任一成功即用。自 Python 版 app/search/builtin.py 移植。
/// provider.options：sources（"tpb,yts,dmhy"）、proxy（源专用代理）、timeout（秒）、limit（每源最多条数）。
/// </summary>
public sealed class BuiltinProvider : ISearchProvider
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    public const string DefaultSources = "tpb,yts,dmhy";

    private readonly ProviderConfig _pc;
    private readonly LlmClient? _llm;
    private readonly List<BuiltinSource> _sources = new();
    private readonly string _proxy = "";
    private readonly double _timeoutSeconds = 20;
    private readonly int _limit = 60;

    public string Name { get; }

    /// <summary>实际生效的源列表（按 options.sources 过滤后）。测试可见。</summary>
    internal IReadOnlyList<BuiltinSource> Sources => _sources;
    /// <summary>实际生效的代理（源级优先于全局）。测试可见。</summary>
    internal string Proxy => _proxy;
    /// <summary>测试注入假源（不走 HTTP）。</summary>
    internal void SetSourcesForTesting(IEnumerable<BuiltinSource> sources)
    {
        _sources.Clear();
        _sources.AddRange(sources);
    }

    public BuiltinProvider(
        ProviderConfig pc,
        string globalProxy = "",
        int globalTimeout = 20,
        LlmClient? llm = null)
    {
        _pc = pc;
        _llm = llm;
        Name = string.IsNullOrEmpty(pc.Name) ? "内置索引" : pc.Name;

        var keys = SearchCommon.OptionString(pc.Options, "sources", DefaultSources)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant())
            .Where(BuiltinSources.Registry.ContainsKey)
            .ToList();
        _sources = keys.Select(k => BuiltinSources.Registry[k]()).ToList();
        if (_sources.Count == 0)
            _sources.Add(new TpbSource());

        _proxy = SearchCommon.OptionString(pc.Options, "proxy", globalProxy).Trim();
        if (!double.TryParse(SearchCommon.OptionString(pc.Options, "timeout"), out var t) || t <= 0)
            t = globalTimeout > 0 ? globalTimeout : 20;
        _timeoutSeconds = t;
        if (!int.TryParse(SearchCommon.OptionString(pc.Options, "limit"), out var lim) || lim <= 0)
            lim = 60;
        _limit = lim;
    }

    public async Task<(IReadOnlyList<SourceItem> Items, IReadOnlyList<string> Warnings)> SearchAsync(
        SearchRequest req, CancellationToken ct = default)
    {
        var query = (req.Query ?? "").Trim();
        if (query.Length == 0)
            return (new List<SourceItem>(), new List<string> { "「内置索引」请输入片名" });

        var cjk = SearchCommon.ContainsCjk(query);
        var hints = new List<string> { query };
        if (req.Year is int year)
            hints.Add($"{query} {year}");
        // 中文关键词在英文站上无效：有大模型就自动翻成英文再搜
        if (cjk)
        {
            var english = await LlmEnglishTitleAsync(query, req.Year, ct);
            if (english.Length > 0)
            {
                hints.Insert(0, english);
                if (req.Year is int y2)
                    hints.Insert(1, $"{english} {y2}");
            }
        }

        var warnings = new List<string>();
        var items = new List<SourceItem>();
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        if (_proxy.Length > 0)
            handler.Proxy = new WebProxy(_proxy);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(3, _timeoutSeconds)),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        foreach (var source in _sources)
        {
            source.Limit = _limit;
            foreach (var hint in hints)
            {
                var (found, err) = await source.SearchAsync(client, hint, Name, ct);
                if (err.Length > 0)
                {
                    if (hint == hints[0])
                        warnings.Add(err);
                    continue;
                }
                items.AddRange(found);
                break; // 该源用第一个有结果的提示词即可
            }
        }

        // 去重（按 url）
        var seen = new HashSet<string>();
        var uniq = new List<SourceItem>();
        foreach (var item in items)
        {
            var key = item.Url ?? "";
            if (key.Length == 0 || !seen.Add(key)) continue;
            uniq.Add(item);
        }

        // 相关度过滤：索引站常返回“沾边”结果（中文关键词甚至会返回一堆无关内容）
        var relevant = uniq.Where(i =>
            SearchCommon.IsRelevant(query, i.Title) ||
            (hints[0] != query && SearchCommon.IsRelevant(hints[0], i.Title))).ToList();
        if (relevant.Count > 0)
        {
            uniq = relevant;
        }
        else if (uniq.Count > 0 && (Ranking.QueryTokens(query).Count > 0 || cjk))
        {
            var reason = cjk
                ? "中文关键词在这些英文索引站上无效（TPB/YTS 不会中文匹配，返回的都是无关结果）。"
                  + "建议：① 用英文片名搜（例：WALL-E）；② 在「设置 → 网络（代理）」填代理以启用 DMHY 中文源；"
                  + "③ 在「设置 → 大模型」配好 API Key 后，会自动把中文名翻成英文再搜。"
                : "返回的结果与关键词都不相关，已过滤；换个更准确的关键词试试。";
            return (new List<SourceItem>(), warnings.Append(reason).ToList());
        }

        if (uniq.Count == 0)
        {
            warnings.Add(
                $"内置索引（{string.Join(", ", _sources.Select(s => s.Label))}）都没有结果："
                + "换关键词、或在设置里给该源配代理（全局 network.proxy）");
        }

        if (!string.IsNullOrEmpty(req.Quality))
        {
            var q = req.Quality.ToLowerInvariant();
            var filtered = uniq.Where(i =>
            {
                var bucket = string.IsNullOrEmpty(i.Resolution) ? i.Quality : i.Resolution;
                return (bucket ?? "").ToLowerInvariant().Contains(q);
            }).ToList();
            if (filtered.Count > 0)
                uniq = filtered;
        }

        return (uniq, warnings);
    }

    /// <summary>用已配置的大模型把中文片名换成英文名（失败不影响主流程）。</summary>
    private async Task<string> LlmEnglishTitleAsync(string query, int? year, CancellationToken ct)
    {
        if (_llm is null || string.IsNullOrEmpty(_llm.Config.ApiKey))
            return "";
        var hint = year is int y ? $"{query} {y}" : query;
        var system =
            "你是影视名称助手。用户给一个中文片名，输出它最常见的**英文片名**（用于 BT 站搜索），"
            + "只输出片名本身，不要年份、不要引号、不要其它说明。";
        string text;
        try
        {
            text = await _llm.ChatAsync(new List<Dictionary<string, string>>
            {
                new() { ["role"] = "system", ["content"] = system },
                new() { ["role"] = "user", ["content"] = hint },
            }, 0, ct);
        }
        catch (Exception) { return ""; }

        text = (text ?? "").Trim().Trim('"', '\'', '`', '。', '.', ' ');
        if (text.Length == 0 || SearchCommon.ContainsCjk(text) || text.Length > 80)
            return "";
        return text;
    }
}

/// <summary>单个索引源的实现：镜像轮询 + 解析。</summary>
public abstract class BuiltinSource
{
    public abstract string Key { get; }
    public abstract string Label { get; }
    protected abstract IReadOnlyList<string> Endpoints { get; }
    public int Limit { get; set; } = 60;

    protected abstract string BuildUrl(string baseUrl, string query);

    /// <summary>解析响应正文为候选列表（解析失败抛异常）。</summary>
    public abstract List<SourceItem> Parse(string payload, string baseUrl);

    /// <summary>按镜像顺序尝试；返回 (结果, 错误信息)。virtual：测试可用假源覆盖、不走 HTTP。</summary>
    public virtual async Task<(List<SourceItem> Items, string Error)> SearchAsync(
        HttpClient client, string query, string sourceName, CancellationToken ct)
    {
        var lastError = "";
        foreach (var baseUrl in Endpoints)
        {
            var url = BuildUrl(baseUrl, query);
            string body;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Referer", baseUrl);
                using var resp = await client.SendAsync(req, ct);
                if ((int)resp.StatusCode >= 400)
                {
                    lastError = $"{baseUrl} HTTP {(int)resp.StatusCode}";
                    continue;
                }
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 外部取消立即上抛，不能当作镜像失败继续遍历
            }
            catch (OperationCanceledException)
            {
                lastError = $"{baseUrl} 请求超时";
                continue;
            }
            catch (HttpRequestException exc)
            {
                lastError = $"{baseUrl} 连接失败：{exc.Message}";
                continue;
            }

            List<SourceItem> parsed;
            try
            {
                parsed = Parse(body, baseUrl);
            }
            catch (Exception exc)
            {
                lastError = $"{baseUrl} 解析失败：{exc.Message}";
                continue;
            }
            if (parsed.Count > 0)
                return (parsed.Take(Limit).ToList(), "");
            lastError = $"{baseUrl} 无结果";
        }
        return (new List<SourceItem>(), $"「{sourceName}」{Label}：{(lastError.Length > 0 ? lastError : "不可用")}");
    }
}

// ---------------------------------------------------------------- TPB (apibay)
public sealed class TpbSource : BuiltinSource
{
    public override string Key => "tpb";
    public override string Label => "海盗湾";
    protected override IReadOnlyList<string> Endpoints => new[] { "https://apibay.org", "https://apibay.nl" };

    protected override string BuildUrl(string baseUrl, string query) =>
        $"{baseUrl}/q.php?q={Uri.EscapeDataString(query)}";

    public override List<SourceItem> Parse(string payload, string baseUrl)
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<SourceItem>();

        var items = new List<SourceItem>();
        var idx = 0;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            idx++;
            if (row.ValueKind != JsonValueKind.Object) continue;
            var name = JsonStr(row, "name").Trim();
            var infoHash = JsonStr(row, "info_hash").Trim();
            // apibay 无结果时会返回一条 name 为 "No results returned" 的占位
            if (name.Length == 0 || infoHash.Length == 0 ||
                name.ToLowerInvariant().Contains("no results")) continue;

            var (resolution, quality) = SourceParser.DetectQuality(name);
            items.Add(new SourceItem
            {
                Id = $"tpb-{idx}-{infoHash[..Math.Min(8, infoHash.Length)]}",
                Title = name,
                Quality = quality,
                Resolution = resolution,
                Size = SearchCommon.HumanSize(JsonStr(row, "size")),
                Seeds = SearchCommon.IntOrNull(JsonStr(row, "seeders")),
                Peers = SearchCommon.IntOrNull(JsonStr(row, "leechers")),
                Source = Label,
                Url = SearchCommon.BuildMagnet(infoHash, name),
                UrlType = UrlType.Magnet,
                Note = $"TPB · 文件数 {JsonOr(row, "num_files", "?")}",
                Raw = new Dictionary<string, object?> { ["info_hash"] = infoHash },
            });
        }
        return items;
    }
}

// ---------------------------------------------------------------- YTS
public sealed class YtsSource : BuiltinSource
{
    public override string Key => "yts";
    public override string Label => "YTS";
    protected override IReadOnlyList<string> Endpoints =>
        new[] { "https://yts.mx", "https://yts.rs", "https://yts.lt", "https://yts.am" };

    protected override string BuildUrl(string baseUrl, string query) =>
        $"{baseUrl}/api/v2/list_movies.json?query_term={Uri.EscapeDataString(query)}&limit=50";

    public override List<SourceItem> Parse(string payload, string baseUrl)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var movies = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("movies", out var moviesEl)
            && moviesEl.ValueKind == JsonValueKind.Array
            ? moviesEl.EnumerateArray().ToList()
            : new List<JsonElement>();

        var items = new List<SourceItem>();
        foreach (var movie in movies)
        {
            if (movie.ValueKind != JsonValueKind.Object) continue;
            var title = FirstNonEmpty(JsonStr(movie, "title_long"), JsonStr(movie, "title")).Trim();
            var yearStr = JsonStr(movie, "year");
            if (yearStr.Length > 0 && !title.Contains(yearStr))
                title = $"{title} ({yearStr})";

            if (!movie.TryGetProperty("torrents", out var torrentsEl) ||
                torrentsEl.ValueKind != JsonValueKind.Array) continue;
            foreach (var torrent in torrentsEl.EnumerateArray())
            {
                if (torrent.ValueKind != JsonValueKind.Object) continue;
                var quality = JsonStr(torrent, "quality");
                var (resolution, _) = SourceParser.DetectQuality(quality);
                var infoHash = JsonStr(torrent, "hash");
                var label = string.Join(" ",
                    title, quality, JsonStr(torrent, "type"), JsonStr(torrent, "video_codec")).Trim();
                var size = SearchCommon.HumanSize(JsonLong(torrent, "size_bytes"));
                if (size.Length == 0) size = JsonStr(torrent, "size");
                items.Add(new SourceItem
                {
                    Id = infoHash.Length > 0
                        ? $"yts-{infoHash[..Math.Min(10, infoHash.Length)]}"
                        : $"yts-{items.Count}",
                    Title = label,
                    Quality = quality.Length > 0 ? quality : "未知",
                    Resolution = resolution,
                    Size = size,
                    Seeds = SearchCommon.IntOrNull(JsonStr(torrent, "seeds")),
                    Peers = SearchCommon.IntOrNull(JsonStr(torrent, "peers")),
                    Source = Label,
                    Url = SearchCommon.BuildMagnet(infoHash, label),
                    UrlType = infoHash.Length > 0 ? UrlType.Magnet : UrlType.Unknown,
                    Note = $"YTS · {JsonStr(torrent, "type")}".Trim(),
                    Raw = new Dictionary<string, object?> { ["hash"] = infoHash },
                });
            }
        }
        return items.Where(i => i.UrlType == UrlType.Magnet).ToList();
    }
}

// ---------------------------------------------------------------- DMHY (RSS)
public sealed class DmhySource : BuiltinSource
{
    private static readonly Regex SizeRegex = new(@"([\d.]+)\s*(GB|MB)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string Key => "dmhy";
    public override string Label => "动漫花园";
    protected override IReadOnlyList<string> Endpoints => new[] { "https://share.dmhy.org", "https://dmhy.org" };

    protected override string BuildUrl(string baseUrl, string query) =>
        $"{baseUrl}/topics/rss/rss.xml?keyword={Uri.EscapeDataString(query)}";

    public override List<SourceItem> Parse(string payload, string baseUrl)
    {
        var root = XDocument.Parse(payload).Root;
        if (root is null) return new List<SourceItem>();

        var items = new List<SourceItem>();
        foreach (var entry in root.Descendants("item"))
        {
            var title = (entry.Element("title")?.Value ?? "").Trim();
            var magnet = entry.Element("enclosure")?.Attribute("url")?.Value ?? "";
            if (string.IsNullOrEmpty(magnet))
            {
                foreach (var el in entry.Descendants())
                {
                    var text = (el.Value ?? "").Trim();
                    if (text.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                    {
                        magnet = text;
                        break;
                    }
                }
            }
            if (title.Length == 0 || !magnet.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                continue;

            var description = entry.Element("description")?.Value ?? "";
            var sizeMatch = SizeRegex.Match(description);
            var size = sizeMatch.Success
                ? $"{sizeMatch.Groups[1].Value} {sizeMatch.Groups[2].Value.ToUpperInvariant()}"
                : "";
            var pubDate = entry.Element("pubDate")?.Value ?? "";
            var (resolution, quality) = SourceParser.DetectQuality(title);
            items.Add(new SourceItem
            {
                Id = $"dmhy-{items.Count}",
                Title = title,
                Quality = quality,
                Resolution = resolution,
                Size = size,
                Source = Label,
                Url = magnet,
                UrlType = UrlType.Magnet,
                Note = "DMHY · " + (pubDate.Length > 16 ? pubDate[..16] : pubDate),
            });
        }
        return items;
    }
}

public static class BuiltinSources
{
    public static readonly Dictionary<string, Func<BuiltinSource>> Registry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tpb"] = () => new TpbSource(),
        ["yts"] = () => new YtsSource(),
        ["dmhy"] = () => new DmhySource(),
    };
}

internal static class JsonExt
{
    public static string JsonStr(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.ToString(),
                _ => "",
            };
        }
        return "";
    }

    public static long? JsonLong(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            return n;
        return null;
    }

    public static string JsonOr(JsonElement el, string name, string fallback)
    {
        var s = JsonStr(el, name);
        return s.Length > 0 ? s : fallback;
    }

    public static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
            if (v.Length > 0) return v;
        return "";
    }
}
