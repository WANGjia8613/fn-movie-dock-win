using System.Windows;
using MovieDock.Core.Config;
using MovieDock.Core.Download;
using MovieDock.Core.Search;

namespace MovieDock.App;

public partial class App : Application
{
    public static ConfigStore Store { get; private set; } = null!;
    public static AppConfig Config { get; private set; } = null!;
    public static DownloadManager Manager { get; private set; } = null!;
    public static Aria2ProcessHost Aria2Host { get; private set; } = null!;
    public static SearchService Searcher { get; private set; } = null!;

    /// <summary>设置变更后重建检索聚合（providers 随配置变化）。</summary>
    public static void RebuildSearcher() => Searcher = new SearchService(Config);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Store = new ConfigStore();
        Config = Store.Load();
        Manager = new DownloadManager(Config);
        Aria2Host = new Aria2ProcessHost();
        Searcher = new SearchService(Config);
        if (Config.Downloader.Aria2Mode != "none")
            _ = EnsureAria2Async();
        Manager.StartBackground();
        new MainWindow().Show();
    }

    private static async Task EnsureAria2Async()
    {
        try
        {
            await Aria2Host.EnsureRunningAsync(
                Manager.Aria2, Config.IncomingDir(),
                Config.Downloader.Aria2.RpcSecret, Config.Downloader.Aria2.Port);
        }
        catch
        {
            // aria2 不可用：磁力/种子任务会给出中文错误提示，HTTP 直链引擎仍可下载
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            await Manager.StopAsync();
            Manager.Dispose();
            await Aria2Host.DisposeAsync();
        }
        catch { /* 退出阶段不再报错 */ }
        base.OnExit(e);
    }
}
