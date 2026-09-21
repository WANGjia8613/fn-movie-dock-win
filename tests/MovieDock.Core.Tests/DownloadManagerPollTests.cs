using MovieDock.Core.Download;
using MovieDock.Core.Models;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>
/// 对应 Python unit_checks 第 9 节（磁力 followedBy 接管 + 占位文件防整理 —— 真机发现的 bug）。
/// 整节共用一套临时目录与假 aria2，按 Python 脚本的顺序在一个 Fact 里顺序断言会破坏并行隔离，
/// 这里按用例拆分、每个 Fact 自建场景。
/// </summary>
public class DownloadManagerPollTests
{
    private sealed class Scene : IDisposable
    {
        public string Root = TestSupport.NewTempDir();
        public string TaskDirPath;
        public string RealFile;
        public string MetaTorrent;
        public FakeAria2 Fake = new();
        public DownloadManager Mgr;

        public Scene()
        {
            var cfg = TestSupport.NewConfig(Root);
            cfg.Organize.LibraryRoot = "";
            TaskDirPath = Path.Combine(cfg.DownloadRoot(), "incoming", "mag1");
            Directory.CreateDirectory(TaskDirPath);
            RealFile = Path.Combine(TaskDirPath, "WALL-E.mp4");
            File.WriteAllBytes(RealFile, new byte[1000]);
            MetaTorrent = Path.Combine(TaskDirPath, "6687a51b.torrent");
            File.WriteAllBytes(MetaTorrent, new byte[13288]);

            Fake.States["meta1"] = TestSupport.Aria2State($$"""
                {"status":"complete","followedBy":["real1"],"totalLength":"13288","completedLength":"13288",
                 "files":[{"path":{{Json(MetaTorrent)}},"length":"13288"}]}
                """);
            Fake.States["real1"] = TestSupport.Aria2State($$"""
                {"status":"active","totalLength":"1000000","completedLength":"1000","downloadSpeed":"500",
                 "files":[{"path":{{Json(RealFile)}},"length":"1000000"}]}
                """);
            Fake.States["real2"] = TestSupport.Aria2State($$"""
                {"status":"complete","totalLength":"1000","completedLength":"1000",
                 "files":[{"path":{{Json(RealFile)}},"length":"1000"}]}
                """);
            Mgr = new DownloadManager(cfg) { Aria2 = Fake };
        }

        public static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

        public void Dispose() => Mgr.Dispose();
    }

    private static DownloadTask MagnetTask(string id, string gid) => new()
    {
        TaskId = id, Title = "WALL-E", Url = $"magnet:?xt=urn:btih:{id}",
        Status = "active", Gid = gid, Engine = "aria2",
        OrganizeOpts = new OrganizeOptions { Enabled = true, Mode = "copy" },
        CreatedAt = "2026-01-03T00:00:00+08:00",
    };

    [Fact]
    public async Task Magnet_FollowedByAdopt_AndNotFinalizedEarly()
    {
        using var s = new Scene();
        // 元数据下载完成 → 切换到 followedBy 的真实 GID，不得当成完成
        var t = MagnetTask("mag1", "meta1");
        s.Mgr.Tasks["mag1"] = t;
        await s.Mgr.PollOnceAsync(default);

        Assert.True(t.Gid == "real1" && t.Status == "active", $"gid={t.Gid} status={t.Status}");
        Assert.True(t.OrganizedPath.Length == 0 && File.Exists(s.RealFile), $"organized={t.OrganizedPath}");
    }

    [Fact]
    public async Task IncompleteFile_NotOrganized_UntilFullSize()
    {
        using var s = new Scene();
        var t2 = MagnetTask("mag2", "real2");
        s.Mgr.Tasks["mag2"] = t2;

        // 文件只写了一半（总长对不上）→ 不得整理
        File.WriteAllBytes(s.RealFile, new byte[500]);
        await s.Mgr.PollOnceAsync(default);
        Assert.True(t2.Status == "active" && t2.OrganizedPath.Length == 0,
            $"status={t2.Status} organized={t2.OrganizedPath}");

        // 写完了再轮询才收尾
        File.WriteAllBytes(s.RealFile, new byte[1000]);
        await s.Mgr.PollOnceAsync(default);
        Assert.True(t2.Status == "complete" && t2.OrganizedPath.Length > 0,
            $"status={t2.Status} organized={t2.OrganizedPath}");
    }

    [Fact]
    public void CollectOutputFiles_ExcludesTorrentMetadata()
    {
        using var s = new Scene();
        var t2 = MagnetTask("mag2", "real2");
        var files = s.Mgr.CollectOutputFiles(t2, new List<string> { s.MetaTorrent });
        Assert.DoesNotContain(files, p => p.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase));
    }
}
