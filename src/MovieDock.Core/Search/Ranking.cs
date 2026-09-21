using System.Globalization;
using System.Text.RegularExpressions;
using MovieDock.Core.Models;

namespace MovieDock.Core.Search;

/// <summary>候选片源评分与排序（自 Python 版 app/ranking.py 移植）。</summary>
public static class Ranking
{
    private static readonly (Regex Pattern, string Label)[] TagPatterns =
    {
        (new(@"(?<![a-z0-9])(dolby[\s._-]?vision|dovi|\bdv\b)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "DoVi"),
        (new(@"(?<![a-z0-9])(hdr10\+|hdr10plus)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "HDR10+"),
        (new(@"(?<![a-z0-9])(hdr)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "HDR"),
        (new(@"(?<![a-z0-9])(remux)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "REMUX"),
        (new(@"(?<![a-z0-9])(bdremux)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "BDRemux"),
        (new(@"(?<![a-z0-9])(blu-?ray|bdrip)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "BluRay"),
        (new(@"(?<![a-z0-9])(web-?dl)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "WEB-DL"),
        (new(@"(?<![a-z0-9])(webrip)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "WEBRip"),
        (new(@"(?<![a-z0-9])(atmos)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Atmos"),
        (new(@"(?<![a-z0-9])(truehd|dts-hd|dts[\s._-]?hd)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "无损音轨"),
        (new(@"(?<![a-z0-9])(10bit)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "10bit"),
        (new(@"(?<![a-z0-9])(x265|h\.?265|hevc)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "HEVC"),
        (new(@"(?<![a-z0-9])(x264|h\.?264|avc)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "H.264"),
        (new(@"(?<![a-z0-9])(imax)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "IMAX"),
        (new(@"(?<![a-z0-9])(repack|proper)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "修正版"),
        (new(@"(简繁|简英|中英|双语|chs|cht|zh)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "可能带中字"),
    };

    private static readonly (string Key, double Rank)[] ResRank =
    {
        ("2160p", 40.0),
        ("1080p", 22.0),
        ("720p", 10.0),
        ("480p", 2.0),
        ("", 8.0),
    };

    private static readonly Dictionary<string, double> TagBonus = new()
    {
        ["DoVi"] = 6.0,
        ["HDR10+"] = 5.0,
        ["HDR"] = 5.0,
        ["REMUX"] = 4.0,
        ["BDRemux"] = 3.0,
        ["BluRay"] = 2.0,
        ["WEB-DL"] = 1.0,
        ["WEBRip"] = 0.5,
        ["Atmos"] = 1.5,
        ["无损音轨"] = 1.5,
        ["IMAX"] = 1.5,
        ["可能带中字"] = 3.0,
    };

    private static readonly Regex NonKeepRegex = new(@"[^0-9a-z\u4e00-\u9fff]+", RegexOptions.Compiled);
    private static readonly Regex TokenSplitRegex = new(@"[^0-9a-zA-Z\u4e00-\u9fff]+", RegexOptions.Compiled);
    private static readonly Regex SizeNumRegex =
        new(@"([\d.]+)\s*(tb|gb|gib|mb|mib|kb|kib|b)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>整串压缩（去掉分隔符）：wall-e → walle，用于区分 WALL-E 与 Wall Street。</summary>
    public static string Compact(string? text) =>
        NonKeepRegex.Replace((text ?? "").ToLowerInvariant(), "");

    /// <summary>关键词拆词元（≥2 字符）。</summary>
    public static List<string> QueryTokens(string? query)
    {
        var parts = TokenSplitRegex.Split((query ?? "").ToLowerInvariant());
        return parts.Where(p => p.Length >= 2).ToList();
    }

    /// <summary>把 '18.2 GB' / '900 MB' / '1.2TB' 归一成 GB（失败返回 0）。</summary>
    public static double SizeToGb(string? size)
    {
        var m = SizeNumRegex.Match(size ?? "");
        if (!m.Success) return 0.0;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0.0;
        var unit = (m.Groups[2].Success ? m.Groups[2].Value : "gb").ToLowerInvariant();
        var factor = unit switch
        {
            "tb" => 1024.0,
            "gb" => 1.0,
            "gib" => 1.0737,
            "mb" => 1 / 1024.0,
            "mib" => 1 / 1024.0,
            "kb" => 1 / 1024.0 / 1024.0,
            "kib" => 1 / 1024.0 / 1024.0,
            "b" => 1 / 1024.0 / 1024.0 / 1024.0,
            _ => 1.0,
        };
        return value * factor;
    }

    /// <summary>画质/特性标签识别：用于候选打分与界面展示。</summary>
    public static List<string> DetectTags(string? text)
    {
        var low = (text ?? "").ToLowerInvariant();
        var tags = new List<string>();
        foreach (var (pattern, label) in TagPatterns)
        {
            if (tags.Contains(label)) continue;
            if (pattern.IsMatch(low)) tags.Add(label);
        }
        return tags;
    }

    /// <summary>给候选打分：清晰度为主，做种数次之，特性标签/体积做加分与合理性约束。</summary>
    public static double ScoreSource(SourceItem item, string preferResolution = "", string query = "", int? year = null)
    {
        var resolution = item.Resolution;
        if (string.IsNullOrEmpty(resolution)) resolution = item.Quality;
        resolution = (resolution ?? "").ToLowerInvariant();

        double score = 0;
        var matched = false;
        foreach (var (key, rank) in ResRank)
        {
            if (key.Length > 0 && resolution.Contains(key))
            {
                score += rank;
                matched = true;
                break;
            }
        }
        if (!matched) score += 8.0;

        if (!string.IsNullOrEmpty(preferResolution) && resolution.Contains(preferResolution.ToLowerInvariant()))
            score += 10.0;

        if (item.Seeds is int seeds)
        {
            // 做种数按对数给分，封顶 20
            score += Math.Min(20.0, Math.Max(0.0, Math.Sqrt(seeds) * 1.6));
        }
        else
        {
            score += 3.0;
        }

        var tags = item.Tags is { Count: > 0 } ? item.Tags : DetectTags($"{item.Title} {item.Note}");
        foreach (var tag in tags)
            score += TagBonus.GetValueOrDefault(tag, 0.0);

        var gb = SizeToGb(item.Size);
        if (gb != 0)
        {
            // 体积过小（4K/1080p 却只有几 GB）多半是低码率或假种，明显扣分；过大（>90GB）略扣
            if (resolution.StartsWith("2160") && gb < 6) score -= 14.0;
            else if (resolution.StartsWith("2160") && gb < 10) score -= 6.0;
            if (gb > 90) score -= 4.0;
            score += Math.Min(3.0, gb / 40.0);
        }

        if (item.UrlType == UrlType.Magnet) score += 1.5;
        else if (item.UrlType == UrlType.Torrent) score += 1.0;

        // 关键词相关度：索引站常返回「沾边」的结果（搜 wall-e 也会出华尔街之狼）
        var titleLow = (item.Title ?? "").ToLowerInvariant();
        var tokens = QueryTokens(query);
        if (tokens.Count > 0)
        {
            var hits = tokens.Count(t => titleLow.Contains(t));
            var ratio = (double)hits / tokens.Count;
            if (ratio >= 1.0) score += 18.0;
            else if (ratio > 0) score += 12.0 * ratio;
            else score -= 12.0;
        }
        // 整串（去掉分隔符）命中：wall-e → walle，能把 WALL-E 与 Wall Street 区分开
        var compactQuery = Compact(query);
        if (compactQuery.Length >= 4 && Compact(item.Title).Contains(compactQuery))
            score += 25.0;
        if (year is int y && (item.Title ?? "").Contains(y.ToString()))
            score += 3.0;

        return Math.Round(score, 2);
    }

    /// <summary>就地填充标签与评分。</summary>
    public static void AnnotateScores(
        IReadOnlyList<SourceItem> items, string preferResolution = "", string query = "", int? year = null)
    {
        foreach (var it in items)
        {
            if (it.Tags.Count == 0)
                it.Tags = DetectTags($"{it.Title} {it.Note}");
            it.Score = ScoreSource(it, preferResolution, query, year);
        }
    }

    /// <summary>按评分降序排序（会就地填充评分）。</summary>
    public static List<SourceItem> SortByScore(
        IReadOnlyList<SourceItem> items, string preferResolution = "", string query = "", int? year = null)
    {
        AnnotateScores(items, preferResolution, query, year);
        return items.OrderByDescending(i => i.Score).ToList();
    }

    /// <summary>一键最优：取评分最高且带可用链接的候选。</summary>
    public static SourceItem? BestSource(
        IReadOnlyList<SourceItem> items, string preferResolution = "", string query = "", int? year = null)
    {
        var withUrl = items.Where(i => !string.IsNullOrEmpty(i.Url)).ToList();
        var ranked = SortByScore(withUrl, preferResolution, query, year);
        return ranked.Count > 0 ? ranked[0] : null;
    }
}
