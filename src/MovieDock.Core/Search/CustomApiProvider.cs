using System.Text;
using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;

namespace MovieDock.Core.Search;

/// <summary>
/// 用户自定义检索接口。
/// 约定：GET 默认查询参数 q / query, year, quality；POST 时 JSON 提交相同字段；
/// 响应 JSON 数组，或 {items|results|sources: [...]}；
/// 字段兼容 title/name, url/magnet/link, quality, resolution, size, seeds, note。
/// </summary>
public sealed class CustomApiProvider(ProviderConfig pc) : ISearchProvider
{
    public string Name { get; } = string.IsNullOrEmpty(pc.Name) ? "自定义索引" : pc.Name;

    public async Task<(IReadOnlyList<SourceItem>, IReadOnlyList<string>)> SearchAsync(
        SearchRequest req, CancellationToken ct = default)
    {
        var url = (pc.Url ?? "").Trim();
        if (url.Length == 0)
            return ([], [$"「{Name}」未配置 url，已跳过（可在设置中填写）"]);

        var query = req.Query.Trim();
        var parameters = new Dictionary<string, string> { ["q"] = query, ["query"] = query };
        if (req.Year is int y) parameters["year"] = y.ToString();
        if (!string.IsNullOrEmpty(req.Quality)) parameters["quality"] = req.Quality;

        var method = string.IsNullOrEmpty(pc.Method) ? "GET" : pc.Method.ToUpperInvariant();
        var headers = pc.Headers ?? new Dictionary<string, string>();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage resp;
        try
        {
            if (method == "POST")
            {
                using var post = new HttpRequestMessage(HttpMethod.Post, url);
                post.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(parameters), Encoding.UTF8, "application/json");
                foreach (var (k, v) in headers) post.Headers.TryAddWithoutValidation(k, v);
                resp = await client.SendAsync(post, ct);
            }
            else
            {
                var sep = url.Contains('?') ? '&' : '?';
                var qs = string.Join("&", parameters.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                using var get = new HttpRequestMessage(HttpMethod.Get, url + sep + qs);
                foreach (var (k, v) in headers) get.Headers.TryAddWithoutValidation(k, v);
                resp = await client.SendAsync(get, ct);
            }
        }
        catch (Exception exc) when (exc is HttpRequestException or TaskCanceledException)
        {
            return ([], [$"「{Name}」请求失败：{exc.Message}"]);
        }

        using (resp)
        {
            if ((int)resp.StatusCode >= 400)
                return ([], [$"「{Name}」返回 HTTP {(int)resp.StatusCode}"]);

            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(ct);
                _ = System.Text.Json.JsonDocument.Parse(body);
            }
            catch (Exception)
            {
                return ([], [$"「{Name}」响应不是 JSON"]);
            }

            List<SourceItem> items;
            try
            {
                items = SourceParser.ParseLlmSources(body, Name);
            }
            catch (Exception exc)
            {
                return ([], [$"「{Name}」解析失败：{exc.Message}"]);
            }

            if (items.Count == 0)
                return ([], [$"「{Name}」未返回可用候选"]);

            var warnings = new List<string>();
            if (items.Any(i => i.UrlType == UrlType.Unknown))
                warnings.Add("部分链接类型未能识别，请优先选择磁力或直链。");
            return (items, warnings);
        }
    }
}
