using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;

namespace MovieDock.Core.Search;

public static class SearchProviders
{
    public static List<ISearchProvider> Load(AppConfig cfg)
    {
        var client = new LlmClient(cfg.Llm);
        var proxy = (cfg.Network.Proxy ?? "").Trim();
        var timeout = cfg.Network.TimeoutSeconds > 0 ? cfg.Network.TimeoutSeconds : 20;
        var providers = new List<ISearchProvider>();
        foreach (var pc in cfg.Search.Providers)
        {
            if (!pc.Enabled) continue;
            switch (pc.Type)
            {
                case "builtin":
                    providers.Add(new BuiltinProvider(pc, proxy, timeout, client));
                    break;
                case "demo":
                    providers.Add(new DemoProvider(pc));
                    break;
                case "llm":
                    providers.Add(new LlmSearchProvider(client, pc));
                    break;
                case "qbittorrent":
                    providers.Add(new QBittorrentProvider(pc));
                    break;
                case "custom_api":
                    providers.Add(new CustomApiProvider(pc));
                    break;
            }
        }
        return providers;
    }

    public static List<string> Labels(AppConfig cfg) =>
        cfg.Search.Providers.Where(p => p.Enabled).Select(p => string.IsNullOrEmpty(p.Name) ? p.Type : p.Name).ToList();

    /// <summary>设置页用的类型中文名。</summary>
    public static readonly Dictionary<string, string> TypeLabels = new()
    {
        ["builtin"] = "内置索引（推荐）",
        ["demo"] = "演示数据",
        ["llm"] = "大模型检索",
        ["qbittorrent"] = "qBittorrent 搜索",
        ["custom_api"] = "自定义索引",
    };
}

/// <summary>
/// 聚合检索：收集各源结果、按 URL 去重、清晰度过滤（无匹配时展示全部）、
/// 评分标注/排序 + 最优候选标记。自 Python 版 routes.py /search 端点移植。
/// </summary>
public sealed class SearchService
{
    private readonly Func<List<ISearchProvider>> _providers;
    private readonly AppConfig _cfg;

    public SearchService(AppConfig cfg) : this(() => SearchProviders.Load(cfg), cfg) { }

    public SearchService(Func<List<ISearchProvider>> providers, AppConfig? cfg = null)
    {
        _providers = providers;
        _cfg = cfg ?? new AppConfig();
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest req, CancellationToken ct = default)
    {
        var providers = _providers();
        if (providers.Count == 0)
            return new SearchResponse(req.Query, [], [],
                ["没有启用任何检索源，请在「设置」中打开内置索引、大模型检索或自定义索引。"]);

        var items = new List<SourceItem>();
        var warnings = new List<string>();
        var labels = new List<string>();
        foreach (var p in providers)
        {
            labels.Add(p.Name);
            try
            {
                var (found, warns) = await p.SearchAsync(req, ct);
                items.AddRange(found);
                warnings.AddRange(warns);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 用户取消检索时立即中断，不转成警告继续下一个源
            }
            catch (Exception exc)
            {
                warnings.Add($"「{p.Name}」出错：{exc.Message}");
            }
        }

        var seen = new HashSet<string>();
        var unique = new List<SourceItem>();
        foreach (var it in items)
        {
            var key = (it.Url ?? "").Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;
            unique.Add(it);
        }

        if (!string.IsNullOrEmpty(req.Quality))
        {
            var q = req.Quality.ToLowerInvariant();
            var filtered = unique.Where(it =>
                (it.Quality ?? "").ToLowerInvariant().Contains(q) ||
                (it.Resolution ?? "").ToLowerInvariant().Contains(q)).ToList();
            if (filtered.Count > 0) unique = filtered;
            else warnings.Add($"没有匹配「{req.Quality}」的结果，已展示全部候选。");
        }

        // 评分排序（HDR/DoVi/做种数/体积等）：请求未指定时取配置 search.sort_by_score
        var sortWanted = req.SortByScore ?? _cfg.Search.SortByScore;
        if (sortWanted)
            unique = Ranking.SortByScore(unique, req.Quality ?? "", req.Query, req.Year);
        else
            Ranking.AnnotateScores(unique, req.Quality ?? "", req.Query, req.Year);

        var best = unique.Count > 0 ? Ranking.BestSource(unique, req.Quality ?? "", req.Query, req.Year) : null;
        return new SearchResponse(req.Query, unique, labels, warnings, best?.Id ?? "");
    }
}
