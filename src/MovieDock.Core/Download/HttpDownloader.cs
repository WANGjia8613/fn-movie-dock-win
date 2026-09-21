using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MovieDock.Core.Download;

public sealed record DownloadProgress(long Done, long Total, double SpeedBytesPerSec);

/// <summary>HTTP 直链下载（aria2 不可用时的降级路径），流式写盘 + 节流进度回调。</summary>
public static partial class HttpDownloader
{
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidCharsRegex();

    public static async Task<string> DownloadHttpToAsync(
        string url, string destDir, string preferredName = "",
        Action<DownloadProgress>? onProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var name = FilenameFromUrl(url, preferredName);
        var dest = Path.Combine(destDir, name);
        if (File.Exists(dest))
        {
            var stem = Path.GetFileNameWithoutExtension(dest);
            var ext = Path.GetExtension(dest);
            var i = 1;
            while (File.Exists(dest))
            {
                dest = Path.Combine(destDir, $"{stem}.{i}{ext}");
                i++;
            }
        }

        long done = 0, total = 0;
        double lastSpeed = 0;
        var lastReport = DateTimeOffset.MinValue;

        using var client = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long length) total = length;

        var startTimestamp = Stopwatch.GetTimestamp();
        await using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256, useAsync: true);
        var buffer = new byte[1024 * 256];
        int read;
        while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
            var speed = elapsed > 0 ? done / elapsed : 0;
            var now = DateTimeOffset.UtcNow;
            if (onProgress != null && ((now - lastReport).TotalSeconds > 0.5 || (total > 0 && done >= total)))
            {
                onProgress(new DownloadProgress(done, total, speed));
                lastReport = now;
                lastSpeed = speed;
            }
        }

        if (onProgress != null)
            onProgress(new DownloadProgress(done, total > 0 ? total : done, lastSpeed));
        return dest;
    }

    private static string FilenameFromUrl(string url, string preferred)
    {
        var name = "";
        try
        {
            name = Path.GetFileName(Uri.UnescapeDataString(new Uri(url).AbsolutePath));
        }
        catch (Exception) { /* 非法 URL 时退回 preferred */ }
        if (name.Length > 0) return name;
        if (!string.IsNullOrEmpty(preferred))
        {
            var safe = InvalidCharsRegex().Replace(preferred, "").Trim();
            return safe.Length > 0 ? safe : "download.bin";
        }
        return "download.bin";
    }
}
