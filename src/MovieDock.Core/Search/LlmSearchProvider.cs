using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;

namespace MovieDock.Core.Search;

public sealed class LlmSearchProvider(LlmClient client, ProviderConfig? pc = null) : ISearchProvider
{
    public string Name { get; } = pc is { Name: not "" } ? pc.Name : "大模型检索";

    public async Task<(IReadOnlyList<SourceItem>, IReadOnlyList<string>)> SearchAsync(
        SearchRequest req, CancellationToken ct = default)
    {
        var (items, warnings) = await LlmSourceFinder.FindSourcesAsync(client, req.Query, req.Year, req.Quality, ct);
        return (items, warnings);
    }
}

public static class LlmSourceFinder
{
    /// <summary>用用户配置的大模型整理/检索候选片源。模型可能幻觉出不可用链接，结果仅作候选。</summary>
    public static async Task<(List<SourceItem> Items, List<string> Warnings)> FindSourcesAsync(
        LlmClient client, string query, int? year = null, string? quality = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (string.IsNullOrEmpty(client.Config.ApiKey))
            return ([], ["未配置大模型 API Key，已跳过「大模型检索」"]);

        var hint = year is int y ? $"{query} {y}" : query;
        if (!string.IsNullOrEmpty(quality)) hint += $" 希望{quality}";
        const string system =
            "你是影视资源检索助手。用户会提供电影/剧集名称。"
            + "请尽量给出不同清晰度的可下载片源候选（磁力链接、种子或直链）。"
            + "只输出 JSON 数组，不要其它说明。每项字段："
            + "title, url, quality, resolution, size, seeds, note。"
            + "若无法确认真实链接，url 可为空字符串；不要编造无法使用的 btih。"
            + "优先输出你较有把握、格式完整的 magnet 或 http 链接。";
        var user = $"检索：{hint}\n请返回 JSON 数组。";

        string content;
        try
        {
            content = await client.ChatAsync(new[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = system },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = user },
            }, 0.3, ct);
        }
        catch (LlmException exc)
        {
            return ([], [exc.Message]);
        }

        List<SourceItem> items;
        try
        {
            items = SourceParser.ParseLlmSources(content);
        }
        catch (Exception exc)
        {
            return ([], [$"解析大模型结果失败：{exc.Message}"]);
        }

        if (items.Count == 0)
            warnings.Add("大模型已调用，但未解析出可用候选链接。可配置「自定义索引 API」，或在下载页手动粘贴磁力/直链。");
        else
            warnings.Add("大模型结果仅作候选，链接有效性以实际下载为准。");
        return (items, warnings);
    }
}
