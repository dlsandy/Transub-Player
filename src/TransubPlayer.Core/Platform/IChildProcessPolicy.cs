using System.Diagnostics;

namespace TransubPlayer.Platform;

/// <summary>
/// Tracks processes this app spawned (mpv / llama-server).
/// Windows uses a Job Object; macOS/Linux use process-group kill on exit.
/// </summary>
internal interface IChildProcessPolicy
{
    void Track(Process process);

    void KeepAlive(Process process);

    void Stop(ref Process? process, int waitMs);

    void StopAll();

    /// <summary>Optional start-info tweaks (e.g. new process group on Unix).</summary>
    void PrepareStartInfo(ProcessStartInfo startInfo);
}
