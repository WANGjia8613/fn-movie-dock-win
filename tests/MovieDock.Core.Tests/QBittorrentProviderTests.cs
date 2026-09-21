using System.Text.Json;
using MovieDock.Core.Config;
using MovieDock.Core.Models;
using MovieDock.Core.Search;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 4 节（qBittorrent 结果映射）。</summary>
public class QBittorrentProviderTests
{
    private static SourceItem MapSample()
    {
        var pc = new ProviderConfig
        {
            Type = "qbittorrent", Enabled = true, Name = "qB", Url = "http://127.0.0.1:8085",
            Options = new Dictionary<string, object> { ["username"] = "admin", ["password"] = "x", ["plugins"] = "yts,bt4g" },
        };
        var qbt = new QBittorrentProvider(pc);
        using var doc = JsonDocument.Parse("""
            {
              "fileName": "沙丘2.Dune.Part.Two.2024.2160p.BluRay.REMUX.DoVi.HDR.mkv",
              "fileUrl": "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567",
              "fileSize": 12884901888,
              "nbSeeders": 56,
              "nbLeechers": 8,
              "siteUrl": "bt4g",
              "pubDate": "2024-05-01T00:00:00Z"
            }
            """);
        return qbt.ToItem(doc.RootElement, 0);
    }

    [Fact]
    public void Qbt_MapQuality()
    {
        var item = MapSample();
        Assert.True(item.Quality == "4K" && item.Resolution == "2160p", $"{item.Resolution}/{item.Quality}");
    }

    [Fact]
    public void Qbt_MapSizeSeeds()
    {
        var item = MapSample();
        Assert.True(item.Size.StartsWith("12.00 GB") && item.Seeds == 56, $"{item.Size}/{item.Seeds}");
    }

    [Fact]
    public void Qbt_MapType()
    {
        var item = MapSample();
        Assert.Equal(UrlType.Magnet, item.UrlType);
    }
}
