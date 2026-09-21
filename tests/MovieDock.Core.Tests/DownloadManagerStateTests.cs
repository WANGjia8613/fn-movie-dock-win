using MovieDock.Core.Config;
using MovieDock.Core.Download;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 7 节（任务状态持久化 + per-task 目录 + aria2 选项）与第 10 节（磁力 dn= 推断片名）。</summary>
public class DownloadManagerStateTests
{
    /// <summary>造一套「持久化 → 重开」场景，返回 (重开后的 manager, 临时根目录)。</summary>
    private static (DownloadManager Mgr2, string Root) ReopenManager()
    {
        var tmp = TestSupport.NewTempDir();
        var cfg = TestSupport.NewConfig(tmp);
        cfg.Downloader.BtTrackers = "udp://tracker.example:1337/announce";

        var mgr = new DownloadManager(cfg);
        mgr.Tasks["active1"] = new DownloadTask
        {
            TaskId = "active1", Title = "进行中", Url = "magnet:?xt=urn:btih:1",
            Status = "active", Gid = "g1", CreatedAt = "2026-01-01T00:00:00+08:00",
        };
        mgr.Tasks["done1"] = new DownloadTask
        {
            TaskId = "done1", Title = "已完成", Url = "magnet:?xt=urn:btih:2",
            Status = "complete", CreatedAt = "2026-01-02T00:00:00+08:00",
            OrganizedPath = Path.Combine(tmp, "dl", "movies", "x.mkv"),
            SubtitleStatus = "done", SubtitlePath = "/x/x.zh.ass", SubtitleNote = "测试字幕",
        };
        mgr.Dirty = true;
        mgr.Persist(force: true);
        return (new DownloadManager(cfg), tmp);
    }

    [Fact]
    public void Persist_KeepsComplete()
    {
        var (mgr2, _) = ReopenManager();
        Assert.Equal("complete", mgr2.Get("done1")?.Status);
    }

    [Fact]
    public void Persist_MarksInterrupted()
    {
        var (mgr2, _) = ReopenManager();
        Assert.Equal("interrupted", mgr2.Get("active1")?.Status);
    }

    [Fact]
    public void Persist_SubtitleFields()
    {
        var (mgr2, _) = ReopenManager();
        Assert.Equal("/x/x.zh.ass", mgr2.Get("done1")?.SubtitlePath);
    }

    [Fact]
    public void Aria2_NoSeed()
    {
        var (mgr2, _) = ReopenManager();
        var task = new DownloadTask { TaskId = "dir1", Title = "t", Url = "magnet:?xt=urn:btih:3" };
        var opts = mgr2.Aria2Options(task);
        Assert.True(opts.GetValueOrDefault("seed-time") == "0" && opts.GetValueOrDefault("seed-ratio") == "0.0",
            opts.GetValueOrDefault("seed-time"));
    }

    [Fact]
    public void Aria2_Tracker()
    {
        var (mgr2, _) = ReopenManager();
        var task = new DownloadTask { TaskId = "dir1", Title = "t", Url = "magnet:?xt=urn:btih:3" };
        var opts = mgr2.Aria2Options(task);
        Assert.True(opts.GetValueOrDefault("bt-tracker", "").StartsWith("udp://tracker.example"),
            opts.GetValueOrDefault("bt-tracker", ""));
    }

    [Fact]
    public void Aria2_PerTaskDir()
    {
        var (mgr2, _) = ReopenManager();
        var task = new DownloadTask { TaskId = "dir1", Title = "t", Url = "magnet:?xt=urn:btih:3" };
        var opts = mgr2.Aria2Options(task);
        Assert.True(opts["dir"].EndsWith("dir1") && opts["dir"].Contains("incoming"), opts["dir"]);
    }

    [Fact]
    public void Magnet_DnTitle()
    {
        var (title, year) = DownloadManager.TitleFromUrl(
            "magnet:?xt=urn:btih:6687A51BB38802620E13542D9C50235039F939D1&dn=WALL-E+%282008%29+1080p+BrRip+x264+-+1.20GB+-+YIFY");
        Assert.True(title == "WALL-E" && year == 2008, $"{title}/{year}");
    }

    [Fact]
    public void Url_FallbackTitle()
    {
        var (title2, _) = DownloadManager.TitleFromUrl("https://example.com/Some.Movie.2019.1080p.mkv");
        Assert.False(string.IsNullOrEmpty(title2));
    }
}
