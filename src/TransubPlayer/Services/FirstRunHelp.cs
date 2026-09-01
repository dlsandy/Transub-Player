using System.Diagnostics;

namespace TransubPlayer.Services;

/// <summary>Actionable first-run recovery: fetch mpv, open Transub site.</summary>
internal static class FirstRunHelp
{
    public const string TransubSite = "https://www.transub.cc";

    public static string? FindFetchMpvScript()
    {
        foreach (var path in ScriptCandidates())
        {
            if (File.Exists(path))
                return Path.GetFullPath(path);
        }

        return null;
    }

    public static bool OpenTransubSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = TransubSite, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RunFetchMpvAsync(Action<string> log, CancellationToken ct)
    {
        var script = FindFetchMpvScript()
            ?? throw new InvalidOperationException("未找到 tools\\fetch-mpv.ps1，且安装包内没有 mpv。");

        log("正在下载 mpv…");
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);

        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = start, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log(e.Data);
            };

            if (!proc.Start())
                throw new InvalidOperationException("无法启动 PowerShell 下载 mpv。");

            ChildProcessLifetime.Track(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"下载 mpv 失败（退出码 {proc.ExitCode}）。");
        }
        catch (OperationCanceledException)
        {
            ChildProcessLifetime.Stop(ref proc);
            throw;
        }
        finally
        {
            var p = proc;
            if (p is null) { }
            else
            {
                try
                {
                    if (!p.HasExited)
                        ChildProcessLifetime.Stop(ref proc);
                    else
                        p.Dispose();
                }
                catch
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                }
            }
        }
    }

    private static IEnumerable<string> ScriptCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "tools", "fetch-mpv.ps1");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "fetch-mpv.ps1"));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "fetch-mpv.ps1"));
    }
}

internal sealed class MpvMissingException : InvalidOperationException
{
    public MpvMissingException()
        : base("未找到播放组件 mpv。安装包应自带 mpv；开发环境可运行 tools\\fetch-mpv.ps1。")
    {
    }
}
