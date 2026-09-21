using System.Security.Cryptography;
using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;

namespace MovieDock.Core.Search;

/// <summary>内置演示数据，便于不连外网时验证界面与下载链路。</summary>
public sealed class DemoProvider(ProviderConfig? pc = null) : ISearchProvider
{
    public string Name { get; } = pc is { Name: not "" } ? pc.Name : "演示数据";

    public Task<(IReadOnlyList<SourceItem>, IReadOnlyList<string>)> SearchAsync(SearchRequest req, CancellationToken ct = default)
    {
        var query = req.Query.Trim();
        var year = req.Year ?? 2024;
        var digest = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(query))).ToLowerInvariant()[..8];
        var pad1 = new string('0', 40 - digest.Length);
        var pad2 = new string('1', 40 - digest.Length);

        var samples = new List<(string Title, string Quality, string Resolution, string Size, int Seeds, int Peers, UrlType Type, string Url, string Note)>
        {
            ($"{query} ({year}) 2160p UHD Demo", "2160p", "2160p", "18.2 GB", 42, 12, UrlType.Magnet,
                $"magnet:?xt=urn:btih:{digest}{pad1}&dn={query}-2160p-demo",
                "演示磁力，仅用于验证检索列表 UI；真实下载请配置检索源或粘贴有效链接"),
            ($"{query} ({year}) 1080p BluRay Demo", "1080p", "1080p", "8.4 GB", 88, 30, UrlType.Magnet,
                $"magnet:?xt=urn:btih:{digest}{pad2}&dn={query}-1080p-demo",
                "演示数据"),
            ($"{query} ({year}) 720p WEB-DL Demo", "720p", "720p", "3.1 GB", 25, 8, UrlType.Http,
                "https://example.com/demo/sample-720p.mkv",
                "演示直链，example.com 不可真实下载"),
        };

        if (!string.IsNullOrEmpty(req.Quality))
        {
            var q = req.Quality.ToLowerInvariant();
            var filtered = samples.Where(s =>
                s.Quality.ToLowerInvariant().Contains(q) || s.Resolution.ToLowerInvariant().Contains(q)).ToList();
            if (filtered.Count > 0) samples = filtered;
        }

        var items = samples.Select((s, i) => new SourceItem
        {
            Id = $"demo-{i}-{digest}",
            Title = s.Title,
            Quality = s.Quality,
            Resolution = s.Resolution,
            Size = s.Size,
            Seeds = s.Seeds,
            Peers = s.Peers,
            Source = Name,
            Url = s.Url,
            UrlType = s.Type,
            Note = s.Note,
        }).ToList();

        IReadOnlyList<string> warnings = new[] { "当前包含「演示数据」结果，用于验证界面流程。" };
        return Task.FromResult<(IReadOnlyList<SourceItem>, IReadOnlyList<string>)>((items, warnings));
    }
}
