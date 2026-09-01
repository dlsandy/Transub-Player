using System.Diagnostics;
using System.Globalization;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Extract 16 kHz mono PCM WAV via mpv (no bundled ffmpeg).</summary>
internal static class AsrAudioExtract
{
    public static async Task<double> ProbeDurationAsync(string mediaPath, CancellationToken ct)
    {
        var mpv = MpvLocator.Find()
            ?? throw new InvalidOperationException(Loc.Get("Main.Status.MpvMissing"));

        var psi = new ProcessStartInfo
        {
            FileName = mpv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add("--vo=null");
        psi.ArgumentList.Add("--ao=null");
        psi.ArgumentList.Add("--frames=0");
        psi.ArgumentList.Add("--get-property=duration");
        psi.ArgumentList.Add(mediaPath);

        var (exitCode, stdout, _) = await RunMpvAsync(psi, ct, readStdout: true).ConfigureAwait(false);
        var text = stdout.Trim();
        if (exitCode != 0)
            return 3600;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0
            ? sec
            : 3600;
    }

    public static async Task<string> ExtractWavAsync(
        string mediaPath,
        double startSec,
        double durationSec,
        CancellationToken ct)
    {
        var mpv = MpvLocator.Find()
            ?? throw new InvalidOperationException(Loc.Get("Main.Status.MpvMissing"));

        var wav = Path.Combine(
            Path.GetTempPath(),
            "transub-asr-" + Guid.NewGuid().ToString("N") + ".wav");

        var psi = new ProcessStartInfo
        {
            FileName = mpv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--really-quiet");
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--vo=null");
        psi.ArgumentList.Add("--ao=pcm");
        psi.ArgumentList.Add($"--ao-pcm-file={wav}");
        psi.ArgumentList.Add("--ao-pcm-waveheader=yes");
        psi.ArgumentList.Add("--af=aresample=16000,format=s16");
        psi.ArgumentList.Add("--audio-channels=mono");
        if (startSec > 0.05)
            psi.ArgumentList.Add($"--start={startSec.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (durationSec > 0)
            psi.ArgumentList.Add($"--length={durationSec.ToString("0.###", CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add(mediaPath);

        try
        {
            var (exitCode, _, err) = await RunMpvAsync(psi, ct, readStdout: false).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(wav) || new FileInfo(wav).Length < 1024)
            {
                TryDeleteTemp(wav);
                var detail = TrimMpvError(err);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? Loc.Get("AsrAudioExtract.Failed")
                    : Loc.Format("AsrAudioExtract.FailedDetail", detail));
            }

            return wav;
        }
        catch
        {
            TryDeleteTemp(wav);
            throw;
        }
    }

    public static void TryDeleteTemp(string? wavPath)
    {
        if (string.IsNullOrWhiteSpace(wavPath)) return;
        try
        {
            if (File.Exists(wavPath))
                File.Delete(wavPath);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Start headless mpv, track it for exit cleanup, and kill the tree on cancel/timeout.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunMpvAsync(
        ProcessStartInfo psi,
        CancellationToken ct,
        bool readStdout)
    {
        Process? proc = null;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException(Loc.Get("AsrAudioExtract.Failed"));
            ChildProcessLifetime.Track(proc);

            using var killReg = ct.Register(static state =>
            {
                try
                {
                    var p = (Process)state!;
                    if (!p.HasExited)
                        p.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already gone
                }
            }, proc);

            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;
            if (readStdout)
                stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            if (psi.RedirectStandardError)
                stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                throw;
            }

            var stdout = stdoutTask is null ? "" : await stdoutTask.ConfigureAwait(false);
            var stderr = stderrTask is null ? "" : await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, stdout, stderr);
        }
        finally
        {
            if (proc is not null)
            {
                TryKill(proc);
                try { proc.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    private static string TrimMpvError(string err)
    {
        if (string.IsNullOrWhiteSpace(err)) return "";
        var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("●", StringComparison.Ordinal))
            .Where(l => !l.StartsWith("AO:", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.StartsWith("[ao/pcm]", StringComparison.OrdinalIgnoreCase))
            .Take(3);
        return string.Join(" ", lines).Trim();
    }
}
