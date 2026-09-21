using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MovieDock.Core.Models;

namespace MovieDock.Core.Llm;

/// <summary>大模型返回内容的解析：JSON 候选 / 正则兜底提取磁力与链接 / 清晰度识别。</summary>
public static class SourceParser
{
    private static readonly Regex MagnetRegex =
        new(@"magnet:\?xt=urn:btih:[A-Za-z0-9]{32,40}[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex =
        new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FenceRegex =
        new(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.Compiled);

    // 使用边界，避免 HDR/UHD/Blu-ray 等误判为 720p
    private static readonly (Regex Pattern, string Resolution, string Quality)[] QualityPatterns =
    {
        (new(@"(?<![a-z0-9])(2160p|4k|uhd)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "2160p", "4K"),
        (new(@"(?<![a-z0-9])(1080p|fhd)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "1080p", "1080p"),
        (new(@"(?<![a-z0-9])(720p)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "720p", "720p"),
        (new(@"(?<![a-z0-9])(480p)(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled), "480p", "480p"),
    };

    public static (string Resolution, string Quality) DetectQuality(string? text)
    {
        var low = (text ?? "").ToLowerInvariant();
        foreach (var (pattern, resolution, quality) in QualityPatterns)
            if (pattern.IsMatch(low))
                return (resolution, quality);
        return ("", "未知");
    }

    public static string StableId(string prefix, string url)
    {
        var digest = Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(url ?? "")))
            .ToLowerInvariant();
        return $"{prefix}-{digest[..10]}";
    }

    public static UrlType ClassifyUrl(string? url)
    {
        var low = (url ?? "").Trim().ToLowerInvariant();
        if (low.StartsWith("magnet:")) return UrlType.Magnet;
        var path = low;
        if (low.StartsWith("http"))
        {
            try { path = new Uri(low).AbsolutePath; }
            catch (UriFormatException) { }
        }
        if (path.EndsWith(".torrent") || low.Contains(".torrent?")) return UrlType.Torrent;
        if (low.StartsWith("http://") || low.StartsWith("https://")) return UrlType.Http;
        return UrlType.Unknown;
    }

    internal static JsonDocument? TryParseJsonBlock(string text)
    {
        var t = (text ?? "").Trim();
        var fence = FenceRegex.Match(t);
        if (fence.Success) t = fence.Groups[1].Value.Trim();
        foreach (var candidate in new[] { t, Slice(t, '[', ']'), Slice(t, '{', '}') })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            try { return JsonDocument.Parse(candidate); }
            catch (JsonException) { }
        }
        return null;
    }

    private static string? Slice(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        var end = text.LastIndexOf(close);
        return start != -1 && end > start ? text[start..(end + 1)] : null;
    }

    public static List<SourceItem> ParseLlmSources(string? content, string providerName = "大模型检索")
    {
        content ??= "";
        var doc = TryParseJsonBlock(content);

        List<JsonElement> rawItems = new();
        if (doc != null)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "items", "sources", "results" })
                    if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
                    {
                        rawItems = v.EnumerateArray().ToList();
                        break;
                    }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                rawItems = root.EnumerateArray().ToList();
            }
        }

        var results = new List<SourceItem>();
        var seen = new HashSet<string>();
        var idx = 0;
        foreach (var raw in rawItems)
        {
            idx++;
            if (raw.ValueKind != JsonValueKind.Object) continue;
            var url = FirstString(raw, "url", "magnet", "link").Trim();
            var title = FirstString(raw, "title", "name").Trim();
            if (title.Length == 0)
                title = url.Length > 0 ? url : $"结果{idx}";
            if (url.Length == 0) continue;
            if (!seen.Add(url)) continue;

            var urlType = ClassifyUrl(url);
            if (urlType == UrlType.Unknown)
                urlType = url.ToLowerInvariant().StartsWith("http") ? UrlType.Http : UrlType.Unknown;

            var (resolution, quality) = DetectQuality($"{title} {url} {FirstString(raw, "quality")}");
            var rawQuality = FirstString(raw, "quality");
            if (rawQuality.Length > 0) quality = rawQuality;
            var rawResolution = FirstString(raw, "resolution");
            if (rawResolution.Length > 0) resolution = rawResolution;

            results.Add(new SourceItem
            {
                Id = StableId("llm", url),
                Title = title.Truncate(200),
                Quality = quality,
                Resolution = resolution,
                Size = FirstString(raw, "size"),
                Seeds = TryInt(raw, "seeds"),
                Peers = TryInt(raw, "peers"),
                Source = providerName,
                Url = url,
                UrlType = urlType,
                Note = FirstString(raw, "note", "comment"),
            });
        }

        if (results.Count == 0)
            results.AddRange(ScanRawLinks(content, providerName, seen));
        return results;
    }

    private static List<SourceItem> ScanRawLinks(string content, string providerName, HashSet<string> seen)
    {
        var results = new List<SourceItem>();
        foreach (Match m in MagnetRegex.Matches(content))
        {
            var url = m.Value.Trim();
            if (url.Length == 0 || !seen.Add(url)) continue;
            var (resolution, quality) = DetectQuality(url);
            results.Add(new SourceItem
            {
                Id = StableId("llm-raw", url),
                Title = $"模型提及的磁力链接 {results.Count + 1}",
                Quality = quality,
                Resolution = resolution,
                Source = providerName,
                Url = url,
                UrlType = UrlType.Magnet,
                Note = "从大模型回复中提取，请自行确认是否可用",
            });
        }
        foreach (Match m in UrlRegex.Matches(content))
        {
            var url = m.Value.TrimEnd(')', '.', ',', ';', ']');
            var low = url.ToLowerInvariant();
            if (url.Length == 0 || seen.Contains(url)) continue;
            if (low.Contains("api.") || low.Contains("openai") || low.Contains("localhost") || low.Contains("127.0.0.1"))
                continue;
            if (!(low.Contains(".torrent") || low.Contains("magnet") || low.Contains("download")))
                continue;
            seen.Add(url);
            var (resolution, quality) = DetectQuality(url);
            results.Add(new SourceItem
            {
                Id = StableId("llm-url", url),
                Title = $"模型提及的链接 {results.Count + 1}",
                Quality = quality,
                Resolution = resolution,
                Source = providerName,
                Url = url,
                UrlType = low.EndsWith(".torrent") ? UrlType.Torrent : UrlType.Http,
                Note = "从大模型回复中提取，请自行确认是否可用",
            });
        }
        return results;
    }

    private static string FirstString(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (obj.TryGetProperty(key, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
                if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
            }
        return "";
    }

    private static int? TryInt(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return null;
    }
}
