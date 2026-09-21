using System.Diagnostics;

namespace MovieDock.Core.Download;

/// <summary>
/// 托管本机 aria2c.exe：优先复用已在监听的 RPC；找不到可执行文件时返回不可用。
/// 应用退出时连带关闭自己拉起的进程。
/// </summary>
public sealed class Aria2ProcessHost : IAsyncDisposable
{
    private Process? _process;
    private bool _startedByUs;
    private bool _externalOk;

    /// <summary>我们拉起的进程还活着，或此前已确认有外部 aria2 RPC 可复用。</summary>
    public bool IsRunning => _process is { HasExited: false } || _externalOk;

    public static string? FindAria2Executable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        if (File.Exists(local)) return local;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "aria2c.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception) { /* PATH 里的异常条目直接跳过 */ }
        }
        return null;
    }

    /// <summary>确保 aria2 RPC 可用：已有实例直接复用，否则启动子进程并等待就绪。</summary>
    public async Task<bool> EnsureRunningAsync(Aria2Client probe, string incomingDir, string? secret = null, int port = 6800, CancellationToken ct = default)
    {
        if (await probe.IsAvailableAsync(ct))
        {
            _startedByUs = false;
            _externalOk = true;
            return true;
        }

        if (_process is { HasExited: false } && _startedByUs)
        {
            // 已拉起过但 RPC 尚未就绪：继续等同一个进程，避免重复启动并把旧进程句柄弄丢
            return await WaitReadyAsync(probe, ct);
        }

        var exe = FindAria2Executable();
        if (exe == null) return false;

        var args =
            $"--enable-rpc --rpc-listen-port={port} " +
            $"--dir=\"{incomingDir}\" " +
            "--seed-time=0 --continue=true --auto-file-renaming=true " +
            "--allow-overwrite=false --file-allocation=none --console-log-level=warn";
        if (!string.IsNullOrEmpty(secret)) args += $" --rpc-secret=\"{secret}\"";

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 刻意不重定向 stdout/stderr：从不读取的重定向管道被写满后，aria2 会阻塞挂起
        };
        _process = Process.Start(psi);
        _startedByUs = true;

        return await WaitReadyAsync(probe, ct);
    }

    private async Task<bool> WaitReadyAsync(Aria2Client probe, CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            if (_process is { HasExited: true }) return false;
            if (await probe.IsAvailableAsync(ct)) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process != null && _startedByUs && !_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception) { /* 进程可能已自行退出 */ }
        }
        _process?.Dispose();
        _process = null;
        _externalOk = false;
        await ValueTask.CompletedTask;
    }
}
