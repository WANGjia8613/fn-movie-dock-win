using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MovieDock.Core.Subtitle;

/// <summary>SubHD 字幕站访问异常。</summary>
public sealed class SubHdException(string message) : Exception(message);

/// <summary>SubHD 字幕条目。</summary>
public sealed class SubHdEntry
{
    public string Sid { get; init; } = "";
    public string Title { get; init; } = "";
    public string Fmt { get; init; } = "";
    public string Lang { get; init; } = "";
    public string Movie { get; init; } = "";
    public string Year { get; init; } = "";
}

/// <summary>SubHD 影片条目。</summary>
public sealed class SubHdMovie
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Year { get; init; } = "";
    public string Type { get; init; } = "";
}

/// <summary>
/// SubHD（subhd.tv）字幕客户端。自 Python 版 app/subtitle/subhd.py 移植。
/// 流程（必须保持同一会话，prepare 会下发 token cookie）：
/// 搜索影片 → 列字幕条目 → prepare-download → 访问临时页 → down API → 拿真实下载 URL。
/// </summary>
public sealed class SubHdClient
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
    private const string Base = "https://subhd.tv";

    private static readonly Regex SidRegex = new(@"href=""/a/([A-Za-z0-9]+)""[^>]*>(.*?)</a>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FmtRegex = new(@">(ASS|SRT|SUP|SUB|SSA)<",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WsRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly double _timeoutSeconds;
    private readonly string _proxy;

    public SubHdClient(double timeoutSeconds = 30.0, string proxy = "")
    {
        _timeoutSeconds = timeoutSeconds;
        _proxy = (proxy ?? "").Trim();
    }

    // ---------- 会话 ----------
    private HttpClient CreateClient(CookieContainer cookies)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = cookies,
        };
        if (_proxy.Length > 0)
            handler.Proxy = new WebProxy(_proxy);
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _timeoutSeconds)),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Referrer = new Uri(Base + "/");
        return client;
    }

    private static string CleanHtml(string html) =>
        WsRegex.Replace(TagRegex.Replace(html ?? "", ""), " ").Trim();

    // ---------- 搜索 ----------
    /// <summary>搜索影片，返回多个候选（SubHD 对英文名/别名匹配很差，需多候选兜底）。</summary>
    public async Task<List<SubHdMovie>> SearchMoviesAsync(HttpClient client, string keyword)
    {
        var url = $"{Base}/searchD/{Uri.EscapeDataString(keyword)}";
        string body;
        try
        {
            body = await client.GetStringAsync(url);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            throw new SubHdException($"搜索失败：{exc.Message}");
        }
        var movies = new List<SubHdMovie>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    if (it.ValueKind != JsonValueKind.Object) continue;
                    var mid = Str(it, "id");
                    if (mid.Length == 0) continue;
                    movies.Add(new SubHdMovie
                    {
                        Id = mid,
                        Title = Str(it, "title"),
                        Year = Str(it, "year"),
                        Type = Str(it, "type"),
                    });
                }
            }
        }
        catch (JsonException exc)
        {
            throw new SubHdException($"搜索返回异常：{exc.Message}");
        }
        return movies;
    }

    /// <summary>电影优先、年份匹配优先（否则「电焊工波力」这类同系列短片会抢在最前面）。</summary>
    public static List<SubHdMovie> RankMovies(List<SubHdMovie> movies, int? year)
    {
        return movies
            .OrderBy(m =>
            {
                var typeRank = m.Type.ToLowerInvariant() == "movie" ? 0 : 1;
                var yearRank = year is int y && m.Year.Length > 0 && y.ToString() == m.Year ? 0 : (year is null ? 0 : 1);
                return (yearRank, typeRank);
            })
            .ToList();
    }

    public async Task<List<SubHdEntry>> ListSubtitlesAsync(
        HttpClient client, string movieId, string movieTitle = "", string movieYear = "")
    {
        string html;
        try
        {
            html = await client.GetStringAsync($"{Base}/d/{movieId}");
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            throw new SubHdException($"获取字幕列表失败：{exc.Message}");
        }

        var entries = new List<SubHdEntry>();
        var seenSid = new HashSet<string>();
        foreach (var m in SidRegex.Matches(html))
        {
            var match = (Match)m;
            var sid = match.Groups[1].Value;
            var title = CleanHtml(match.Groups[2].Value);
            if (title.Length == 0 || !seenSid.Add(sid)) continue;

            var tail = CleanHtml(html.Substring(Math.Min(match.Index + match.Length, html.Length),
                Math.Min(900, html.Length - Math.Min(match.Index + match.Length, html.Length))));
            var fmtM = FmtRegex.Match(html.Substring(Math.Min(match.Index + match.Length, html.Length),
                Math.Min(900, html.Length - Math.Min(match.Index + match.Length, html.Length))));
            var langs = new[] { "双语", "简体", "繁体", "英语", "国语", "粤语" }
                .Where(w => tail.Contains(w)).ToList();

            entries.Add(new SubHdEntry
            {
                Sid = sid,
                Title = title.Length > 200 ? title[..200] : title,
                Fmt = fmtM.Success ? fmtM.Groups[1].Value.ToUpperInvariant() : "",
                Lang = string.Join(" ", langs),
                Movie = movieTitle,
                Year = movieYear,
            });
        }
        return entries;
    }

    // ---------- 下载 ----------
    public async Task<string> PrepareAsync(HttpClient client, string sid)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { sid }),
            System.Text.Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/api/sub/prepare-download") { Content = content };
        req.Headers.Referrer = new Uri($"{Base}/a/{sid}");
        using var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new SubHdException($"prepare 返回异常：{Truncate(text, 120)}");
        }
        using (doc)
        {
            var success = doc.RootElement.TryGetProperty("success", out var s) &&
                          (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.String && s.GetString() == "true"));
            if (!success)
                throw new SubHdException($"prepare 失败：{Truncate(text, 160)}");
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (url.Length == 0)
                throw new SubHdException("prepare 未返回临时下载页");
            // 必须访问临时页，拿到页面 token（响应要 dispose，否则连接被占用）
            using (await client.GetAsync(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : Base + url)) { }
            return url;
        }
    }

    public async Task<string> ResolveDownloadUrlAsync(HttpClient client, string sid)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { sid }),
            System.Text.Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/api/sub/down") { Content = content };
        req.Headers.Referrer = new Uri($"{Base}/down/{sid}");
        using var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new SubHdException($"down 返回异常：{Truncate(text, 120)}");
        }
        using (doc)
        {
            var success = doc.RootElement.TryGetProperty("success", out var s) &&
                          (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.String && s.GetString() == "true"));
            var hasPass = doc.RootElement.TryGetProperty("pass", out var p) &&
                          (p.ValueKind is JsonValueKind.True or JsonValueKind.String or JsonValueKind.Number);
            if (!success || !hasPass)
                throw new SubHdException($"下载被拒绝：{Truncate(text, 160)}");
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (url.Length == 0)
                throw new SubHdException("down 未返回下载地址");
            return url;
        }
    }

    public async Task<string> DownloadArchiveAsync(HttpClient client, string url, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var name = url.Split('/').LastOrDefault() ?? "";
        name = name.Split('?')[0];
        if (name.Length == 0) name = "subtitle.bin";

        string path;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(Base + "/");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ctype.Contains("zip") && !name.ToLowerInvariant().EndsWith(".zip"))
                name += ".zip";
            path = Path.Combine(destDir, name);
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(path);
            await src.CopyToAsync(dst, 64 * 1024);
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            throw new SubHdException($"下载字幕包失败：{exc.Message}");
        }

        var size = new FileInfo(path).Length;
        if (size < 128)
            throw new SubHdException("下载到的文件过小，可能链接已过期");
        return path;
    }

    // ---------- 组合流程 ----------
    /// <summary>按关键词（或指定 sid）下载字幕压缩包，返回归档文件路径。</summary>
    public async Task<string> FetchAsync(string keyword, string destDir, string? sid = null)
    {
        var cookies = new CookieContainer();
        using var client = CreateClient(cookies);
        using (await client.GetAsync(Base + "/")) { /* 仅建立会话取 cookie，响应立即释放 */ }

        var target = sid;
        if (string.IsNullOrEmpty(target))
        {
            var movies = await SearchMoviesAsync(client, keyword);
            var movieId = movies.Count > 0 ? movies[0].Id : "";
            if (movieId.Length == 0)
                throw new SubHdException($"未找到影片：{keyword}");
            var entries = await ListSubtitlesAsync(client, movieId);
            if (entries.Count == 0)
                throw new SubHdException($"影片「{keyword}」暂无字幕条目");
            target = entries[0].Sid;
        }
        await PrepareAsync(client, target);
        var url = await ResolveDownloadUrlAsync(client, target);
        return await DownloadArchiveAsync(client, url, destDir);
    }

    /// <summary>按关键词取字幕条目；会把搜索到的前几部影片合并去重。</summary>
    public async Task<List<SubHdEntry>> ListByKeywordAsync(string keyword, int? year = null, int maxMovies = 2)
    {
        var cookies = new CookieContainer();
        using var client = CreateClient(cookies);
        using (await client.GetAsync(Base + "/")) { /* 仅建立会话取 cookie，响应立即释放 */ }

        var movies = await SearchMoviesAsync(client, keyword);
        if (movies.Count == 0) return new List<SubHdEntry>();

        var entries = new List<SubHdEntry>();
        var seen = new HashSet<string>();
        foreach (var movie in RankMovies(movies, year).Take(Math.Max(1, maxMovies)))
        {
            foreach (var entry in await ListSubtitlesAsync(client, movie.Id, movie.Title, movie.Year))
            {
                if (seen.Add(entry.Sid))
                    entries.Add(entry);
            }
        }
        return entries;
    }

    public Task<string> FetchEntryAsync(string sid, string destDir) => FetchAsync("", destDir, sid);

    private static string Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
