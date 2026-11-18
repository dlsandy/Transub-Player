using System.Diagnostics;
using System.Globalization;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal sealed record VideoClipResult(bool Ok, string Path, long SizeBytes, string? Error, string? Note = null);

/// <summary>
/// Cut a local-file time range.
/// Prefer ffmpeg stream-copy when it is installed. The bundled mpv build has no
/// <c>copy</c> encoder, so the fallback re-encodes with libx264 + AAC.
/// </summary>
internal static class VideoClip
{
    public const double MinDurationSec = 0.5;

    public static string ClipsDir => AppPaths.ClipsDir;

    /// <summary>Folder the next clip would be written to: beside the video, or the fallback clips folder.</summary>
    public static string OutputDirectory(string? mediaPath)
        => TrySourceDirectory(mediaPath) ?? ClipsDir;

    public static string DefaultOutputPath(string mediaPath, double startSec, double endSec)
    {
        var stem = StreamRecord.SanitizeStem(Path.GetFileNameWithoutExtension(mediaPath));
        if (startSec > endSec) (startSec, endSec) = (endSec, startSec);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var beside = TrySourceDirectory(mediaPath);
        var dir = beside ?? ClipsDir;
        if (beside is null && !MediaSourceHelper.IsNonLocalMedia(mediaPath))
            PlayerLog.Write("原目录不可写，片段改存 " + dir);
        Directory.CreateDirectory(dir);
        var name = $"{stem}_{Compact(startSec)}-{Compact(endSec)}_{stamp}.mkv";
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) return path;
        return Path.Combine(dir, $"{stem}_{Compact(startSec)}-{Compact(endSec)}_{stamp}_{Guid.NewGuid().ToString("N")[..4]}.mkv");
    }

    private static string? TrySourceDirectory(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || MediaSourceHelper.IsNonLocalMedia(mediaPath))
            return null;
        string full;
        try { full = Path.GetFullPath(mediaPath); }
        catch { return null; }
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) || !CanCreateFile(dir))
            return null;
        return dir;
    }

    private static bool CanCreateFile(string dir)
    {
        var probe = Path.Combine(dir, ".transub-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var _ = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch
            {
                // DeleteOnClose already removed it, or the folder rejected the probe.
            }
        }
    }

    public static async Task<VideoClipResult> ExtractAsync(
        string mediaPath,
        double startSec,
        double endSec,
        string outputPath,
        CancellationToken ct = default)
    {
        if (endSec < startSec) (startSec, endSec) = (endSec, startSec);
        var length = endSec - startSec;
        if (length < MinDurationSec)
            return new VideoClipResult(false, outputPath, 0, Loc.Get("Main.Status.ClipTooShort"));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var ffmpeg = StreamRecord.FindFfmpeg();
        if (ffmpeg is not null)
        {
            TryDelete(outputPath);
            var copy = await RunFfmpegCopyAsync(ffmpeg, mediaPath, startSec, length, outputPath, ct)
                .ConfigureAwait(false);
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (copy.Ok)
                return Ok(outputPath, note: null);

            TryDelete(outputPath);
            PlayerLog.Write("ffmpeg 截取失败，改用转码：" + copy.Error);
        }

        var mpv = MpvLocator.Find();
        if (mpv is null)
            return new VideoClipResult(false, outputPath, 0, Loc.Get("Main.Status.MpvMissing"));

        TryDelete(outputPath);
        var encoded = await RunMpvEncodeAsync(mpv, mediaPath, startSec, endSec, outputPath, includeAudio: true, ct)
            .ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (encoded.Ok)
            return Ok(outputPath, Loc.Get("Main.Clip.Reencoded"));

        TryDelete(outputPath);
        var videoOnly = await RunMpvEncodeAsync(mpv, mediaPath, startSec, endSec, outputPath, includeAudio: false, ct)
            .ConfigureAwait(false);
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (videoOnly.Ok)
            return Ok(outputPath, Loc.Get("Main.Clip.Reencoded"));

        TryDelete(outputPath);
        var detail = videoOnly.Error ?? encoded.Error;
        PlayerLog.Write("截取失败：" + detail);
        return new VideoClipResult(false, outputPath, 0, TrimError(detail));
    }

    private static VideoClipResult Ok(string path, string? note)
    {
        var size = new FileInfo(path).Length;
        PlayerLog.Write(string.IsNullOrWhiteSpace(note)
            ? $"片段已保存 {path} ({size} B)"
            : $"片段已保存 {path} ({size} B) {note}");
        return new VideoClipResult(true, path, size, null, note);
    }

    private static async Task<(bool Ok, string? Error)> RunMpvEncodeAsync(
        string mpv,
        string mediaPath,
        double startSec,
        double endSec,
        string outputPath,
        bool includeAudio,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = mpv,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(mpv) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--idle=no");
        psi.ArgumentList.Add("--force-window=no");
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--hwdec=no");
        psi.ArgumentList.Add("--no-sub");
        psi.ArgumentList.Add("--start=" + startSec.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--end=" + endSec.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--o=" + outputPath);
        psi.ArgumentList.Add("--of=matroska");
        psi.ArgumentList.Add("--ovc=libx264");
        psi.ArgumentList.Add("--ovcopts-add=preset=veryfast");
        psi.ArgumentList.Add("--ovcopts-add=crf=20");
        if (includeAudio)
        {
            psi.ArgumentList.Add("--oac=aac");
            psi.ArgumentList.Add("--oacopts-add=b=160k");
        }
        else
        {
            psi.ArgumentList.Add("--no-audio");
        }

        psi.ArgumentList.Add(mediaPath);

        var (code, err) = await RunProcessAsync(psi, ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            return (true, null);
        return (false, string.IsNullOrWhiteSpace(err) ? Loc.Get("Main.Clip.Failed") : err);
    }

    private static async Task<(bool Ok, string? Error)> RunFfmpegCopyAsync(
        string ffmpeg,
        string mediaPath,
        double startSec,
        double lengthSec,
        string outputPath,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ffmpeg) ?? AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(startSec.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(lengthSec.ToString("0.###", CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0?");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a:0?");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-avoid_negative_ts");
        psi.ArgumentList.Add("make_zero");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("matroska");
        psi.ArgumentList.Add(outputPath);

        var (code, err) = await RunProcessAsync(psi, ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            return (true, null);
        return (false, string.IsNullOrWhiteSpace(err) ? Loc.Get("Main.Clip.Failed") : err);
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        Process? proc = null;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException(Loc.Get("Main.Clip.Failed"));
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

            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                throw;
            }

            var err = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, err);
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

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static string Compact(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}h{ts.Minutes:00}m{ts.Seconds:00}s"
            : $"{ts.Minutes:00}m{ts.Seconds:00}s";
    }

    private static string TrimError(string? err)
    {
        if (string.IsNullOrWhiteSpace(err)) return Loc.Get("Main.Clip.Failed");
        var line = err.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(static l => l.Length > 0) ?? "";
        if (line.Length > 180) line = line[..180];
        return string.IsNullOrWhiteSpace(line) ? Loc.Get("Main.Clip.Failed") : line;
    }
}
