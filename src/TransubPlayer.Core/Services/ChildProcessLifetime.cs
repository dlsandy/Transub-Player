using System.Diagnostics;
using TransubPlayer.Platform;

namespace TransubPlayer.Services;

/// <summary>
/// Tracks processes this app spawned (mpv / llama-server).
/// Delegates to <see cref="PlatformServices.ChildProcesses"/> (Job Object on Windows, tree-kill on Mac).
/// </summary>
internal static class ChildProcessLifetime
{
    public static readonly TimeSpan HttpBudget = TimeSpan.FromSeconds(2);
    public const int ProcessWaitMs = 4000;
    public const int MpvQuitWaitMs = 800;

    public static void Track(Process process)
        => PlatformServices.ChildProcesses.Track(process);

    public static void KeepAlive(Process process)
        => PlatformServices.ChildProcesses.KeepAlive(process);

    public static void Stop(ref Process? process, int waitMs = ProcessWaitMs)
        => PlatformServices.ChildProcesses.Stop(ref process, waitMs);

    public static void KillRemaining()
        => PlatformServices.ChildProcesses.StopAll();

    public static void PrepareStartInfo(ProcessStartInfo startInfo)
        => PlatformServices.ChildProcesses.PrepareStartInfo(startInfo);
}
