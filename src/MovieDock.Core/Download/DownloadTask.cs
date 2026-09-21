using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MovieDock.Core.Models;

namespace MovieDock.Core.Download;

/// <summary>单个下载任务的可观察状态（自 Python 版 manager.py DownloadTask 移植，含 0.4.1 新字段）。</summary>
public sealed class DownloadTask : INotifyPropertyChanged
{
    private string _gid = "";
    private string _status = "queued";
    private string _title = "";
    private string _url = "";
    private string _quality = "";
    private int? _year;
    private double _progress;
    private string _downloadSpeed = "";
    private string _totalLength = "";
    private string _completedLength = "";
    private List<string> _files = new();
    private string _savedPath = "";
    private string _organizedPath = "";
    private string _episode = "";
    private List<string> _tags = new();
    private string _subtitleStatus = "";
    private string _subtitlePath = "";
    private string _subtitleNote = "";
    private string _error = "";
    private string _updatedAt = Now();
    private string _engine = "aria2";
    private string _httpDest = "";
    private bool _fetchSubtitle = true;
    private string _incomingDir = "";
    private int _resumeAttempts;

    public string TaskId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string CreatedAt { get; set; } = Now();
    public OrganizeOptions OrganizeOpts { get; set; } = new();

    public string Gid { get => _gid; set => Set(ref _gid, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public string Quality { get => _quality; set => Set(ref _quality, value); }
    public int? Year { get => _year; set => Set(ref _year, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string DownloadSpeed { get => _downloadSpeed; set => Set(ref _downloadSpeed, value); }
    public string TotalLength { get => _totalLength; set => Set(ref _totalLength, value); }
    public string CompletedLength { get => _completedLength; set => Set(ref _completedLength, value); }
    public List<string> Files { get => _files; set => Set(ref _files, value); }
    public string SavedPath { get => _savedPath; set => Set(ref _savedPath, value); }
    public string OrganizedPath { get => _organizedPath; set => Set(ref _organizedPath, value); }
    /// <summary>剧集集号标签（S01E02），识别到才填。</summary>
    public string Episode { get => _episode; set => Set(ref _episode, value); }
    /// <summary>画质/特性标签（DoVi/HDR/REMUX…）。</summary>
    public List<string> Tags { get => _tags; set => Set(ref _tags, value); }
    public string SubtitleStatus { get => _subtitleStatus; set => Set(ref _subtitleStatus, value); }
    public string SubtitlePath { get => _subtitlePath; set => Set(ref _subtitlePath, value); }
    public string SubtitleNote { get => _subtitleNote; set => Set(ref _subtitleNote, value); }
    public string Error { get => _error; set => Set(ref _error, value); }
    public string UpdatedAt { get => _updatedAt; set => Set(ref _updatedAt, value); }
    public string Engine { get => _engine; set => Set(ref _engine, value); }
    public string HttpDest { get => _httpDest; set => Set(ref _httpDest, value); }
    /// <summary>是否自动匹配字幕（新建任务时可覆盖）。</summary>
    public bool FetchSubtitle { get => _fetchSubtitle; set => Set(ref _fetchSubtitle, value); }
    /// <summary>任务专属下载目录（per_task_dir 开启时记录，重启后删除任务仍能定位文件）。</summary>
    public string IncomingDir { get => _incomingDir; set => Set(ref _incomingDir, value); }
    /// <summary>HTTP 直链续传重试次数（aria2 unpause 有已知 bug，靠重新添加绕开）。</summary>
    public int ResumeAttempts { get => _resumeAttempts; set => Set(ref _resumeAttempts, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss");

    // ---------- 持久化 ----------
    private static readonly JsonSerializerOptions StateOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>导出为可 JSON 序列化的状态字典（对应 Python to_state）。</summary>
    public Dictionary<string, object?> ToState()
    {
        var opts = new Dictionary<string, object?>
        {
            ["title"] = OrganizeOpts.Title,
            ["year"] = OrganizeOpts.Year,
            ["quality"] = OrganizeOpts.Quality,
            ["enabled"] = OrganizeOpts.Enabled,
            ["mode"] = OrganizeOpts.Mode,
            ["library_root"] = OrganizeOpts.LibraryRoot,
            ["series_dir_template"] = OrganizeOpts.SeriesDirTemplate,
            ["series_file_template"] = OrganizeOpts.SeriesFileNameTemplate,
        };
        return new Dictionary<string, object?>
        {
            ["task_id"] = TaskId,
            ["gid"] = Gid,
            ["status"] = Status,
            ["title"] = Title,
            ["url"] = Url,
            ["quality"] = Quality,
            ["year"] = Year,
            ["progress"] = Progress,
            ["download_speed"] = DownloadSpeed,
            ["total_length"] = TotalLength,
            ["completed_length"] = CompletedLength,
            ["files"] = Files,
            ["saved_path"] = SavedPath,
            ["organized_path"] = OrganizedPath,
            ["episode"] = Episode,
            ["tags"] = Tags,
            ["subtitle_status"] = SubtitleStatus,
            ["subtitle_path"] = SubtitlePath,
            ["subtitle_note"] = SubtitleNote,
            ["error"] = Error,
            ["created_at"] = CreatedAt,
            ["updated_at"] = UpdatedAt,
            ["organize_opts"] = opts,
            ["engine"] = Engine,
            ["http_dest"] = HttpDest,
            ["fetch_subtitle"] = FetchSubtitle,
            ["incoming_dir"] = IncomingDir,
            ["resume_attempts"] = ResumeAttempts,
        };
    }

    /// <summary>从状态字典恢复任务（未知字段忽略，对应 Python _load_state 的 valid 过滤）。</summary>
    public static DownloadTask? FromState(Dictionary<string, object?> data)
    {
        if (data is null) return null;
        var taskId = data.TryGetValue("task_id", out var tid) ? tid?.ToString() : null;
        if (string.IsNullOrEmpty(taskId)) return null;

        var task = new DownloadTask { TaskId = taskId! };
        if (data.TryGetValue("gid", out var v)) task.Gid = v?.ToString() ?? "";
        if (data.TryGetValue("status", out var s)) task.Status = s?.ToString() ?? "queued";
        if (data.TryGetValue("title", out var t)) task.Title = t?.ToString() ?? "";
        if (data.TryGetValue("url", out var u)) task.Url = u?.ToString() ?? "";
        if (data.TryGetValue("quality", out var q)) task.Quality = q?.ToString() ?? "";
        if (data.TryGetValue("year", out var y))
        {
            if (y is JsonElement { ValueKind: JsonValueKind.Number } ye && ye.TryGetInt32(out var yi)) task.Year = yi;
            else if (y is int yInt) task.Year = yInt;
        }
        if (data.TryGetValue("progress", out var p))
        {
            if (p is JsonElement { ValueKind: JsonValueKind.Number } pe && pe.TryGetDouble(out var pd)) task.Progress = pd;
            else if (p is double pdv) task.Progress = pdv;
        }
        if (data.TryGetValue("download_speed", out var ds)) task.DownloadSpeed = ds?.ToString() ?? "";
        if (data.TryGetValue("total_length", out var tl)) task.TotalLength = tl?.ToString() ?? "";
        if (data.TryGetValue("completed_length", out var cl)) task.CompletedLength = cl?.ToString() ?? "";
        if (data.TryGetValue("files", out var f) && f is JsonElement { ValueKind: JsonValueKind.Array } fe)
            task.Files = fe.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        if (data.TryGetValue("saved_path", out var sp)) task.SavedPath = sp?.ToString() ?? "";
        if (data.TryGetValue("organized_path", out var op)) task.OrganizedPath = op?.ToString() ?? "";
        if (data.TryGetValue("episode", out var ep)) task.Episode = ep?.ToString() ?? "";
        if (data.TryGetValue("tags", out var tg) && tg is JsonElement { ValueKind: JsonValueKind.Array } tge)
            task.Tags = tge.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        if (data.TryGetValue("subtitle_status", out var ss)) task.SubtitleStatus = ss?.ToString() ?? "";
        if (data.TryGetValue("subtitle_path", out var subp)) task.SubtitlePath = subp?.ToString() ?? "";
        if (data.TryGetValue("subtitle_note", out var sn)) task.SubtitleNote = sn?.ToString() ?? "";
        if (data.TryGetValue("error", out var e)) task.Error = e?.ToString() ?? "";
        if (data.TryGetValue("created_at", out var ca) && !string.IsNullOrEmpty(ca?.ToString())) task.CreatedAt = ca!.ToString()!;
        if (data.TryGetValue("updated_at", out var ua) && !string.IsNullOrEmpty(ua?.ToString())) task.UpdatedAt = ua!.ToString()!;
        if (data.TryGetValue("engine", out var en)) task.Engine = en?.ToString() ?? "aria2";
        if (data.TryGetValue("http_dest", out var hd)) task.HttpDest = hd?.ToString() ?? "";
        if (data.TryGetValue("fetch_subtitle", out var fs))
        {
            if (fs is JsonElement { ValueKind: JsonValueKind.True }) task.FetchSubtitle = true;
            else if (fs is JsonElement { ValueKind: JsonValueKind.False }) task.FetchSubtitle = false;
        }
        if (data.TryGetValue("incoming_dir", out var id)) task.IncomingDir = id?.ToString() ?? "";
        if (data.TryGetValue("resume_attempts", out var ra) && ra is JsonElement { ValueKind: JsonValueKind.Number } rae && rae.TryGetInt32(out var rai))
            task.ResumeAttempts = rai;

        if (data.TryGetValue("organize_opts", out var oo) && oo is JsonElement { ValueKind: JsonValueKind.Object } ooe)
        {
            var o = task.OrganizeOpts;
            if (ooe.TryGetProperty("title", out var ot) && ot.ValueKind == JsonValueKind.String) o.Title = ot.GetString() ?? "";
            if (ooe.TryGetProperty("year", out var oy) && oy.ValueKind == JsonValueKind.Number && oy.TryGetInt32(out var oyi)) o.Year = oyi;
            if (ooe.TryGetProperty("quality", out var oq) && oq.ValueKind == JsonValueKind.String) o.Quality = oq.GetString() ?? "";
            if (ooe.TryGetProperty("enabled", out var oen) && oen.ValueKind is JsonValueKind.True or JsonValueKind.False) o.Enabled = oen.GetBoolean();
            if (ooe.TryGetProperty("mode", out var om) && om.ValueKind == JsonValueKind.String) o.Mode = om.GetString();
            if (ooe.TryGetProperty("library_root", out var olr) && olr.ValueKind == JsonValueKind.String) o.LibraryRoot = olr.GetString();
            if (ooe.TryGetProperty("series_dir_template", out var osd) && osd.ValueKind == JsonValueKind.String) o.SeriesDirTemplate = osd.GetString();
            if (ooe.TryGetProperty("series_file_template", out var osf) && osf.ValueKind == JsonValueKind.String) o.SeriesFileNameTemplate = osf.GetString();
        }
        return task;
    }

    /// <summary>序列化整份任务列表（对应 Python _persist 的 payload）。</summary>
    public static string SerializeState(IEnumerable<DownloadTask> tasks) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["saved_at"] = Now(),
            ["tasks"] = tasks.Select(t => t.ToState()).ToList(),
        }, StateOpts);
}

internal static class SizeFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string HumanSize(double? n)
    {
        if (n is null) return "";
        var v = n.Value;
        if (v < 0) return "";
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:F2} {Units[i]}";
    }
}
