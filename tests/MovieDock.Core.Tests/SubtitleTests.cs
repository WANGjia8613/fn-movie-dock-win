using MovieDock.Core.Config;
using MovieDock.Core.Subtitle;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 5 节（字幕：关键词 / 打分 / 命名）。</summary>
public class SubtitleTests
{
    private const string VideoName = "WALL-E.2008.2160p.UHD.BDRemux.HDR.DoVi.P8.Hybrid.by.DVT.mkv";

    private static string VideoPath() =>
        Path.Combine(TestSupport.NewTempDir(), VideoName); // 仅做字符串解析，不需要文件存在

    private static SubtitleService NewService(SubtitleConfig? cfg = null) => new(cfg ?? new SubtitleConfig());

    private static List<string> Tokens() => SubtitleService.TokensFromVideo(VideoPath());

    [Fact]
    public void Subtitle_KeywordsExtra()
    {
        var cfg = new SubtitleConfig { ExtraKeywords = new List<string> { "机器人总动员", "瓦力" } };
        var kw = NewService(cfg).KeywordCandidates(VideoPath(), "", 2008);
        Assert.True(kw.Contains("机器人总动员") && kw.Contains("瓦力"), string.Join("|", kw.Take(6)));
    }

    [Fact]
    public void Subtitle_KeywordsEnglish()
    {
        var kw = NewService().KeywordCandidates(VideoPath(), "", 2008);
        Assert.Contains(kw, k => k.Contains("WALL E"));
    }

    [Fact]
    public void Subtitle_Tokens()
    {
        var tokens = Tokens();
        Assert.True(tokens.Contains("2160p") && tokens.Contains("uhd") && tokens.Contains("dvt"), string.Join(",", tokens));
    }

    [Fact]
    public void Subtitle_EntryPrefersAssBilingual()
    {
        var service = NewService();
        var tokens = Tokens();
        var assBilingual = new SubHdEntry { Sid = "1", Title = "WALL-E.2008.2160p.UHD.BluRay.4K适配HDR 简英双语", Fmt = "ASS" };
        var srtOnly = new SubHdEntry { Sid = "2", Title = "WALL-E.2008.1080p.BluRay", Fmt = "SRT" };
        var a = service.ScoreEntry(assBilingual, tokens, true);
        var b = service.ScoreEntry(srtOnly, tokens, true);
        Assert.True(a > b, $"{a} vs {b}");
    }

    [Fact]
    public void Subtitle_FileRank()
    {
        Assert.True(SubtitleService.ScoreSubFile("x.zh.ass") > SubtitleService.ScoreSubFile("x.eng.srt"), "ass>.eng.srt");
    }

    [Fact]
    public void Subtitle_SimplifiedPreferred()
    {
        // 同一发布版本下，简体版应优先于繁体版（真机反馈：繁英被选中过）
        var service = NewService();
        var tokens = Tokens();
        var assSimp = new SubHdEntry { Sid = "3", Title = "WALL-E.2008.1080p.BluRay.x264 简英双语", Fmt = "ASS", Lang = "双语 简体 英语" };
        var assTrad = new SubHdEntry { Sid = "4", Title = "WALL-E.2008.1080p.BluRay.x264.DTS-WiKi.cht&eng", Fmt = "ASS", Lang = "双语 繁体 英语" };
        var s = service.ScoreEntry(assSimp, tokens, true, true);
        var t = service.ScoreEntry(assTrad, tokens, true, true);
        Assert.True(s > t, $"简={s} 繁={t}");
    }

    [Fact]
    public void Subtitle_NameTemplate()
    {
        var cfg = new SubtitleConfig();
        var stem = Path.GetFileNameWithoutExtension(VideoName);
        var name = (cfg.NameTemplate ?? "{video}.zh").Replace("{video}", stem);
        Assert.True(name.EndsWith(".zh", StringComparison.Ordinal) && name.Contains("DVT"), name);
    }
}
