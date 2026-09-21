using MovieDock.Core.Llm;
using MovieDock.Core.Models;
using MovieDock.Core.Organize;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 1 节（剧集识别）与 review_checks 第 2/3/4 节（清晰度 / LLM 解析 / 年份）。</summary>
public class NameParsingTests
{
    [Fact]
    public void ParseEpisode_Cases()
    {
        var cases = new Dictionary<string, (int?, int?)>
        {
            ["Show.S01E02.1080p.WEB-DL"] = (1, 2),
            ["show.s1e2"] = (1, 2),
            ["Show.1x03.720p"] = (1, 3),
            ["剧集名称.第5集.HD"] = (1, 5),
            ["Show.E07.2160p"] = (1, 7),
            ["WALL-E.2008.2160p.UHD.BDRemux.HDR.DoVi.P8.Hybrid.by.DVT"] = (null, null),
        };
        var bad = new List<string>();
        foreach (var (text, expect) in cases)
        {
            var got = NameParsing.ParseEpisode(text);
            if (got != expect) bad.Add($"{text} -> {got} (期望 {expect})");
        }
        Assert.True(bad.Count == 0, string.Join("; ", bad));
    }

    [Fact]
    public void EpisodeLabel_Basic()
    {
        Assert.Equal("S01E02", NameParsing.EpisodeLabel(1, 2));
        Assert.Equal("", NameParsing.EpisodeLabel(null, null));
    }

    [Fact]
    public void DetectQuality_HdrNotMisjudged()
    {
        // HDR 不应误判为 720p
        var (r, q) = SourceParser.DetectQuality("Movie.Name.2024.HDR.2160p.UHD.BluRay");
        Assert.True(q is "4K" or "2160p" || r == "2160p", $"{r}/{q}");
    }

    [Fact]
    public void DetectQuality_HdrUnknown()
    {
        var (r2, q2) = SourceParser.DetectQuality("Something.HDR.x265");
        Assert.True(r2 == "" && q2 == "未知", $"{r2}/{q2}");
    }

    [Fact]
    public void ParseLlmSources_NonJsonMagnetFallback()
    {
        var items = SourceParser.ParseLlmSources("没有 JSON，但有磁力 magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567");
        Assert.Contains(items, i => i.UrlType == UrlType.Magnet);
    }

    [Fact]
    public void ParseLlmSources_JsonBlockOk()
    {
        var items = SourceParser.ParseLlmSources(
            "```json\n[{\"title\":\"A 1080p\",\"url\":\"magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"quality\":\"1080p\",\"seeds\":\"12\"}]\n```");
        Assert.True(items.Count == 1 && items[0].Seeds == 12, items.Count > 0 ? items[0].Title : "empty");
    }

    [Fact]
    public void ParseTitleYear_Basic()
    {
        var (t, y) = NameParsing.ParseTitleYear("沙丘2 (2024) 1080p BluRay");
        Assert.True(t == "沙丘2" && y == 2024, $"{t}/{y}");
    }

    [Fact]
    public void ParseTitleYear_Edge()
    {
        // 片名自带数字（2049）可以被识别为年份，也可以识别不出 —— 两种都接受（对齐 Python 用例）
        var (_, y2) = NameParsing.ParseTitleYear("银翼杀手2049");
        Assert.True(y2 is 2049 or null, $"{y2}");
    }
}
