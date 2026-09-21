using MovieDock.Core.Download;
using Xunit;

namespace MovieDock.Core.Tests;

/// <summary>对应 Python unit_checks 第 13 节（暂停/继续/删除历史；前端 3 项为 Python Web UI 专属，.NET 不移植）。</summary>
public class DownloadManagerOpsTests
{
    private sealed class Scene : IDisposable
    {
        public string Root = TestSupport.NewTempDir();
        public FakeAria2 Fake = new();
        public DownloadManager Mgr;
        public string TaskDirPath;
        public string Video;
        public string SubZh;
        public string SubOther;

        public Scene()
        {
            var cfg = TestSupport.NewConfig(Root);
            Mgr = new DownloadManager(cfg) { Aria2 = Fake };

            // 准备一份「已下载完成」的产物：视频 + 同名字幕 + 任务临时目录
            TaskDirPath = Path.Combine(cfg.DownloadRoot(), "incoming", "t1");
            Directory.CreateDirectory(TaskDirPath);
            File.WriteAllBytes(Path.Combine(TaskDirPath, "junk.torrent"), new byte[] { 0x78 });
            var movieDir = Path.Combine(cfg.DownloadRoot(), "movies", "Demo (2020)");
            Directory.CreateDirectory(movieDir);
            Video = Path.Combine(movieDir, "Demo (2020) - 1080p.mkv");
            File.WriteAllBytes(Video, new byte[128]);
            SubZh = Path.Combine(movieDir, "Demo (2020) - 1080p.zh.ass");
            File.WriteAllBytes(SubZh, new byte[32]);
            SubOther = Path.Combine(movieDir, "Other.zh.ass");
            File.WriteAllBytes(SubOther, new byte[8]);

            Mgr.Tasks["t1"] = new DownloadTask
            {
                TaskId = "t1", Title = "Demo", Url = "magnet:?xt=urn:btih:aaaa", Status = "active",
                Gid = "gA", Engine = "aria2", CreatedAt = "2026-01-05T00:00:00+08:00",
                IncomingDir = TaskDirPath, OrganizedPath = Video, SubtitlePath = SubZh,
            };
        }

        public void Dispose() => Mgr.Dispose();
    }

    [Fact]
    public async Task Task_Pause()
    {
        using var s = new Scene();
        var t1 = s.Mgr.Get("t1")!;
        await s.Mgr.PauseTaskAsync(t1);
        Assert.True(t1.Status == "paused" && s.Fake.CalledWith("aria2.pause", "gA"), t1.Status);
    }

    [Fact]
    public async Task Task_Resume()
    {
        using var s = new Scene();
        var t1 = s.Mgr.Get("t1")!;
        await s.Mgr.ResumeTaskAsync(t1);
        Assert.True(t1.Status == "active" && s.Fake.CalledWith("aria2.unpause", "gA"), t1.Status);
    }

    [Fact]
    public async Task Task_PauseHttpUnsupported()
    {
        using var s = new Scene();
        var httpTask = new DownloadTask { TaskId = "h1", Title = "http", Url = "https://x/a.bin", Status = "active", Engine = "http" };
        await s.Mgr.PauseTaskAsync(httpTask);
        Assert.Contains("不支持暂停", httpTask.Error);
    }

    [Fact]
    public async Task RemoveTask_KeepsFiles()
    {
        using var s = new Scene();
        var res = await s.Mgr.RemoveTaskAsync("t1", deleteFiles: false);
        Assert.True(res.Ok && !s.Mgr.Tasks.ContainsKey("t1") && File.Exists(s.Video) && File.Exists(s.SubZh),
            string.Join(",", res.RemovedFiles));
    }

    [Fact]
    public async Task RemoveTask_DeletesFiles()
    {
        using var s = new Scene();
        var res = await s.Mgr.RemoveTaskAsync("t1", deleteFiles: true);
        Assert.True(!File.Exists(s.Video) && !File.Exists(s.SubZh) && !Directory.Exists(s.TaskDirPath),
            string.Join(",", res.RemovedFiles));
    }

    [Fact]
    public async Task RemoveTask_KeepsUnrelatedSubtitle()
    {
        using var s = new Scene();
        await s.Mgr.RemoveTaskAsync("t1", deleteFiles: true);
        Assert.True(File.Exists(s.SubOther), s.SubOther);
    }

    [Fact]
    public async Task RemoveTask_GuardsOutsideRoots()
    {
        using var s = new Scene();
        // 越界保护：路径在配置目录外 → 不删
        var outsideDir = TestSupport.NewTempDir("moviedock-outside-");
        var outside = Path.Combine(outsideDir, "important.mkv");
        File.WriteAllBytes(outside, new byte[] { 0x6B });
        s.Mgr.Tasks["t3"] = new DownloadTask
        {
            TaskId = "t3", Title = "out", Url = "magnet:?xt=urn:btih:b", Status = "complete",
            OrganizedPath = outside, CreatedAt = "2026-01-06T00:00:00+08:00",
        };
        var res = await s.Mgr.RemoveTaskAsync("t3", deleteFiles: true);
        Assert.True(File.Exists(outside) && res.RemovedFiles.Count == 0, string.Join(",", res.RemovedFiles));
    }

    [Fact]
    public async Task Clear_CompletedOnly()
    {
        using var s = new Scene();
        s.Mgr.Tasks["f1"] = new DownloadTask
        {
            TaskId = "f1", Title = "f", Url = "magnet:?xt=urn:btih:c", Status = "complete",
            CreatedAt = "2026-01-07T00:00:00+08:00",
        };
        s.Mgr.Tasks["r1"] = new DownloadTask
        {
            TaskId = "r1", Title = "r", Url = "magnet:?xt=urn:btih:d", Status = "active",
            Gid = "gR", Engine = "aria2", CreatedAt = "2026-01-08T00:00:00+08:00",
        };
        var res = await s.Mgr.ClearTasksAsync("completed");
        Assert.True(res.Removed == 1 && !s.Mgr.Tasks.ContainsKey("f1") && s.Mgr.Tasks.ContainsKey("r1"),
            $"removed={res.Removed}");
    }
}
