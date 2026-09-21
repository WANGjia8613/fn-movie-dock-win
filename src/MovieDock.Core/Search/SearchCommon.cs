using System.Text.RegularExpressions;

namespace MovieDock.Core.Search;

/// <summary>检索源共用的小工具（自 Python 版 app/search/common.py 移植）。</summary>
public static class SearchCommon
{
    private static readonly Regex CjkRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    /// <summary>字节字节数/字符串 → "12.34 GB"（失败返回空串）。</summary>
    public static string HumanSize(long? bytes)
    {
        var n = (double?)bytes ?? 0;
        return HumanSize(n);
    }

    public static string HumanSize(double? n)
    {
        if (n is null or <= 0) return "";
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        var v = n.Value;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:F2} {units[i]}";
    }

    public static string HumanSize(string? value)
    {
        return double.TryParse(value, out var n) ? HumanSize(n) : "";
    }

    public static int? IntOrNull(string? value) =>
        int.TryParse(value, out var n) ? n : null;

    public static bool ContainsCjk(string? text) =>
        !string.IsNullOrEmpty(text) && CjkRegex.IsMatch(text);

    /// <summary>磁力链接构造：info_hash + dn（名称）+ 常用 tracker。</summary>
    public static string BuildMagnet(string? infoHash, string name = "")
    {
        var h = (infoHash ?? "").Trim();
        if (h.Length == 0) return "";
        var url = $"magnet:?xt=urn:btih:{h}";
        if (name.Length > 0)
            url += $"&dn={Uri.EscapeDataString(name)}";
        foreach (var tr in new[]
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.tracker.cl:1337/announce",
            "udp://tracker.openbittorrent.com:6969/announce",
            "udp://exodus.desync.com:6969/announce",
        })
        {
            url += $"&tr={Uri.EscapeDataString(tr)}";
        }
        return url;
    }

    /// <summary>标题是否与关键词相关：词元命中或整串（去分隔符）命中。</summary>
    public static bool IsRelevant(string query, string? title)
    {
        var low = (title ?? "").ToLowerInvariant();
        if (Ranking.QueryTokens(query).Any(t => low.Contains(t)))
            return true;
        var compact = Ranking.Compact(query);
        return compact.Length >= 4 && Ranking.Compact(title).Contains(compact);
    }

    /// <summary>从 provider.options 里读字符串（兼容 YamlDotNet 的 object 与 System.Text.Json 的 JsonElement）。</summary>
    public static string OptionString(Dictionary<string, object>? options, string key, string fallback = "")
    {
        if (options is null || !options.TryGetValue(key, out var value) || value is null)
            return fallback;
        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString() ?? fallback,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } jn => jn.ToString(),
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.True } => "true",
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.False } => "false",
            _ => value.ToString() ?? fallback,
        };
    }
}
