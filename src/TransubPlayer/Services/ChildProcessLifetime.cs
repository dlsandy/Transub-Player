using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TransubPlayer.Services;

/// <summary>
/// Tracks processes this app spawned (mpv / llama-server).
/// A Windows job with KILL_ON_JOB_CLOSE reaps them if the player exits abnormally.
/// Do not track already-running Transub services we only connected to.
/// </summary>
internal static class ChildProcessLifetime
{
    public static readonly TimeSpan HttpBudget = TimeSpan.FromSeconds(2);
    public const int ProcessWaitMs = 4000;
    public const int MpvQuitWaitMs = 800;

    private const int JobObjectInfoClassExtendedLimit = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly ConcurrentDictionary<int, byte> Pids = new();
    private static readonly ConcurrentBag<Process> Kept = new();
    private static readonly object JobGate = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _jobClosed;

    public static void Track(Process process)
    {
        if (process is null) return;
        try
        {
            if (process.HasExited) return;
            process.EnableRaisingEvents = true;
            var pid = process.Id;
            Pids[pid] = 0;
            process.Exited += (_, _) => Pids.TryRemove(pid, out _);
            AssignToJob(process);
        }
        catch (Exception ex)
        {
            PlayerLog.Write("跟踪子进程失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Keep redirected IO handles alive without killing. Call when another owner
    /// (or app exit) will stop the process later.
    /// </summary>
    public static void KeepAlive(Process process)
    {
        if (process is null) return;
        Kept.Add(process);
    }

    public static void Stop(ref Process? process, int waitMs = ProcessWaitMs)
    {
        var p = process;
        process = null;
        if (p is null) return;
        try
        {
            var pid = 0;
            try { pid = p.Id; } catch { /* handle closed */ }
            if (pid != 0) Pids.TryRemove(pid, out _);
            if (!p.HasExited)
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

    /// <summary>Last-resort: kill remaining spawned processes and close the job (kills grandchildren).</summary>
    public static void KillRemaining()
    {
        foreach (var pid in Pids.Keys)
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

            Pids.TryRemove(pid, out _);
        }

        while (Kept.TryTake(out var kept))
        {
            try { kept.Dispose(); } catch { /* ignore */ }
        }

        CloseJob();
    }

    private static void AssignToJob(Process process)
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

    private static IntPtr EnsureJob()
    {
        lock (JobGate)
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

    private static void CloseJob()
    {
        lock (JobGate)
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
