using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MovieDock.Core.Download;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;

namespace MovieDock.App;

public partial class MainWindow : Window
{
    private static readonly Brush BestRowBrush = (Brush)new BrushConverter().ConvertFrom("#FFF3D6")!;

    private readonly ObservableCollection<SourceItem> _results = new();
    private readonly ObservableCollection<DownloadTask> _tasks = new();
    private readonly object _tasksLock = new();
    private readonly DispatcherTimer _statusTimer;
    private CancellationTokenSource? _searchCts;
    private string _bestId = "";

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        TasksGrid.ItemsSource = _tasks;
        BindingOperations.EnableCollectionSynchronization(_tasks, _tasksLock);
        App.Manager.TaskChanged += OnManagerTaskChanged;
        Closed += (_, _) => App.Manager.TaskChanged -= OnManagerTaskChanged;
        Loaded += async (_, _) =>
        {
            ReconcileTasks();
            LoadSettings();
            await RefreshAria2StatusAsync();
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusTimer.Tick += async (_, _) => await RefreshAria2StatusAsync();
        _statusTimer.Start();
    }

    // ==================== 检索 ====================
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchHint.Text = "请输入片名";
            return;
        }
        int? year = int.TryParse(YearBox.Text.Trim(), out var y) ? y : null;
        var quality = (QualityCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (quality == "全部") quality = null;

        SetSearchBusy(true);
        _searchCts = new CancellationTokenSource();
        try
        {
            var resp = await App.Searcher.SearchAsync(new SearchRequest
            {
                Query = query,
                Year = year,
                Quality = quality,
            }, _searchCts.Token);
            _results.Clear();
            foreach (var item in resp.Items) _results.Add(item);
            _bestId = resp.BestId;
            WarningsBox.ItemsSource = resp.Warnings;
            ResultsGrid.Items.Refresh();
            SearchHint.Text = $"共 {resp.Items.Count} 条候选"
                + (resp.Providers.Count > 0 ? $"（来源：{string.Join("、", resp.Providers)}）" : "");
        }
        catch (OperationCanceledException)
        {
            SearchHint.Text = "已取消检索";
        }
        catch (Exception ex)
        {
            SearchHint.Text = $"检索出错：{ex.Message}";
        }
        finally
        {
            SetSearchBusy(false);
        }
    }

    private void CancelSearch_Click(object sender, RoutedEventArgs e) => _searchCts?.Cancel();

    private void SetSearchBusy(bool busy)
    {
        SearchButton.IsEnabled = !busy;
        CancelSearchButton.IsEnabled = busy;
        SearchButton.Content = busy ? "搜索中…" : "搜索";
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private void ResultsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Background = e.Row.Item is SourceItem it && it.Id.Length > 0 && it.Id == _bestId
            ? BestRowBrush
            : Brushes.White;
    }

    private async void ResultsGrid_DoubleClick(object sender, MouseButtonEventArgs e) => await DownloadSelectedAsync();

    private async void DownloadSelected_Click(object sender, RoutedEventArgs e) => await DownloadSelectedAsync();

    private async Task DownloadSelectedAsync()
    {
        if (ResultsGrid.SelectedItem is not SourceItem item)
        {
            SearchHint.Text = "先在结果列表里选中一条候选";
            return;
        }
        var task = await App.Manager.StartDownloadAsync(item.Url, item.Title, null, item.Quality);
        SearchHint.Text = $"已添加下载：{task.Title}";
        MainTabs.SelectedIndex = 1;
    }

    private async void AddManual_Click(object sender, RoutedEventArgs e)
    {
        var url = ManualUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            SearchHint.Text = "先粘贴磁力链接或 HTTP 直链";
            return;
        }
        var task = await App.Manager.StartDownloadAsync(url);
        ManualUrlBox.Clear();
        SearchHint.Text = $"已添加下载：{task.Title}";
        MainTabs.SelectedIndex = 1;
    }

    // ==================== 任务 ====================
    private void OnManagerTaskChanged(DownloadTask task) =>
        Dispatcher.BeginInvoke(new Action(ReconcileTasks));

    /// <summary>把 Manager 的任务列表（CreatedAt 倒序）同步进绑定集合：增删 + 重排，保留行选中。</summary>
    private void ReconcileTasks()
    {
        lock (_tasksLock)
        {
            var want = App.Manager.ListTasks();
            for (var i = _tasks.Count - 1; i >= 0; i--)
                if (!want.Any(w => w.TaskId == _tasks[i].TaskId))
                    _tasks.RemoveAt(i);
            foreach (var t in want)
                if (IndexOfTask(t.TaskId) < 0)
                    _tasks.Add(t);
            for (var i = 0; i < want.Count; i++)
            {
                var idx = IndexOfTask(want[i].TaskId);
                if (idx >= 0 && idx != i) _tasks.Move(idx, i);
            }
            var active = want.Count(t => t.Status == "active");
            var done = want.Count(t => t.Status == "complete");
            TaskSummaryText.Text = $"共 {want.Count} 个任务 · 下载中 {active} · 已完成 {done}";
        }
    }

    private int IndexOfTask(string taskId)
    {
        for (var i = 0; i < _tasks.Count; i++)
            if (_tasks[i].TaskId == taskId) return i;
        return -1;
    }

    private DownloadTask? SelectedTask => TasksGrid.SelectedItem as DownloadTask;

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is { } t) await App.Manager.PauseTaskAsync(t);
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is { } t) await App.Manager.ResumeTaskAsync(t);
    }

    private async void RetrySubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is { } t) await App.Manager.RetrySubtitleAsync(t);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is { } t)
        {
            await App.Manager.RemoveTaskAsync(t.TaskId, deleteFiles: false);
            ReconcileTasks();
        }
    }

    private async void RemoveWithFiles_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is not { } t) return;
        if (MessageBox.Show($"确定删除任务「{t.Title}」并同时删除已下载的文件吗？\n（仅限下载目录/资料库目录内的文件）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var r = await App.Manager.RemoveTaskAsync(t.TaskId, deleteFiles: true);
        ReconcileTasks();
        TaskSummaryText.Text = r.RemovedFiles.Count > 0
            ? $"任务已删除，同时删除 {r.RemovedFiles.Count} 个文件"
            : "任务已删除（没有可删的文件）";
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        var r = await App.Manager.ClearTasksAsync("completed", deleteFiles: false);
        ReconcileTasks();
        TaskSummaryText.Text = r.Message;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTask is not { } t) return;
        var path = t.OrganizedPath.Length > 0 ? t.OrganizedPath
            : t.SavedPath.Length > 0 ? t.SavedPath
            : t.Files.Count > 0 ? t.Files[0] : "";
        try
        {
            if (path.Length > 0 && File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (path.Length > 0 && Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
            else
                TaskSummaryText.Text = "该任务还没有落盘文件";
        }
        catch (Exception ex)
        {
            TaskSummaryText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private async Task RefreshAria2StatusAsync()
    {
        try
        {
            var info = await App.Manager.Aria2StatusAsync();
            var ok = info.TryGetValue("available", out var a) && a is bool b && b;
            var ver = info.TryGetValue("version", out var v) ? v?.ToString() ?? "" : "";
            Aria2StatusText.Text = ok
                ? $"aria2 已连接（v{ver}）"
                : "aria2 未连接（磁力/种子不可用，HTTP 直链仍可下载）";
        }
        catch (Exception ex)
        {
            Aria2StatusText.Text = $"aria2 状态未知：{ex.Message}";
        }
    }

    // ==================== 设置 ====================
    private void LoadSettings()
    {
        var cfg = App.Config;
        DownloadRootBox.Text = cfg.Paths.DownloadRoot;
        LibraryRootBox.Text = cfg.Organize.LibraryRoot;
        ProxyBox.Text = cfg.Network.Proxy;
        LlmUrlBox.Text = cfg.Llm.BaseUrl;
        LlmKeyBox.Password = cfg.Llm.ApiKey;
        LlmModelBox.Text = cfg.Llm.Model;
        SortByScoreCheck.IsChecked = cfg.Search.SortByScore;
        SubtitleEnabledCheck.IsChecked = cfg.Subtitle.Enabled;
        OrganizeEnabledCheck.IsChecked = cfg.Organize.Enabled;
        foreach (var (box, type) in ProviderBoxes())
            box.IsChecked = cfg.Search.Providers.FirstOrDefault(p => p.Type == type)?.Enabled == true;
    }

    private (CheckBox Box, string Type)[] ProviderBoxes() => new[]
    {
        (ProvBuiltinCheck, "builtin"),
        (ProvDemoCheck, "demo"),
        (ProvLlmCheck, "llm"),
        (ProvQbitCheck, "qbittorrent"),
        (ProvCustomCheck, "custom_api"),
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = App.Config;
        cfg.Paths.DownloadRoot = DownloadRootBox.Text.Trim();
        cfg.Organize.LibraryRoot = LibraryRootBox.Text.Trim();
        cfg.Network.Proxy = ProxyBox.Text.Trim();
        cfg.Llm.BaseUrl = LlmUrlBox.Text.Trim();
        cfg.Llm.ApiKey = LlmKeyBox.Password.Trim();
        cfg.Llm.Model = LlmModelBox.Text.Trim();
        cfg.Search.SortByScore = SortByScoreCheck.IsChecked == true;
        cfg.Subtitle.Enabled = SubtitleEnabledCheck.IsChecked == true;
        cfg.Organize.Enabled = OrganizeEnabledCheck.IsChecked == true;
        foreach (var (box, type) in ProviderBoxes())
        {
            var pc = cfg.Search.Providers.FirstOrDefault(p => p.Type == type);
            if (pc != null) pc.Enabled = box.IsChecked == true;
        }
        try
        {
            App.Store.Save(cfg);
            App.Manager.ApplyConfig(cfg);
            App.RebuildSearcher();
            SaveTip.Text = $"已保存（{DateTime.Now:HH:mm:ss}），立即生效";
        }
        catch (Exception ex)
        {
            SaveTip.Text = $"保存失败：{ex.Message}";
        }
    }

    private void BrowseDownload_Click(object sender, RoutedEventArgs e) =>
        BrowseFolderInto(DownloadRootBox);

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e) =>
        BrowseFolderInto(LibraryRootBox);

    private static void BrowseFolderInto(TextBox box)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (dlg.ShowDialog() == true) box.Text = dlg.FolderName;
    }

    private async void LlmTest_Click(object sender, RoutedEventArgs e)
    {
        LlmTestResult.Text = "测试中…";
        try
        {
            var client = LlmClient.FromInput(LlmUrlBox.Text, LlmKeyBox.Password, LlmModelBox.Text);
            var r = await client.TestAsync();
            LlmTestResult.Foreground = r.Ok ? new SolidColorBrush(Color.FromRgb(0x1e, 0x8e, 0x3e)) : Brushes.DarkRed;
            LlmTestResult.Text = r.Ok ? $"连接成功（{r.Model}）" : $"连接失败：{r.Message}";
        }
        catch (Exception ex)
        {
            LlmTestResult.Foreground = Brushes.DarkRed;
            LlmTestResult.Text = $"连接失败：{ex.Message}";
        }
    }
}
