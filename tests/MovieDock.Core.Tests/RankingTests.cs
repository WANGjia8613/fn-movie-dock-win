using MovieDock.Core.Models;
using MovieDock.Core.Search;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 3 节（候选评分与排序）。</summary>
public class RankingTests
{
    private static SourceItem WallE4k() => new()
    {
        Id = "a",
        Title = "WALL-E.2008.2160p.UHD.BDRemux.HDR.DoVi.P8.Hybrid.by.DVT",
        Quality = "2160p", Resolution = "2160p", Size = "38.2 GB", Seeds = 19,
        Url = "magnet:?xt=urn:btih:aaa", UrlType = UrlType.Magnet,
    };

    private static SourceItem WallE1080() => new()
    {
        Id = "b",
        Title = "WALL-E.2008.1080p.BluRay.x264.TrueHD.7.1.Atmos-SWTYBLZ",
        Quality = "1080p", Resolution = "1080p", Size = "12 GB", Seeds = 180,
        Url = "magnet:?xt=urn:btih:bbb", UrlType = UrlType.Magnet,
    };

    private static SourceItem Fake4k() => new()
    {
        Id = "c",
        Title = "WALL-E.2008.2160p.WEB-DL.x265",
        Quality = "2160p", Resolution = "2160p", Size = "1.2 GB", Seeds = 3,
        Url = "magnet:?xt=urn:btih:ccc", UrlType = UrlType.Magnet,
    };

    private static List<SourceItem> Ranked() =>
        Ranking.SortByScore(new List<SourceItem> { WallE1080(), Fake4k(), WallE4k() }, preferResolution: "2160p");

    [Fact]
    public void Ranking_TopIs4kRemux()
    {
        var ranked = Ranked();
        Assert.True(ranked[0].Id == "a", string.Join(", ", ranked.Select(i => $"{i.Id}:{i.Score}")));
    }

    [Fact]
    public void Ranking_PenalizesTiny4k()
    {
        var ranked = Ranked();
        Assert.True(ranked[^1].Id == "c", string.Join(", ", ranked.Select(i => $"{i.Id}:{i.Score}")));
    }

    [Fact]
    public void Ranking_BestSource()
    {
        var ranked = Ranked();
        var best = Ranking.BestSource(ranked) ?? ranked[0];
        Assert.Equal("a", best.Id);
    }

    [Fact]
    public void DetectTags_Flags()
    {
        var tags = Ranking.DetectTags("WALL-E.2008.2160p.UHD.BDRemux.HDR.DoVi.P8.Hybrid.by.DVT");
        Assert.True(tags.Contains("DoVi") && tags.Contains("HDR") && tags.Contains("BDRemux"), string.Join(",", tags));
    }
}
