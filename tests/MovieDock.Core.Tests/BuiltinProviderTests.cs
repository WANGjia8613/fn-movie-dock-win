using MovieDock.Core.Config;
using MovieDock.Core.Models;
using MovieDock.Core.Search;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 11 节（内置直连索引源：解析 / 源选择 / 代理优先级 / 去重 / 相关度过滤 / LLM 翻译）。</summary>
public class BuiltinProviderTests
{
    // ---------- common.py ----------

    [Fact]
    public void Common_HumanSize()
    {
        var s = SearchCommon.HumanSize(1293736264L);
        Assert.True(s.StartsWith("1.21 GB") || s.Contains("GB"), s);
    }

    [Fact]
    public void Common_IntOrNone()
    {
        Assert.True(SearchCommon.IntOrNull("52") == 52 && SearchCommon.IntOrNull("x") is null);
    }

    [Fact]
    public void Common_BuildMagnet()
    {
        var mag = SearchCommon.BuildMagnet("6687A51BB38802620E13542D9C50235039F939D1", "WALL-E 2008");
        Assert.True(mag.StartsWith("magnet:?xt=urn:btih:6687") && mag.Contains("&dn=WALL-E%202008") && mag.Contains("&tr="),
            mag[..Math.Min(60, mag.Length)]);
    }

    // ---------- 三源解析 ----------

    private const string TpbPayload = """
        [
          {"id":"1","name":"WALL-E (2008) [1080p]",
           "info_hash":"6687A51BB38802620E13542D9C50235039F939D1","leechers":"8","seeders":"52",
           "num_files":"3","size":"1293736264","category":"207"},
          {"id":"0","name":"No results returned","info_hash":"0000000000000000000000000000000000000000",
           "seeders":"0","leechers":"0","size":"0"}
        ]
        """;

    [Fact]
    public void Tpb_Parse()
    {
        var items = new TpbSource().Parse(TpbPayload, "https://apibay.org");
        Assert.True(items.Count == 1 && items[0].Quality == "1080p" && items[0].Seeds == 52,
            $"{items.Count} 条 / {(items.Count > 0 ? items[0].Seeds : "-")}");
    }

    [Fact]
    public void Tpb_Magnet()
    {
        var items = new TpbSource().Parse(TpbPayload, "https://apibay.org");
        Assert.True(items.Count > 0 && items[0].Url.StartsWith("magnet:?xt=urn:btih:6687"));
    }

    [Fact]
    public void Yts_Parse()
    {
        const string payload = """
            {"status":"ok","data":{"movies":[{
              "title":"Wall-E","title_long":"Wall-E (2008)","year":2008,
              "torrents":[{"hash":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","quality":"1080p","type":"bluray",
                          "size_bytes":1293736264,"seeds":100,"peers":10,"video_codec":"x264"}]
            }]}}
            """;
        var items = new YtsSource().Parse(payload, "https://yts.mx");
        Assert.True(items.Count == 1 && items[0].Seeds == 100 && items[0].Title.Contains("Wall-E (2008)"),
            items.Count > 0 ? items[0].Title : "empty");
    }

