namespace MovieDock.Core.Config;

public sealed class ServerConfig
{
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8090;
}

public sealed class LlmConfig
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 60;
}

public sealed class PathsConfig
{
    /// <summary>空串 = 使用 Windows 默认（用户「视频」库下的 MovieDock 目录）。</summary>
    public string DownloadRoot { get; set; } = "";
    /// <summary>任务状态落盘目录（重启不丢任务）；空串 = %LOCALAPPDATA%\MovieDock\data。</summary>
    public string StateDir { get; set; } = "";
}

/// <summary>出网设置：内置索引源 / 字幕站等外部请求共用。境内直连不稳时填本地代理。</summary>
public sealed class NetworkConfig
{
    public string Proxy { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class ProviderConfig
{
    public string Type { get; set; } = "";
    public bool Enabled { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Method { get; set; } = "GET";
    public Dictionary<string, string> Headers { get; set; } = new();
    /// <summary>provider 专属参数（qBittorrent 的 username/password/plugins、内置源的 sources/proxy/limit 等）。</summary>
    public Dictionary<string, object> Options { get; set; } = new();
}

public sealed class SearchConfig
{
    public List<ProviderConfig> Providers { get; set; } = new();
    /// <summary>是否按评分排序候选（HDR/DoVi/做种数/体积等）。</summary>
    public bool SortByScore { get; set; } = true;
}

public sealed class OrganizeConfig
{
    public bool Enabled { get; set; } = true;
    public string MovieDirTemplate { get; set; } = "{title} ({year})";
    public string FileNameTemplate { get; set; } = "{title} ({year}) - {quality}";
    /// <summary>剧集模板（识别到 S01E02 / 第2集 时使用）。</summary>
    public string SeriesDirTemplate { get; set; } = "{title} ({year})/Season {season}";
    public string SeriesFileNameTemplate { get; set; } = "{title} ({year}) - S{season}E{episode} - {quality}";
    public string Mode { get; set; } = "move"; // move | copy | hardlink
    public string UnknownYear { get; set; } = "未知年份";
    /// <summary>电影子目录名（相对下载根目录）。</summary>
    public string MoviesSubdir { get; set; } = "movies";
    /// <summary>资料库根目录：留空则用 download_root；填了就把整理结果直接落到该目录。</summary>
    public string LibraryRoot { get; set; } = "";
}

public sealed class Aria2Config
{
    public string RpcUrl { get; set; } = "http://127.0.0.1:6800/jsonrpc";
    public string RpcSecret { get; set; } = "";
    /// <summary>桌面模式：应用自己拉起 aria2（连接外部 RPC 时改 false）。</summary>
    public bool AutoStart { get; set; }
    /// <summary>指定 aria2c 可执行文件（留空则自动探测：程序目录 → PATH → 常见安装路径）。</summary>
    public string Binary { get; set; } = "";
    public int Port { get; set; } = 6800;
}

public sealed class DownloaderConfig
{
    public string Engine { get; set; } = "aria2";
    /// <summary>Windows 托管模式：managed=随应用拉起 aria2c | external=连接外部 RPC | none=仅 HTTP 直链。</summary>
    public string Aria2Mode { get; set; } = "managed";
    public Aria2Config Aria2 { get; set; } = new();
    public int MaxConcurrent { get; set; } = 3;
    public string CategoryDir { get; set; } = "incoming";
    /// <summary>每个任务使用独立子目录（incoming/&lt;task_id&gt;/），避免并发任务整理错文件。</summary>
    public bool PerTaskDir { get; set; } = true;
    /// <summary>aria2 附加选项（会合并进 addUri options）。</summary>
    public Dictionary<string, string> ExtraOptions { get; set; } = new();
    /// <summary>BT tracker 列表（逗号分隔），留空则不下发。</summary>
    public string BtTrackers { get; set; } = "";
}

/// <summary>自动中文字幕配置（当前内置 SubHD）。</summary>
public sealed class SubtitleConfig
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "subhd";
    /// <summary>找不到时的备用关键词（大陆/台湾译名差异，如「机器人总动员」）。</summary>
    public List<string> ExtraKeywords { get; set; } = new();
    /// <summary>字幕条目匹配提示词，留空则自动从片名/发布组提取。</summary>
    public string MatchHint { get; set; } = "";
    /// <summary>优先语言（用于排序）：简中 &gt; 双语 &gt; 繁中。</summary>
    public bool PreferBilingual { get; set; } = true;
    /// <summary>简体优先（大陆用户默认开）：同发布版本下优先选简体字幕。</summary>
    public bool PreferSimplified { get; set; } = true;
    /// <summary>重命名规则：{video} 为视频文件名（不含扩展名）。</summary>
    public string NameTemplate { get; set; } = "{video}.zh";
    /// <summary>rar/7z 解压工具（Windows 上自动探测 7-Zip 安装路径）。</summary>
    public List<string> ExtractTools { get; set; } = new() { "7z", "7zz", "bsdtar", "unrar", "unar" };
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>关键词全英文时，用大模型把片名翻译成中文再搜（SubHD 对英文名匹配很差）。</summary>
    public bool LlmTranslate { get; set; } = true;
    /// <summary>每次关键词搜索合并前几部影片的字幕条目。</summary>
    public int MaxMovies { get; set; } = 2;
    /// <summary>下载字幕失败时标记「完成（无字幕）」而不是错误。</summary>
    public bool SoftFail { get; set; } = true;
}

public sealed class AppConfig
{
    public ServerConfig Server { get; set; } = new();
    public LlmConfig Llm { get; set; } = new();
    public PathsConfig Paths { get; set; } = new();
    public NetworkConfig Network { get; set; } = new();
    public SearchConfig Search { get; set; } = new();
    public OrganizeConfig Organize { get; set; } = new();
    public DownloaderConfig Downloader { get; set; } = new();
    public SubtitleConfig Subtitle { get; set; } = new();

    public static string DefaultDownloadRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "MovieDock");

    public static string DefaultStateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieDock", "data");

    public string DownloadRoot()
    {
        var raw = Paths.DownloadRoot?.Trim();
        if (string.IsNullOrEmpty(raw)) return DefaultDownloadRoot;
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
        }
        catch (ArgumentException)
        {
            return DefaultDownloadRoot;
        }
    }

    public string IncomingDir() => Path.Combine(DownloadRoot(), Downloader.CategoryDir);

    /// <summary>任务状态目录：配置值 → %LOCALAPPDATA%\MovieDock\data → %TEMP% 兜底。</summary>
    public string StateDir()
    {
        foreach (var raw in new[] { Paths.StateDir?.Trim(), DefaultStateDir })
        {
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                var p = Environment.ExpandEnvironmentVariables(raw);
                Directory.CreateDirectory(p);
                return Path.GetFullPath(p);
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
        }
        var fallback = Path.Combine(Path.GetTempPath(), "movie-dock-state");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>整理目标根目录：配置 library_root → 下载根目录/movies。</summary>
    public string LibraryRoot()
    {
        var raw = (Organize.LibraryRoot ?? "").Trim();
        if (raw.Length > 0)
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
        return Path.Combine(DownloadRoot(), string.IsNullOrEmpty(Organize.MoviesSubdir) ? "movies" : Organize.MoviesSubdir);
    }
}
