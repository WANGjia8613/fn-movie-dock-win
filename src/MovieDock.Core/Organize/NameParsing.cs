using System.Text.RegularExpressions;

namespace MovieDock.Core.Organize;

/// <summary>
/// 片名/年份/剧集集号解析与整理模板填充（自 Python 版 app/organizer/organize.py 移植）。
/// </summary>
public static partial class NameParsing
{
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidCharsRegex();

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex YearBracketRegex = new(@"[(\[]((?:19|20)\d{2})[)\]]", RegexOptions.Compiled);
    private static readonly Regex YearStandaloneRegex = new(@"(?<![0-9])((?:19|20)\d{2})(?![0-9])", RegexOptions.Compiled);

    // 压制/画质/音轨类标记：出现位置之后的内容基本是发布信息，不是片名
    private const string ReleaseTokenPattern =
        @"(?:2160p|1080p|720p|480p|4k|uhd|bluray|blu-?ray|bdremux|remux|web-?dl|webdl|webrip|hdtv|hdrip|" +
        @"brrip|dvdrip|x264|x265|h264|h265|hevc|avc|av1|10bit|8bit|aac|ac3|ddp|dd5|dts(?:-hd)?|truehd|" +
        @"atmos|hdr10\+?|hdr|dovi|repack|proper|internal|multi|dual|remastered|imax|extended)";