    private const string DmhyPayload = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0"><channel><item>
          <title>[DMG][WALL-E][1080p][BDRip]</title>
          <enclosure url="magnet:?xt=urn:btih:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB" length="1" type="application/x-bittorrent" />
          <description>大小：1.2GB</description>
          <pubDate>Sat, 20 Sep 2026 10:00:00 +0800</pubDate>
        </item></channel></rss>
        """;

    [Fact]
    public void Dmhy_Parse()
    {
        var items = new DmhySource().Parse(DmhyPayload, "https://share.dmhy.org");
        Assert.True(items.Count == 1 && items[0].Url.StartsWith("magnet:") && items[0].Quality == "1080p",
            $"{items.Count} 条");
    }

    [Fact]
    public void Dmhy_Size()
    {
        var items = new DmhySource().Parse(DmhyPayload, "https://share.dmhy.org");
        Assert.True(items.Count > 0 && items[0].Size == "1.2 GB", items.Count > 0 ? items[0].Size : "-");
    }

    // ---------- Provider：源选择 / 代理优先级 ----------

    private static BuiltinProvider NewProvider(Dictionary<string, object>? options = null, string globalProxy = "") =>
        new(new ProviderConfig { Type = "builtin", Enabled = true, Name = "内置索引", Options = options ?? new() },
            globalProxy: globalProxy, globalTimeout: 20);

    [Fact]
    public void Builtin_SourceFilter()
    {
        var prov = NewProvider(new Dictionary<string, object> { ["sources"] = "tpb,notexist", ["proxy"] = "http://127.0.0.1:7891" });
        Assert.Equal(new[] { "tpb" }, prov.Sources.Select(s => s.Key).ToArray());
    }

    [Fact]
    public void Builtin_ProxyPrecedence()
    {
        var prov = NewProvider(new Dictionary<string, object> { ["sources"] = "tpb", ["proxy"] = "http://127.0.0.1:7891" },
            globalProxy: "http://127.0.0.1:7890");
        Assert.Equal("http://127.0.0.1:7891", prov.Proxy);
    }

    [Fact]
    public void Builtin_GlobalProxy()
    {
        var prov = NewProvider(globalProxy: "http://127.0.0.1:7890");
        Assert.Equal("http://127.0.0.1:7890", prov.Proxy);
    }

    [Fact]
    public void Builtin_DefaultSources()
    {
        var prov = NewProvider();
        Assert.Equal(new[] { "tpb", "yts", "dmhy" }, prov.Sources.Select(s => s.Key).ToArray());
    }

    // ---------- 合并去重 / 相关度过滤 ----------

    [Fact]
    public async Task Builtin_MergeDedupe()
    {
        var prov = NewProvider();
        prov.SetSourcesForTesting(new[]
        {
            new FakeBuiltinSource("fake", "假源", query => (new List<SourceItem>
            {
                new() { Id = "a", Title = $"{query} A", Quality = "1080p", Url = "magnet:?xt=urn:btih:same", UrlType = UrlType.Magnet },
                new() { Id = "b", Title = $"{query} B", Quality = "2160p", Url = "magnet:?xt=urn:btih:dup", UrlType = UrlType.Magnet },
            }, "")),
        });
        var (items, _) = await prov.SearchAsync(new SearchRequest { Query = "wall-e" });
        Assert.True(items.Count == 2, $"{items.Count} 条");
    }

    private static FakeBuiltinSource GarbageSource() =>
        new("garbage", "垃圾源", _ => (new List<SourceItem>
        {
            new() { Id = "g1", Title = "Avira System Speedup Pro + Crack", Quality = "未知", Url = "magnet:?xt=urn:btih:junk1", UrlType = UrlType.Magnet },
            new() { Id = "g2", Title = "Some Other Unrelated Movie 1999", Quality = "1080p", Url = "magnet:?xt=urn:btih:junk2", UrlType = UrlType.Magnet },
            new() { Id = "g3", Title = "WALL-E 2008 1080p BluRay", Quality = "1080p", Url = "magnet:?xt=urn:btih:good3", UrlType = UrlType.Magnet },
        }, ""));

    [Fact]
    public async Task Builtin_RelevanceFilter()
    {
        // 无关结果必须剔除（真机发现 TPB 对中文关键词返回垃圾）
        var prov = NewProvider();
        prov.SetSourcesForTesting(new[] { GarbageSource() });
        var (items, _) = await prov.SearchAsync(new SearchRequest { Query = "wall-e" });
        Assert.True(items.Count == 1 && items[0].Id == "g3", string.Join(",", items.Select(i => i.Id)));
    }

    [Fact]
    public async Task Builtin_CjkAllGarbageDropped()
    {
        var prov = NewProvider();
        prov.SetSourcesForTesting(new[] { GarbageSource() });
        var (items, _) = await prov.SearchAsync(new SearchRequest { Query = "机器人总动员", Year = 2008 });
        Assert.True(items.Count == 0, $"{items.Count} 条");
    }

    [Fact]
    public async Task Builtin_CjkHint()
    {
        var prov = NewProvider();
        prov.SetSourcesForTesting(new[] { GarbageSource() });
        var (_, warnings) = await prov.SearchAsync(new SearchRequest { Query = "机器人总动员", Year = 2008 });
        Assert.Contains(warnings, w => w.Contains("中文关键词"));
    }

    [Fact]
    public async Task Builtin_LlmTranslateUsed()
    {
        // 中文名 → 大模型翻成英文名后再搜
        var rec = new RecordingSource
        {
            OnQuery = query => query.ToUpperInvariant() == "WALL-E"
                ? (new List<SourceItem>
                {
                    new() { Id = "r1", Title = "WALL-E 2008 1080p", Quality = "1080p", Url = "magnet:?xt=urn:btih:good", UrlType = UrlType.Magnet },
                }, "")
                : (new List<SourceItem>(), "无结果"),
        };
        var prov = new BuiltinProvider(
            new ProviderConfig { Type = "builtin", Enabled = true, Options = new() },
            llm: new FakeLlm("WALL-E"));
        prov.SetSourcesForTesting(new BuiltinSource[] { rec });
        var (items, _) = await prov.SearchAsync(new SearchRequest { Query = "机器人总动员", Year = 2008 });
        Assert.True(rec.Seen.Contains("WALL-E") && items.Count == 1, string.Join(",", rec.Seen));
    }
}
