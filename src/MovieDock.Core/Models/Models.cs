namespace MovieDock.Core.Models;

/// <summary>检索请求（片名 + 可选年份/清晰度偏好/评分排序开关）。</summary>
public sealed class SearchRequest
{
    public string Query { get; init; } = "";
    public int? Year { get; init; }
    public string? Quality { get; init; }
    /// <summary>是否按评分排序（null = 取配置 search.sort_by_score）。</summary>
    public bool? SortByScore { get; init; }
}

public enum UrlType { Unknown, Magnet, Torrent, Http }

public static class UrlTypes
{
    public static string ToText(this UrlType t) => t switch
    {
        UrlType.Magnet => "magnet",
        UrlType.Torrent => "torrent",
        UrlType.Http => "http",
        _ => "unknown",
    };
}

/// <summary>一条候选片源。</summary>
public sealed class SourceItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Quality { get; init; } = "未知";
    public string Resolution { get; init; } = "";
    public string Size { get; init; } = "";
    public int? Seeds { get; init; }
    public int? Peers { get; init; }
    public string Source { get; init; } = "";
    public string Url { get; init; } = "";
    public UrlType UrlType { get; init; } = UrlType.Unknown;
    public string Note { get; init; } = "";
    /// <summary>画质/特性标签（DoVi/HDR/REMUX…），由 Ranking 检测填充。</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>候选评分（越高越优）。</summary>
    public double Score { get; set; }
    /// <summary>源站原始字段（info_hash 等），仅供排查。</summary>
    public Dictionary<string, object?> Raw { get; init; } = new();
}

/// <summary>检索结果聚合。</summary>
public sealed record SearchResponse(
    string Query,
    IReadOnlyList<SourceItem> Items,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> Warnings,
    string BestId = "");

public sealed record LlmTestResult(bool Ok, string Message, string Model = "", string Detail = "");

/// <summary>单个下载任务的整理选项（覆盖全局配置；null = 沿用配置）。</summary>
public sealed class OrganizeOptions
{
    public string Title { get; set; } = "";
    public int? Year { get; set; }
    public string Quality { get; set; } = "";
    public bool? Enabled { get; set; }
    public string? MovieDirTemplate { get; set; }
    public string? FileNameTemplate { get; set; }
    public string? SeriesDirTemplate { get; set; }
    public string? SeriesFileNameTemplate { get; set; }
    public string? Mode { get; set; }
    public string? LibraryRoot { get; set; }
    public string? UnknownYear { get; set; }
}

internal static class Extensions
{
    public static string Truncate(this string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
