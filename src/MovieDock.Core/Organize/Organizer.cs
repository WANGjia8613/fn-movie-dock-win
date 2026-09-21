using System.Runtime.InteropServices;
using MovieDock.Core.Config;
using MovieDock.Core.Models;

namespace MovieDock.Core.Organize;

/// <summary>
/// 下载完成后按模板整理文件：电影 → 「资料库根/片名 (年份)/」，
/// 剧集（识别到 S01E02 / 第2集）→ 「资料库根/片名 (年份)/Season N/」。
/// 自 Python 版 app/organizer/organize.py 移植。
/// </summary>
public sealed class Organizer(OrganizeConfig config, string downloadRoot, string? libraryRoot = null)
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".ts", ".m2ts",
        ".webm", ".rmvb", ".mpg", ".mpeg", ".iso",
    };

    private static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase) { "move", "copy", "hardlink" };

    public OrganizeConfig Config { get; } = config;
    public string DownloadRoot { get; } = downloadRoot;

    /// <summary>整理根目录：覆盖值 → 构造参数 → 配置 library_root → download_root/(movies_subdir)。</summary>
    public string ResolveLibraryRoot(string? overrideRoot = null)
    {
        var raw = (overrideRoot ?? "").Trim();
        if (raw.Length > 0) return raw;
        if (!string.IsNullOrEmpty(libraryRoot)) return libraryRoot;
        raw = (Config.LibraryRoot ?? "").Trim();
        if (raw.Length > 0) return raw;
        var subdir = string.IsNullOrWhiteSpace(Config.MoviesSubdir) ? "movies" : Config.MoviesSubdir;
        return Path.Combine(DownloadRoot, subdir);
    }

    /// <summary>
    /// 计算整理目标路径（不执行移动）。剧集集号优先取 episodeText，否则从文件名解析。
    /// </summary>
    public (string DestDir, string DestFile) BuildPaths(
        string title, int? year, string quality = "", string? sourcePath = null,
        string? episodeText = null, string? libraryRootOverride = null, string? ext = null)
    {
        var fileName = Path.GetFileName(sourcePath ?? "");
        var (season, episode) = NameParsing.ParseEpisode(
            string.IsNullOrEmpty(episodeText) ? fileName : episodeText);
        var e = string.IsNullOrEmpty(ext) ? Path.GetExtension(sourcePath ?? "") : ext;
        var root = ResolveLibraryRoot(libraryRootOverride);

        string folder, filename;
        if (season is not null || episode is not null)
        {
            folder = NameParsing.ApplyTemplate(
                string.IsNullOrWhiteSpace(Config.SeriesDirTemplate) ? Config.MovieDirTemplate : Config.SeriesDirTemplate,
                title, year, quality, season: season, episode: episode, unknownYear: Config.UnknownYear);
            filename = NameParsing.ApplyTemplate(
                string.IsNullOrWhiteSpace(Config.SeriesFileNameTemplate) ? Config.FileNameTemplate : Config.SeriesFileNameTemplate,
                title, year, quality, e, season, episode, Config.UnknownYear);
        }
        else
        {
            folder = NameParsing.ApplyTemplate(Config.MovieDirTemplate, title, year, quality,
                unknownYear: Config.UnknownYear);
            filename = NameParsing.ApplyTemplate(Config.FileNameTemplate, title, year, quality, e,
                unknownYear: Config.UnknownYear);
        }

        var destDir = Path.Combine(root, folder);
        if (e.Length > 0 && !filename.ToLowerInvariant().EndsWith(e.ToLowerInvariant()))
            filename += e;
        return (destDir, Path.Combine(destDir, filename));
    }

    public string OrganizeFile(string src, string title, int? year, string quality = "", OrganizeOptions? options = null)
    {
        if (!File.Exists(src))
            throw new FileNotFoundException($"待整理文件不存在：{src}");

        var opts = options ?? new OrganizeOptions();
        var enabled = opts.Enabled ?? Config.Enabled;
        if (!enabled) return src;

        var mode = ChooseMode(opts.Mode) ?? ChooseMode(Config.Mode) ?? "move";
        var movieDirTemplate = FirstNonEmpty(opts.MovieDirTemplate, Config.MovieDirTemplate);
        var fileNameTemplate = FirstNonEmpty(opts.FileNameTemplate, Config.FileNameTemplate);
        var seriesDirTemplate = FirstNonEmpty(opts.SeriesDirTemplate, Config.SeriesDirTemplate);
        var seriesFileTemplate = FirstNonEmpty(opts.SeriesFileNameTemplate, Config.SeriesFileNameTemplate);
        var unknownYear = FirstNonEmpty(opts.UnknownYear, Config.UnknownYear);

        // 集号从「标题 + 文件名」里一起识别：下载任务标题常自带 S01E02
        var (season, episode) = NameParsing.ParseEpisode($"{title} {Path.GetFileName(src)}");
        var root = ResolveLibraryRoot(opts.LibraryRoot);

        string folder, filename;
        if (season is not null || episode is not null)
        {
            folder = NameParsing.ApplyTemplate(
                string.IsNullOrWhiteSpace(seriesDirTemplate) ? movieDirTemplate : seriesDirTemplate,
                title, year, quality, season: season, episode: episode, unknownYear: unknownYear);
            filename = NameParsing.ApplyTemplate(
                string.IsNullOrWhiteSpace(seriesFileTemplate) ? fileNameTemplate : seriesFileTemplate,
                title, year, quality, Path.GetExtension(src), season, episode, unknownYear);
        }
        else
        {
            folder = NameParsing.ApplyTemplate(movieDirTemplate, title, year, quality, unknownYear: unknownYear);
            filename = NameParsing.ApplyTemplate(fileNameTemplate, title, year, quality, Path.GetExtension(src),
                unknownYear: unknownYear);
        }

        var destDir = Path.Combine(root, folder);
        Directory.CreateDirectory(destDir);

        var ext = Path.GetExtension(src);
        if (ext.Length > 0 && !filename.ToLowerInvariant().EndsWith(ext.ToLowerInvariant()))
            filename += ext;

        var dest = Path.Combine(destDir, filename);
        if (File.Exists(dest))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(src), StringComparison.OrdinalIgnoreCase))
                    return dest;
            }
            catch (Exception) { /* 路径解析失败则继续走重命名逻辑 */ }
            var stem = Path.GetFileNameWithoutExtension(dest);
            var i = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(destDir, $"{stem}.{i}{ext}");
                i++;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        switch (mode)
        {
            case "copy":
                File.Copy(src, dest);
                return dest;
            case "hardlink":
                try
                {
                    // .NET 8 未提供 File.CreateHardLink，直接调 kernel32
                    if (!CreateHardLinkW(dest, src, IntPtr.Zero))
                        throw new IOException($"硬链接创建失败（Win32 错误 {Marshal.GetLastWin32Error()}）");
                    return dest;
                }
                catch (IOException)
                {
                    // 跨卷等情况硬链接失败 → 退化为复制
                    File.Copy(src, dest);
                    return dest;
                }
            default:
                File.Move(src, dest);
                return dest;
        }
    }

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path));

    private static string? ChooseMode(string? mode) =>
        !string.IsNullOrEmpty(mode) && ValidModes.Contains(mode) ? mode : null;

    private static string FirstNonEmpty(string? a, string fallback) =>
        string.IsNullOrWhiteSpace(a) ? fallback : a!;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
