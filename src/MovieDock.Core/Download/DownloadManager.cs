using System.Collections.Concurrent;
using System.Text.Json;
using MovieDock.Core.Config;
using MovieDock.Core.Llm;
using MovieDock.Core.Models;
using MovieDock.Core.Organize;
using MovieDock.Core.Search;
using MovieDock.Core.Subtitle;

namespace MovieDock.Core.Download;

/// <summary>删除任务的返回结果。</summary>
public sealed record RemoveTaskResult(bool Ok, string Message, List<string> RemovedFiles, int Removed = 0);

/// <summary>
/// 下载任务调度：aria2 优先、HTTP 直链降级、完成后自动整理 + 字幕匹配。
/// 自 Python 版 app/downloader/manager.py（0.4.1）移植：
/// 任务持久化（state_dir/tasks.json）、磁力 followedBy 派生任务、暂停/继续/删除、
/// HTTP 直链断点续传（绕开 aria2 unpause bug）、per-task 目录、字幕自动匹配。
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private static readonly HashSet<string> PendingStatuses = new(StringComparer.OrdinalIgnoreCase)
        { "queued", "active", "paused", "waiting", "interrupted" };

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".ts", ".m2ts",
        ".webm", ".rmvb", ".mpg", ".mpeg", ".iso",
    };

    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private int _tick;
    private bool _dirty;

    public AppConfig Config { get; private set; }
    /// <summary>internal set：测试可注入假 aria2。</summary>
    public Aria2Client Aria2 { get; internal set; }
    /// <summary>待落盘标志（测试可直接置位后调 Persist）。</summary>
    internal bool Dirty { get => _dirty; set => _dirty = value; }
    public Organizer Organizer { get; private set; }
    public SubtitleService Subtitle { get; private set; }
    public ConcurrentDictionary<string, DownloadTask> Tasks { get; } = new();

    /// <summary>任务状态变化（可能在后台线程触发，UI 需自行调度）。</summary>
    public event Action<DownloadTask>? TaskChanged;

    public string StatePath => Path.Combine(Config.StateDir(), "tasks.json");

    public DownloadManager(AppConfig cfg)
    {
        Config = cfg;
        Aria2 = new Aria2Client(cfg.Downloader.Aria2.RpcUrl, cfg.Downloader.Aria2.RpcSecret);
        Organizer = new Organizer(cfg.Organize, cfg.DownloadRoot(), ResolveLibraryRoot(cfg));
        Subtitle = new SubtitleService(cfg.Subtitle, CreateLlm(cfg), cfg.Network.Proxy);
        LoadState();
    }

    private static string ResolveLibraryRoot(AppConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.Organize.LibraryRoot) ? "" : cfg.LibraryRoot();

    private static LlmClient? CreateLlm(AppConfig cfg) =>
        string.IsNullOrEmpty(cfg.Llm.ApiKey) ? null : new LlmClient(cfg.Llm);

    // ---------- 状态持久化 ----------
    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = doc.RootElement;
            var tasksEl = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tasks", out var te)
                ? te : root;
            if (tasksEl.ValueKind != JsonValueKind.Array) return;

            foreach (var entry in tasksEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, object?>();
                foreach (var p in entry.EnumerateObject())
                    dict[p.Name] = p.Value.Clone();
                var task = DownloadTask.FromState(dict);
                if (task == null) continue;
                // 重启后下载会话已丢失：非终态任务标记 interrupted
                if (task.Status is not ("complete" or "error"))
                {
                    task.Status = "interrupted";
                    if (task.Error.Length == 0)
                        task.Error = "应用重启，下载会话已丢失，请重新添加任务";
                }
                Tasks[task.TaskId] = task;
            }
        }
        catch (Exception) { /* 状态文件损坏则忽略 */ }
    }

    internal void Persist(bool force = false)
    {
        if (!force && !_dirty) return;
        try
        {
            Directory.CreateDirectory(Config.StateDir());
            var json = DownloadTask.SerializeState(Tasks.Values);
            var tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StatePath, overwrite: true);
            _dirty = false;
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            // 落盘失败不影响主流程
        }
    }

    /// <summary>配置热更新：同步下载根目录、整理规则、aria2 RPC、字幕设置。</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        Config = cfg;
        Organizer = new Organizer(cfg.Organize, cfg.DownloadRoot(), ResolveLibraryRoot(cfg));
        Subtitle = new SubtitleService(cfg.Subtitle, CreateLlm(cfg), cfg.Network.Proxy);
        Aria2 = new Aria2Client(cfg.Downloader.Aria2.RpcUrl, cfg.Downloader.Aria2.RpcSecret);
        try
        {
            Directory.CreateDirectory(cfg.DownloadRoot());
            Directory.CreateDirectory(cfg.IncomingDir());
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
    }

    public void StartBackground()
    {
        if (_pollTask != null) return;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            _pollTask = null;
        }
        Persist(force: true);
    }

    public IReadOnlyList<DownloadTask> ListTasks() =>
        Tasks.Values.OrderByDescending(t => t.CreatedAt, StringComparer.Ordinal).ToList();

    public DownloadTask? Get(string taskId) => Tasks.TryGetValue(taskId, out var t) ? t : null;

    // ---------- 创建任务 ----------
    /// <summary>任务专属下载目录（per_task_dir 开启时 incoming/&lt;task_id&gt;/）。</summary>
    public string TaskDir(DownloadTask task)
    {
        if (!Config.Downloader.PerTaskDir)
            return Config.IncomingDir();
        if (task.IncomingDir.Length > 0)
            return task.IncomingDir;
        var path = Path.Combine(Config.IncomingDir(), task.TaskId);
        task.IncomingDir = path;
        return path;
    }

    public Task<DownloadTask> StartDownloadAsync(
        string url, string title = "", int? year = null, string quality = "",
        OrganizeOptions? organize = null, bool? fetchSubtitle = null)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrEmpty(title))
        {
            var (parsedTitle, parsedYear) = TitleFromUrl(url);
            title = parsedTitle.Length > 0 ? parsedTitle : "未命名任务";
            year ??= parsedYear;
        }
        else
        {
            var (t2, y2) = NameParsing.ParseTitleYear(title);
            if (t2.Length > 0) title = t2;
            year ??= y2;
        }

        var task = new DownloadTask
        {
            Title = title,
            Url = url,
            Quality = quality ?? "",
            Year = year,
            Status = "queued",
            OrganizeOpts = organize ?? new OrganizeOptions(),
            FetchSubtitle = fetchSubtitle ?? Config.Subtitle.Enabled,
        };
        task.Tags = Ranking.DetectTags($"{title} {quality}");
        var (season, episode) = NameParsing.ParseEpisode($"{title} {url}");
        if (episode is not null)
            task.Episode = NameParsing.EpisodeLabel(season, episode);
        Tasks[task.TaskId] = task;
        _dirty = true;
        Persist();
        Raise(task);
        _ = RunTaskAsync(task);
        return Task.FromResult(task);
    }

    /// <summary>从链接推断片名/年份：磁力优先看 dn= 参数，其余交给 ParseTitleYear。</summary>
    internal static (string Title, int? Year) TitleFromUrl(string url)
    {
        var raw = (url ?? "").Trim();
        if (raw.ToLowerInvariant().StartsWith("magnet:") && raw.Contains('?'))
        {
            try
            {
                var query = raw.Split('?', 2)[1];
                foreach (var pair in query.Split('&'))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && kv[0].Equals("dn", StringComparison.OrdinalIgnoreCase))
                    {
                        var name = Uri.UnescapeDataString(kv[1].Replace("+", " "));
                        var (title, year) = NameParsing.ParseTitleYear(name);
                        if (title.Length > 0)
                            return (title, year);
                        break;
                    }
                }
            }
            catch (Exception) { /* 磁力参数解析失败走兜底 */ }
        }
        return NameParsing.ParseTitleYear(raw);
    }

    private static UrlType Classify(string? url)
    {
        var low = (url ?? "").ToLowerInvariant();
        if (low.StartsWith("magnet:")) return UrlType.Magnet;
        if (low.Contains(".torrent")) return UrlType.Torrent;
        if (low.StartsWith("http://") || low.StartsWith("https://")) return UrlType.Http;
        return UrlType.Unknown;
    }

    private async Task RunTaskAsync(DownloadTask task)
    {
        try
        {
            var kind = Classify(task.Url);
            var aria2Ok = kind != UrlType.Unknown && await Aria2.IsAvailableAsync(_cts.Token);

            if (aria2Ok)
            {
                task.Engine = "aria2";
                await StartAria2Async(task);
            }
            else if (kind == UrlType.Http)
            {
                task.Engine = "http";
                await StartHttpAsync(task);
            }
            else
            {
                task.Status = "error";
                task.Error = "当前环境未检测到 aria2 下载引擎，且该链接不是 HTTP 直链，无法下载磁力/种子。"
                    + "请安装 aria2c.exe（程序目录或 PATH），或改用 HTTP 直链。";
                task.UpdatedAt = DownloadTask.Now();
                _dirty = true;
                Persist();
                Raise(task);
            }
        }
        catch (Exception exc)
        {
            task.Status = "error";
            task.Error = FriendlyAria2Error(exc.Message);
            task.UpdatedAt = DownloadTask.Now();
            _dirty = true;
            Persist();
            Raise(task);
        }
    }

    /// <summary>把 aria2 的英文错误改成能看懂的中文提示（保留原文便于排查）。</summary>
    internal static string FriendlyAria2Error(string? raw)
    {
        var text = (raw ?? "").Trim();
        var low = text.ToLowerInvariant();
        var mapping = new (string Key, string Zh)[]
        {
            // 注意顺序：具体的长 key 必须先于被包含的短 key（"max file not found" 含 "not found"）
            ("max file not found", "种子内找不到可下载的文件"),
            ("already registered", "该资源已在 aria2 下载列表中（可能正在下载），无需重复添加"),
            ("no uri", "链接为空或无法识别"),
            ("not found", "aria2 中找不到该任务"),
            ("timeout", "连接超时（资源站/做种者不可达）"),
            ("unrecognized", "链接格式无法识别"),
            ("unfinished", "仍有未完成的分片，下载已中断"),
        };
        foreach (var (key, zh) in mapping)
        {
            if (low.Contains(key))
                return $"{zh}（aria2: {Truncate(text, 120)}）";
        }
        return text;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    /// <summary>aria2 addUri 选项：不做种、断点续传、BT 加速；合并 extra_options 与 bt-tracker。</summary>
    internal Dictionary<string, string> Aria2Options(DownloadTask task)
    {
        var target = TaskDir(task);
        Directory.CreateDirectory(target);
        var options = new Dictionary<string, string>
        {
            ["dir"] = target,
            // 不做种（防止 NAS 变 PCDN 节点）
            ["seed-time"] = "0",
            ["seed-ratio"] = "0.0",
            ["bt-max-peers"] = "64",
            ["max-connection-per-server"] = "8",
            ["split"] = "8",
            ["continue"] = "true",
            ["auto-file-renaming"] = "true",
            ["allow-overwrite"] = "false",
            ["file-allocation"] = "none",
            ["follow-torrent"] = "true",
            ["bt-save-metadata"] = "true",
            ["enable-dht"] = "true",
            ["bt-enable-lpd"] = "true",
        };
        var trackers = (Config.Downloader.BtTrackers ?? "").Trim();
        if (trackers.Length > 0)
            options["bt-tracker"] = trackers;
        foreach (var (k, v) in Config.Downloader.ExtraOptions)
            options[k] = v;
        return options;
    }

    private async Task StartAria2Async(DownloadTask task)
    {
        var options = Aria2Options(task);
        var gid = await Aria2.AddUriAsync(new[] { task.Url }, options, _cts.Token);
        task.Gid = gid;
        task.Status = "active";
        task.UpdatedAt = DownloadTask.Now();
        _dirty = true;
        Raise(task);
    }

    private async Task StartHttpAsync(DownloadTask task)
    {
        var target = TaskDir(task);
        Directory.CreateDirectory(target);
        var path = await HttpDownloader.DownloadHttpToAsync(
            task.Url, target, task.Title, p => OnHttpProgress(task, p), _cts.Token);
        task.HttpDest = path;
        task.Files = new List<string> { path };
        task.SavedPath = path;
        task.Progress = 100.0;
        task.Status = "complete";
        if (File.Exists(path))
        {
            var size = new FileInfo(path).Length;
            if (task.TotalLength.Length == 0)
                task.CompletedLength = SizeFormat.HumanSize(size);
        }
        task.UpdatedAt = DownloadTask.Now();
        await FinishTaskAsync(task, File.Exists(path) ? new List<string> { path } : new());
        _dirty = true;
        Persist();
        Raise(task);
    }

    private void OnHttpProgress(DownloadTask task, DownloadProgress p)
    {
        task.CompletedLength = SizeFormat.HumanSize(p.Done);
        task.TotalLength = p.Total > 0 ? SizeFormat.HumanSize(p.Total) : "";
        task.Progress = p.Total > 0 ? Math.Round(p.Done * 100.0 / p.Total, 2) : 0.0;
        task.DownloadSpeed = p.SpeedBytesPerSec > 0 ? SizeFormat.HumanSize(p.SpeedBytesPerSec) + "/s" : "";
        task.Status = "active";
        task.UpdatedAt = DownloadTask.Now();
        Raise(task);
    }

    // ---------- 轮询 ----------
    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct); }
            catch (Exception) { /* 单轮失败不影响后续轮询 */ }
            _tick++;
            Persist(force: _tick % 5 == 0);
            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task PollOnceAsync(CancellationToken ct)
    {
        var pending = Tasks.Values
            .Where(t => t.Engine == "aria2" && t.Gid.Length > 0 && PendingStatuses.Contains(t.Status))
            .ToList();
        if (pending.Count == 0) return;

        foreach (var task in pending)
        {
            Dictionary<string, JsonElement> st;
            try
            {
                st = await Aria2.TellStatusAsync(task.Gid, ct);
            }
            catch (Exception exc)
            {
                task.Error = exc.Message;
                if (task.Status == "interrupted")
                    task.Error = "下载会话已丢失（应用重启后无法恢复），请重新添加任务";
                Raise(task);
                continue;
            }
            if (st.Count == 0) continue;

            // 磁力链接：首次 addUri 拿到的是「元数据下载」的 GID，
            // 元数据完成时 aria2 会派生子任务（followedBy）——那才是真正的下载。
            if (st.TryGetValue("followedBy", out var followedEl) &&
                followedEl.ValueKind == JsonValueKind.Array &&
                followedEl.EnumerateArray().Any())
            {
                var newGid = followedEl.EnumerateArray().FirstOrDefault().GetString() ?? "";
                if (newGid.Length > 0 && newGid != task.Gid)
                {
                    task.Gid = newGid;
                    task.Status = "active";
                    task.UpdatedAt = DownloadTask.Now();
                    _dirty = true;
                    try
                    {
                        var st2 = await Aria2.TellStatusAsync(newGid, ct);
                        if (st2.Count > 0) st = st2;
                    }
                    catch (Exception) { continue; }
                }
            }

            var status = GetString(st, "status");
            if (status.Length > 0)
            {
                task.Status = status;
                if (status != "error") task.Error = "";
            }
            var total = GetLong(st, "totalLength");
            var done = GetLong(st, "completedLength");
            var speed = GetLong(st, "downloadSpeed");
            task.TotalLength = total > 0 ? SizeFormat.HumanSize(total) : "";
            task.CompletedLength = done > 0 ? SizeFormat.HumanSize(done) : "";
            // aria2 对已暂停的任务仍会回报上一刻的速度，这里强制清零，避免界面显示「暂停中 448KB/s」
            if (status == "paused")
                task.DownloadSpeed = "";
            else
                task.DownloadSpeed = speed > 0 ? SizeFormat.HumanSize(speed) + "/s" : "";
            if (total > 0)
                task.Progress = Math.Round(done * 100.0 / total, 2);
            else if (status == "complete")
                task.Progress = 100.0;

            var filePaths = new List<string>();
            if (st.TryGetValue("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                foreach (var f in filesEl.EnumerateArray())
                    if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("path", out var p) &&
                        p.ValueKind == JsonValueKind.String)
                    {
                        var s = p.GetString();
                        if (!string.IsNullOrEmpty(s)) filePaths.Add(s);
                    }
            if (filePaths.Count > 0)
            {
                task.Files = filePaths;
                task.SavedPath = Path.GetDirectoryName(filePaths[0]) ?? "";
            }
            var errorMessage = GetString(st, "errorMessage");
            if (errorMessage.Length > 0)
                task.Error = FriendlyAria2Error(errorMessage);
            task.UpdatedAt = DownloadTask.Now();
            _dirty = true;
            Raise(task);

            if (status == "complete")
            {
                var paths = CollectOutputFiles(task, filePaths);
                var primary = PickPrimary(paths);
                // 磁力元数据刚完成时文件还是 0 字节/占位，别急着整理
                if (primary != null && !FileComplete(primary, filesEl))
                {
                    task.Status = "active";
                    task.Progress = Math.Min(task.Progress, 99.9);
                    continue;
                }
                task.SavedPath = paths.Count > 0
                    ? Path.GetDirectoryName(paths[0]) ?? ""
                    : TaskDir(task);
                task.Status = "complete";
                await FinishTaskAsync(task, paths);
            }
            else if (status == "error")
            {
                if (await TryHttpResumeAsync(task))
                    continue;
                task.Status = "error";
                if (task.Error.Length == 0)
                    task.Error = "下载失败";
                Raise(task);
            }
        }
    }

    /// <summary>aria2 对 HTTP 直链的 unpause 会变成 “No URI available.” 错误 —— 可重新添加续传。</summary>
    private bool CanHttpResume(DownloadTask task)
    {
        if (task.ResumeAttempts >= 2)
            return false;
        var low = (task.Url ?? "").ToLowerInvariant();
        if (!low.StartsWith("http://") && !low.StartsWith("https://"))
            return false;
        return (task.Error ?? "").ToLowerInvariant().Contains("no uri");
    }

    /// <summary>重新添加同一链接（continue=true）从断点继续；成功返回 True。force=True：用户点「继续」时直接用。</summary>
    private async Task<bool> TryHttpResumeAsync(DownloadTask task, bool force = false)
    {
        if (!force && !CanHttpResume(task))
            return false;
        task.ResumeAttempts++;
        try
        {
            var options = Aria2Options(task);
            options["continue"] = "true";
            var newGid = await Aria2.AddUriAsync(new[] { task.Url }, options, _cts.Token);
            task.Gid = newGid;
            task.Status = "active";
            task.Error = "";
            task.UpdatedAt = DownloadTask.Now();
            _dirty = true;
            Raise(task);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>优先用 aria2 报的准确文件列表；仅在缺失时回退到任务自己的目录扫描。排除 .aria2 临时与 .torrent 元数据。</summary>
    internal List<string> CollectOutputFiles(DownloadTask task, List<string> filePaths)
    {
        var candidates = new List<string>();
        foreach (var raw in filePaths)
        {
            var ext = Path.GetExtension(raw).ToLowerInvariant();
            if (ext == ".torrent" || raw.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(raw))
                candidates.Add(raw);
        }
        if (candidates.Count == 0)
        {
            var root = TaskDir(task);
            if (Directory.Exists(root))
            {
                try
                {
                    candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(p =>
                        {
                            var ext = Path.GetExtension(p).ToLowerInvariant();
                            return ext != ".torrent" && !p.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { }
            }
        }
        return candidates;
    }

    internal static string? PickPrimary(List<string> paths)
    {
        var existing = paths.Where(File.Exists).ToList();
        var media = existing.Where(p => MediaExts.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();
        if (media.Count > 0)
            return media.OrderByDescending(SafeFileSize).First();
        if (existing.Count > 0)
            return existing.OrderByDescending(SafeFileSize).First();
        return null;
    }

    /// <summary>对照 aria2 报的单文件长度，确认文件真的写完了（防磁力占位文件被整理）。</summary>
    private static bool FileComplete(string path, JsonElement filesEl)
    {
        long actual;
        try { actual = new FileInfo(path).Length; }
        catch (IOException) { return false; }

        long? expected = null;
        if (filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("path", out var p) || p.GetString() != path) continue;
                if (item.TryGetProperty("length", out var l) &&
                    l.ValueKind == JsonValueKind.String &&
                    long.TryParse(l.GetString(), out var len))
                    expected = len;
                break;
            }
        }
        if (expected is long exp)
            return actual >= exp * 0.999;
        return actual > 0;
    }

    // ---------- 完成收尾 ----------
    /// <summary>下载完成后的收尾：整理 + 字幕。任一步失败也不把任务判成失败。</summary>
    private async Task FinishTaskAsync(DownloadTask task, List<string> paths)
    {
        string? video = null;
        try
        {
            video = await OrganizeIfNeededAsync(task, paths);
        }
        catch (Exception exc)
        {
            task.Error = $"自动整理异常：{exc.Message}";
        }
        try
        {
            await SubtitleIfNeededAsync(task, video);
        }
        catch (Exception exc)
        {
            task.SubtitleStatus = "failed";
            task.SubtitleNote = $"字幕匹配异常：{exc.Message}";
        }
        task.UpdatedAt = DownloadTask.Now();
        _dirty = true;
        Persist(force: true);
        Raise(task);
    }

    private async Task<string?> OrganizeIfNeededAsync(DownloadTask task, List<string> paths)
    {
        var opts = task.OrganizeOpts ?? new OrganizeOptions();
        var enabled = opts.Enabled ?? Config.Organize.Enabled;
        var primary = PickPrimary(paths);
        if (primary == null)
            return null;
        task.SavedPath = Path.GetDirectoryName(primary) ?? "";

        if (!enabled)
            return primary;
        try
        {
            // move/copy 是阻塞 IO，放到线程池避免影响轮询
            var finalPath = await Task.Run(() =>
                Organizer.OrganizeFile(primary, task.Title, task.Year, task.Quality, opts));
            task.OrganizedPath = finalPath;
            task.Files = new List<string> { finalPath };
            return finalPath;
        }
        catch (Exception exc)
        {
            task.Error = $"下载完成，但自动整理失败：{exc.Message}";
            task.OrganizedPath = "";
            return primary;
        }
    }

    private async Task SubtitleIfNeededAsync(DownloadTask? taskObj, string? video)
    {
        var task = taskObj!;
        if (!task.FetchSubtitle)
        {
            task.SubtitleStatus = "skipped";
            return;
        }
        if (string.IsNullOrEmpty(video) || !File.Exists(video))
        {
            task.SubtitleStatus = "failed";
            task.SubtitleNote = "找不到视频文件，跳过字幕匹配";
            return;
        }
        if (!MediaExts.Contains(Path.GetExtension(video).ToLowerInvariant()))
        {
            task.SubtitleStatus = "skipped";
            task.SubtitleNote = "非视频文件，跳过字幕匹配";
            return;
        }
        task.SubtitleStatus = "searching";
        _dirty = true;
        Raise(task);
        var result = await Subtitle.FetchForVideoAsync(video, task.Title, task.Year, task.Quality);
        if (result.Ok)
        {
            task.SubtitleStatus = "done";
            task.SubtitlePath = result.Files.Count > 0 ? result.Files[0] : "";
            task.SubtitleNote = result.Message;
        }
        else
        {
            task.SubtitleStatus = "failed";
            task.SubtitlePath = "";
            task.SubtitleNote = result.Message;
        }
        _dirty = true;
        Raise(task);
    }

    // ---------- 暂停 / 继续 / 删除 ----------
    public async Task PauseTaskAsync(DownloadTask task)
    {
        if (task.Engine != "aria2" || task.Gid.Length == 0)
        {
            task.Error = "该任务不支持暂停（仅 aria2 的磁力/种子任务可暂停）";
            Raise(task);
            return;
        }
        try
        {
            await Aria2.CallAsync("aria2.pause", new object[] { task.Gid }, _cts.Token);
            task.Status = "paused";
            task.DownloadSpeed = "";
            task.Error = "";
            task.UpdatedAt = DownloadTask.Now();
            _dirty = true;
            Persist(force: true);
            Raise(task);
        }
        catch (Exception exc)
        {
            task.Error = FriendlyAria2Error(exc.Message);
            Raise(task);
        }
    }

    public async Task ResumeTaskAsync(DownloadTask task)
    {
        if (task.Engine != "aria2" || task.Gid.Length == 0)
        {
            task.Error = "该任务不支持续传（仅 aria2 的磁力/种子任务可续传）";
            Raise(task);
            return;
        }

        // HTTP 直链：aria2 的 unpause 有已知 bug（会报 “No URI available.” 并变 error），
        // 因此直接重新添加同一链接 + continue=true 断点续传。
        var low = (task.Url ?? "").ToLowerInvariant();
        if (low.StartsWith("http://") || low.StartsWith("https://"))
        {
            if (await TryHttpResumeAsync(task, force: true))
            {
                Persist(force: true);
                Raise(task);
                return;
            }
        }

        try
        {
            await Aria2.CallAsync("aria2.unpause", new object[] { task.Gid }, _cts.Token);
            task.Status = "active";
            task.Error = "";
            task.UpdatedAt = DownloadTask.Now();
            _dirty = true;
            Persist(force: true);
            Raise(task);
        }
        catch (Exception exc)
        {
            var message = exc.Message;
            task.Error = FriendlyAria2Error(message);
            if (message.ToLowerInvariant().Contains("no uri") && await TryHttpResumeAsync(task, force: true))
            {
                Raise(task);
                return;
            }
            Raise(task);
        }
    }

    // ---------- 删除 ----------
    /// <summary>允许删除的根目录（防止误删配置目录以外的文件）。</summary>
    private List<string> AllowedRoots()
    {
        var roots = new List<string>();
        foreach (var raw in new[] { Config.DownloadRoot(), Config.LibraryRoot() })
        {
            try { roots.Add(Path.GetFullPath(raw)); }
            catch (Exception) { continue; }
        }
        return roots;
    }

    private bool InsideRoots(string path)
    {
        string target;
        try { target = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        foreach (var root in AllowedRoots())
        {
            if (string.Equals(target, root, StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>删除任务产生的文件（仅限下载目录/资料库目录内）。返回已删路径。</summary>
    private List<string> DeleteTaskFiles(DownloadTask task)
    {
        var removed = new List<string>();
        var targets = new List<string>();
        if (task.IncomingDir.Length > 0 && Config.Downloader.PerTaskDir)
            targets.Add(task.IncomingDir);
        if (task.OrganizedPath.Length > 0)
            targets.Add(task.OrganizedPath);
        if (task.SubtitlePath.Length > 0)
            targets.Add(task.SubtitlePath);

        foreach (var path in targets)
        {
            if (!InsideRoots(path)) continue;
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    removed.Add(path);
                    continue;
                }
                if (!File.Exists(path)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (MediaExts.Contains(ext))
                {
                    foreach (var sub in SiblingSubtitles(path))
                    {
                        try { File.Delete(sub); removed.Add(sub); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                File.Delete(path);
                removed.Add(path);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }
        return removed;
    }

    private static List<string> SiblingSubtitles(string video)
    {
        var subExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".ass", ".srt", ".ssa", ".sup", ".sub", ".vtt" };
        var output = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(video);
            if (string.IsNullOrEmpty(dir)) return output;
            var stem = Path.GetFileNameWithoutExtension(video);
            foreach (var cand in Directory.EnumerateFiles(dir))
            {
                if (!subExts.Contains(Path.GetExtension(cand).ToLowerInvariant())) continue;
                var candStem = Path.GetFileNameWithoutExtension(cand);
                if (candStem == stem || candStem.StartsWith(stem + ".", StringComparison.Ordinal))
                    output.Add(cand);
            }
        }
        catch (Exception) { }
        return output;
    }

    /// <summary>从列表移除任务；deleteFiles=true 时同时删除下载文件/字幕（仅限配置目录内）。</summary>
    public async Task<RemoveTaskResult> RemoveTaskAsync(string taskId, bool deleteFiles = false)
    {
        if (!Tasks.TryGetValue(taskId, out var task))
            return new RemoveTaskResult(false, "任务不存在", new List<string>());

        if (task.Gid.Length > 0)
        {
            foreach (var method in new[] { "aria2.remove", "aria2.removeDownloadResult" })
            {
                try { await Aria2.CallAsync(method, new object[] { task.Gid }, _cts.Token); }
                catch (Exception) { /* aria2 里已不存在的任务直接忽略 */ }
            }
        }

        var removed = deleteFiles ? DeleteTaskFiles(task) : new List<string>();
        Tasks.TryRemove(taskId, out _);
        _dirty = true;
        Persist(force: true);
        return new RemoveTaskResult(true, taskId, removed);
    }

    /// <summary>批量清理：scope = completed / error / all。</summary>
    public async Task<RemoveTaskResult> ClearTasksAsync(string scope = "completed", bool deleteFiles = false)
    {
        List<DownloadTask> targets = scope switch
        {
            "completed" => Tasks.Values.Where(t => t.Status == "complete").ToList(),
            "error" => Tasks.Values.Where(t => t.Status == "error").ToList(),
            _ => Tasks.Values.ToList(),
        };
        var removedFiles = new List<string>();
        foreach (var task in targets)
        {
            var result = await RemoveTaskAsync(task.TaskId, deleteFiles);
            removedFiles.AddRange(result.RemovedFiles);
        }
        return new RemoveTaskResult(true, $"已清理 {targets.Count} 个任务", removedFiles, targets.Count);
    }

    /// <summary>手动重试字幕匹配（换关键词后再试）。</summary>
    public async Task RetrySubtitleAsync(DownloadTask task)
    {
        var target = task.OrganizedPath.Length > 0 && File.Exists(task.OrganizedPath)
            ? task.OrganizedPath
            : task.SavedPath.Length > 0 && File.Exists(task.SavedPath)
                ? task.SavedPath
                : "";
        string? video = null;
        if (target.Length > 0 && File.Exists(target))
        {
            video = target;
        }
        else
        {
            foreach (var raw in task.Files)
            {
                if (File.Exists(raw) && MediaExts.Contains(Path.GetExtension(raw).ToLowerInvariant()))
                {
                    video = raw;
                    break;
                }
            }
        }
        if (video == null)
        {
            task.SubtitleStatus = "failed";
            task.SubtitleNote = "找不到视频文件，无法匹配字幕";
            Raise(task);
            return;
        }
        task.FetchSubtitle = true;
        await SubtitleIfNeededAsync(task, video);
        task.UpdatedAt = DownloadTask.Now();
        Persist(force: true);
        Raise(task);
    }

    public async Task<Dictionary<string, object>> Aria2StatusAsync(CancellationToken ct = default)
    {
        var ok = await Aria2.IsAvailableAsync(ct);
        var info = new Dictionary<string, object>
        {
            ["available"] = ok,
            ["rpc_url"] = Config.Downloader.Aria2.RpcUrl,
        };
        if (ok)
        {
            var version = await Aria2.GetVersionAsync(ct);
            if (version.Length > 0) info["version"] = version;
        }
        return info;
    }

    private static string GetString(Dictionary<string, JsonElement> st, string key) =>
        st.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long GetLong(Dictionary<string, JsonElement> st, string key) =>
        st.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n) ? n : 0;

    private static long SafeFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private void Raise(DownloadTask task) => TaskChanged?.Invoke(task);

    public void Dispose()
    {
        _cts.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        catch (OperationCanceledException) { }
        Persist(force: true);
        _cts.Dispose();
    }
}