    private static readonly Regex ReleaseTokenRegex =
        new(@"\b" + ReleaseTokenPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 「1.20GB」这类体积标记（磁力 dn= 里很常见）
    private static readonly Regex SizeMarkerRegex =
        new(@"\d+(?:\.\d+)?\s?(?:GB|MB|KB)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 发布名常用点号分词：Spider-Man.No.Way.Home -> Spider-Man No Way Home
    private static readonly Regex DotWordRegex = new(@"(?<=[A-Za-z0-9])\.(?=[A-Za-z0-9])", RegexOptions.Compiled);

    // 只收拾「分隔用」的短横（两侧带空格 / 末尾），别动 WALL-E、Spider-Man 这类词内连字符
    private static readonly Regex TrailingHyphenRegex = new(@"(?:\s+-\s*)+$", RegexOptions.Compiled);
    private static readonly Regex HyphenRegex = new(@"(?:\s+-\s*)+", RegexOptions.Compiled);

    // 剧集集号识别：S01E02 / s1e2 / 1x02 / 第2集 / EP02 / E02
    private static readonly (Regex Pattern, bool TwoGroups)[] EpisodePatterns =
    {
        (new(@"[Ss](\d{1,2})[\s._-]?[Ee][Pp]?(\d{1,3})", RegexOptions.Compiled), true),
        (new(@"(?<![0-9])(\d{1,2})[xX](\d{1,3})(?![0-9])", RegexOptions.Compiled), true),
        (new(@"第\s*(\d{1,3})\s*[集话話]", RegexOptions.Compiled), false),
        (new(@"(?<![A-Za-z0-9])[Ee][Pp]?(\d{1,3})(?![0-9])", RegexOptions.Compiled), false),
    };

    public static string SanitizeName(string? name, string fallback = "未知影片")
    {
        var n = (name ?? "").Trim();
        n = InvalidCharsRegex().Replace(n, "");
        n = WhitespaceRegex.Replace(n, " ").Trim(' ', '.');
        return n.Length == 0 ? fallback : n;
    }

    /// <summary>模板里可能含 "/"（如 {title} ({year})/Season {season}），逐段清理保留层级。</summary>
    public static string SanitizePath(string? name, string fallback = "未知影片")
    {
        var raw = (name ?? "").Replace("\\", "/");
        if (!raw.Contains('/')) return SanitizeName(raw, fallback);
        var segments = new List<string>();
        foreach (var seg in raw.Split('/'))
        {
            if (seg.Trim().Length == 0) continue;
            segments.Add(SanitizeName(seg, fallback));
        }
        var joined = string.Join("/", segments);
        return joined.Length == 0 ? fallback : joined;
    }

    /// <summary>从片名/磁力 dn 字段中拆出（清洗后的片名, 年份）。</summary>
    public static (string Title, int? Year) ParseTitleYear(string? query)
    {
        var text = (query ?? "").Trim();
        int? year = null;
        // 优先匹配括号内年份，降低片名自带数字的误伤
        var m = YearBracketRegex.Match(text);
        if (!m.Success) m = YearStandaloneRegex.Match(text);
        if (m.Success)
        {
            year = int.Parse(m.Groups[1].Value);
            text = (text[..m.Index] + text[(m.Index + m.Length)..]).Trim();
        }
        text = DotWordRegex.Replace(text, " ");
        // 遇到第一个压制/画质标记就截断 —— 比逐个删除更能得到干净的片名
        var hit = ReleaseTokenRegex.Match(text);
        if (hit.Success && hit.Index > 0)
            text = text[..hit.Index];
        text = SizeMarkerRegex.Replace(text, "");
        text = TrailingHyphenRegex.Replace(text, "");
        text = HyphenRegex.Replace(text, " ");
        text = WhitespaceRegex.Replace(text, " ").Trim(' ', '-', '_', '·', '.');
        return (SanitizeName(text), year);
    }

    /// <summary>从文件名/标题里解析 (季, 集)。识别不到返回 (null, null)。</summary>
    public static (int? Season, int? Episode) ParseEpisode(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return (null, null);

        for (var idx = 0; idx < EpisodePatterns.Length; idx++)
        {
            var (pattern, twoGroups) = EpisodePatterns[idx];
            var m = pattern.Match(raw);
            if (!m.Success) continue;

            int season, episode;
            try
            {
                if (twoGroups)
                {
                    season = int.Parse(m.Groups[1].Value);
                    episode = int.Parse(m.Groups[2].Value);
                }
                else
                {
                    // 只有集号：第2集 / E02 → 季未知按 1
                    season = 1;
                    episode = int.Parse(m.Groups[1].Value);
                }
            }
            catch (Exception exc) when (exc is FormatException or OverflowException)
            {
                continue;
            }
            if (idx == 2) season = season is 0 or 1 ? 1 : season;
            return (season, episode);
        }
        return (null, null);
    }

    /// <summary>集号展示标签：S01E02；无季集信息返回空串。</summary>
    public static string EpisodeLabel(int? season, int? episode)
    {
        if (season is null && episode is null) return "";
        var s = season ?? 1;
        var e = episode ?? 0;
        return $"S{s:00}E{e:00}";
    }

    /// <summary>
    /// 按模板生成目录名 / 文件名。
    /// 占位符：{title} {year} {quality} {resolution} {season} {episode} {season_raw} {episode_raw} {ext}。
    /// </summary>
    public static string ApplyTemplate(
        string? template, string title, int? year,
        string quality = "", string ext = "",
        int? season = null, int? episode = null,
        string unknownYear = "未知年份")
    {
        var safeQuality = string.IsNullOrEmpty(quality) ? "未知" : quality;
        var mapping = new Dictionary<string, string>
        {
            ["title"] = SanitizeName(title),
            ["year"] = year is int y ? y.ToString() : unknownYear,
            ["quality"] = SanitizeName(safeQuality, "未知"),
            ["resolution"] = SanitizeName(safeQuality, "未知"),
            ["season"] = season is int s ? $"{s:00}" : "",
            ["episode"] = episode is int e ? $"{e:00}" : "",
            ["season_raw"] = season is int s2 ? s2.ToString() : "",
            ["episode_raw"] = episode is int e2 ? e2.ToString() : "",
            ["ext"] = (ext ?? "").TrimStart('.'),
        };
        var output = template ?? "";
        foreach (var (key, value) in mapping)
            output = output.Replace("{" + key + "}", value);
        // 剧集模板 S{season}E{episode} 在无集号时残留的 "SE" 去掉
        output = output.Replace("S/E", "").Replace("SE", "");
        // 文件名模板可能含扩展名，sanitize 不应去掉后缀里的点
        if (!string.IsNullOrEmpty(ext))
        {
            var suffix = ext.StartsWith('.') ? ext : "." + ext;
            if (output.ToLowerInvariant().EndsWith(suffix.ToLowerInvariant()))
            {
                var stem = output[..^suffix.Length];
                return SanitizePath(stem, "未命名") + suffix;
            }
        }
        return SanitizePath(output);
    }
}
