using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MovieDock.Core.Subtitle;

/// <summary>
/// 字幕压缩包解压 + 文本编码规整。自 Python 版 app/subtitle/extract.py + runtime.py find_tool 移植。
/// SubHD 的下载包可能是 zip，也可能是 rar（rar 用 zip 库打开会抛异常），
/// 所以按 zip → 7z → bsdtar → unrar → unar 依次兜底。
/// </summary>
public static class ArchiveExtractor
{
    public static readonly string[] SubExts = { ".ass", ".srt", ".ssa", ".sup", ".sub", ".idx", ".vtt" };

    private static readonly Regex CjkRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    static ArchiveExtractor()
    {
        // GBK / gb18030 / Big5 等 Windows 代码页解码需要注册提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Windows 常见安装路径（名称 → 路径列表），对应 Python runtime._TOOL_HINTS。</summary>
    private static readonly Dictionary<string, string[]> ToolHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["7z"] = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
        },
        ["unrar"] = new[]
        {
            @"C:\Program Files\WinRAR\UnRAR.exe",
            @"C:\Program Files\WinRAR\unrar.exe",
        },
        ["bsdtar"] = Array.Empty<string>(),
        ["unar"] = Array.Empty<string>(),
    };

    /// <summary>备选可执行名，对应 Python runtime._TOOL_ALIASES。</summary>
    private static readonly Dictionary<string, string[]> ToolAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["7z"] = new[] { "7z", "7zz", "7za" },
        ["aria2c"] = new[] { "aria2c" },
        ["unrar"] = new[] { "unrar", "UnRAR" },
        ["bsdtar"] = new[] { "bsdtar", "tar" },
        ["unar"] = new[] { "unar", "lsar" },
    };

    /// <summary>探测外部工具可执行路径：程序目录 → PATH → 常见安装路径。</summary>
    public static string? FindTool(string name, string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            if (File.Exists(overridePath)) return overridePath;
            var which = FindInPath(overridePath);
            if (which != null) return which;
        }

        // 程序目录（随包二进制）
        foreach (var alias in AliasesOf(name))
        {
            var local = Path.Combine(AppContext.BaseDirectory, alias.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? alias : alias + ".exe");
            if (File.Exists(local)) return local;
            var localPlain = Path.Combine(AppContext.BaseDirectory, alias);
            if (File.Exists(localPlain)) return localPlain;
        }
        // PATH
        foreach (var alias in AliasesOf(name))
        {
            var found = FindInPath(alias);
            if (found != null) return found;
        }
        // 常见安装路径
        if (ToolHints.TryGetValue(name, out var hints))
        {
            foreach (var hint in hints)
                if (File.Exists(hint)) return hint;
        }
        return null;
    }

    private static string[] AliasesOf(string name) =>
        ToolAliases.TryGetValue(name, out var aliases) ? aliases : new[] { name };

    private static string? FindInPath(string fileName)
    {
        if (fileName.Contains(Path.DirectorySeparatorChar) || fileName.Contains(Path.AltDirectorySeparatorChar))
            return null;
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
                if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(dir.Trim(), fileName + ".exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception) { /* PATH 里的异常条目直接跳过 */ }
        }
        return null;
    }

    /// <summary>zip 里非 UTF-8 标记的中文文件名会变成乱码，这里尝试按 GBK 还原。</summary>
    private static string FixZipName(string name)
    {
        if (name.Length == 0) return name;
        // 纯 ASCII 无需处理；已经含 CJK 说明解码正确
        var hasNonAscii = false;
        foreach (var c in name)
        {
            if (c > 127) { hasNonAscii = true; break; }
        }
        if (!hasNonAscii || CjkRegex.IsMatch(name)) return name;
        try
        {
            // 乱码名按 Latin-1（≈cp437 的可打印区）取回原始字节，再按 GBK 解码
            var bytes = Encoding.Latin1.GetBytes(name);
            var fixedName = Encoding.GetEncoding("GBK").GetString(bytes);
            return CjkRegex.IsMatch(fixedName) ? fixedName : name;
        }
        catch (Exception)
        {
            return name;
        }
    }

    private static bool ExtractZip(string archive, string outDir)
    {
        if (!IsZipFile(archive)) return false;
        using var z = ZipFile.OpenRead(archive);
        var outRoot = Path.GetFullPath(outDir);
        foreach (var info in z.Entries)
        {
            var fixedName = FixZipName(info.FullName.Replace('\\', '/'));
            var target = Path.GetFullPath(Path.Combine(outDir, fixedName));
            // 防止路径穿越
            if (!target.StartsWith(outRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (info.FullName.EndsWith('/') || info.FullName.EndsWith('\\') || string.IsNullOrEmpty(info.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            info.ExtractToFile(target, overwrite: true);
        }
        return true;
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            // PK 魔数（3\4）
            using var fs = File.OpenRead(path);
            var head = new byte[4];
            return fs.Read(head, 0, 4) == 4 && head[0] == 0x50 && head[1] == 0x4B;
        }
        catch (Exception) { return false; }
    }

    private static (bool Ok, string Output) RunTool(string tool, string archive, string outDir)
    {
        var name = Path.GetFileNameWithoutExtension(tool).ToLowerInvariant();
        string[] cmd;
        if (name is "7z" or "7zz" or "7za")
            cmd = new[] { tool, "x", "-y", $"-o{outDir}", archive };
        else if (name is "bsdtar" or "tar")
            cmd = new[] { tool, "-xf", archive, "-C", outDir };
        else if (name is "unrar" or "unrar-free")
            cmd = new[] { tool, "x", "-y", archive, outDir.EndsWith(Path.DirectorySeparatorChar) ? outDir : outDir + Path.DirectorySeparatorChar };
        else if (name == "unar")
            cmd = new[] { tool, "-o", outDir, archive };
        else
            return (false, $"不支持的工具：{tool}");

        try
        {
            var psi = new ProcessStartInfo(cmd[0])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // ArgumentList 必须逐个添加：拼成单个字符串会被整体当成一个参数，7z/unrar 无法识别
            foreach (var arg in cmd.Skip(1))
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "进程启动失败");
            // 先异步读两路输出再等退出：输出填满管道时子进程会阻塞写，先 WaitForExit 就是死锁
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120_000))
            {
                try { proc.Kill(); } catch (Exception) { }
                return (false, "超时");
            }
            var output = (stdoutTask.Result ?? "") + (stderrTask.Result ?? "");
            return (proc.ExitCode == 0, output);
        }
        catch (Exception exc)
        {
            return (false, exc.Message);
        }
    }

    /// <summary>解压归档，返回所有落盘文件（递归）。失败抛异常。</summary>
    public static List<string> ExtractArchive(string archive, string outDir, List<string>? tools = null)
    {
        Directory.CreateDirectory(outDir);
        if (!File.Exists(archive))
            throw new InvalidOperationException($"归档不存在：{archive}");

        if (ExtractZip(archive, outDir))
            return ListFilesRecursive(outDir);

        var errors = new List<string>();
        foreach (var tool in tools ?? new List<string> { "7z", "7zz", "bsdtar", "unrar", "unar" })
        {
            var path = FindTool(tool);
            if (path == null) continue;
            var (ok, output) = RunTool(path, archive, outDir);
            var files = ListFilesRecursive(outDir);
            if (ok && files.Count > 0)
                return files;
            var brief = output.Trim();
            errors.Add($"{tool}: {(brief.Length > 200 ? brief[..200] : brief.Length > 0 ? brief : "无输出")}");
        }
        throw new InvalidOperationException(
            "解压失败（非 zip 且外部工具不可用）：" +
            (errors.Count > 0 ? string.Join("；", errors) : "未找到 7z/bsdtar/unrar"));
    }

    private static List<string> ListFilesRecursive(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>挑出解压结果里的字幕文件。</summary>
    public static List<string> FindSubtitleFiles(string root) =>
        ListFilesRecursive(root)
            .Where(p => SubExts.Contains(Path.GetExtension(p).ToLowerInvariant()) &&
                        !Path.GetFileName(p).StartsWith("."))
            .ToList();

    /// <summary>把非 UTF-8 的字幕文本转成 UTF-8，返回使用的编码名。</summary>
    public static string NormalizeTextEncoding(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".ass" or ".srt" or ".ssa" or ".vtt"))
            return "";
        var raw = File.ReadAllBytes(path);
        // UTF-8 BOM
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return "utf-8";
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            _ = strict.GetString(raw);
            return "utf-8";
        }
        catch (DecoderFallbackException) { }
        foreach (var encName in new[] { "gb18030", "big5", "windows-1252" })
        {
            try
            {
                var enc = Encoding.GetEncoding(encName);
                var text = enc.GetString(raw);
                File.WriteAllText(path, text, new UTF8Encoding(false));
                return encName;
            }
            catch (Exception) { continue; }
        }
        return "";
    }
}
