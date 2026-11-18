using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TransubPlayer.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Platform.Windows;

/// <summary>Windows Job Object KILL_ON_JOB_CLOSE for spawned mpv / llama-server.</summary>
internal sealed class WindowsChildProcessPolicy : IChildProcessPolicy
{
    private const int JobObjectInfoClassExtendedLimit = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly ConcurrentDictionary<int, byte> _pids = new();
    private readonly ConcurrentBag<Process> _kept = new();
    private readonly object _jobGate = new();
    private IntPtr _job = IntPtr.Zero;
    private bool _jobClosed;

    public void PrepareStartInfo(ProcessStartInfo startInfo)
    {
        // Job Object covers the tree; no extra start flags required.
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
            AssignToJob(process);
        }
        catch (Exception ex)
        {
            PlayerLog.Write("跟踪子进程失败：" + ex.Message);
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

        CloseJob();
    }

    private void AssignToJob(Process process)
    {
        var job = EnsureJob();
        if (job == IntPtr.Zero) return;
        try
        {
            if (!AssignProcessToJobObject(job, process.Handle))
                PlayerLog.Write($"未能加入退出清理组 pid={process.Id} · 仍会在关闭时结束该进程");
        }
        catch (Exception ex)
        {
            PlayerLog.Write("加入退出清理组失败：" + ex.Message);
        }
    }

    private IntPtr EnsureJob()
    {
        lock (_jobGate)
        {
            if (_jobClosed) return IntPtr.Zero;
            if (_job != IntPtr.Zero) return _job;

            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return IntPtr.Zero;

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(handle, JobObjectInfoClassExtendedLimit, ptr, (uint)size))
                {
                    CloseHandle(handle);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            _job = handle;
            return _job;
        }
    }

    private void CloseJob()
    {
        lock (_jobGate)
        {
            _jobClosed = true;
            if (_job == IntPtr.Zero) return;
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
