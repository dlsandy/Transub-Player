using System.Collections.Concurrent;
using System.Diagnostics;

namespace TransubPlayer.Platform;

/// <summary>macOS/Linux child-process tracking via process tree kill (no Job Objects).</summary>
internal sealed class UnixChildProcessPolicy : IChildProcessPolicy
{
    public static readonly TimeSpan HttpBudget = TimeSpan.FromSeconds(2);
    public const int ProcessWaitMs = 4000;
    public const int MpvQuitWaitMs = 800;

    private readonly ConcurrentDictionary<int, byte> _pids = new();
    private readonly ConcurrentBag<Process> _kept = new();

    public void PrepareStartInfo(ProcessStartInfo startInfo)
    {
        // New session so grandchildren stay in a killable tree when possible.
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            startInfo.UseShellExecute = false;
        }
        catch
        {
            // ignore
        }
    }

    public void Track(Process process)
    {
        if (process is null) return;
        try
        {
            try
            {
                if (process.HasExited) return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            process.EnableRaisingEvents = true;
            var pid = process.Id;
            _pids[pid] = 0;
            process.Exited += (_, _) => _pids.TryRemove(pid, out _);
        }
        catch (Exception ex)
        {
            Services.PlayerLog.Write("跟踪子进程失败：" + ex.Message);
        }
    }

    public void KeepAlive(Process process)
    {
        if (process is null) return;
        _kept.Add(process);
    }

    public void Stop(ref Process? process, int waitMs)
    {
        var p = process;
        process = null;
        if (p is null) return;
        try
        {
            var pid = 0;
            try { pid = p.Id; } catch { /* handle closed */ }
            if (pid != 0) _pids.TryRemove(pid, out _);
            var alive = false;
            try { alive = !p.HasExited; } catch (InvalidOperationException) { /* not started */ }
            if (alive)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(waitMs);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { p.Dispose(); } catch { /* ignore */ }
        }
    }

    public void StopAll()
    {
        foreach (var pid in _pids.Keys)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            _pids.TryRemove(pid, out _);
        }

        while (_kept.TryTake(out var kept))
        {
            try { kept.Dispose(); } catch { /* ignore */ }
        }
    }
}
