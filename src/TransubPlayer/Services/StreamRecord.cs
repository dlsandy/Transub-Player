using System.Diagnostics;
using System.Text;

namespace TransubPlayer.Services;

internal sealed record StreamRecordStopResult(bool Ok, string Path, long SizeBytes, string? Error);

/// <summary>
/// Live stream recording helpers.
/// mpv <c>stream-record</c> writes a raw MPEG-TS dump that often lacks clean timestamps;
/// finalize remuxes it to MP4 (or a repaired TS) so Transub Player / common players can open it.
/// </summary>
internal static class StreamRecord
{
    public static string RecordingsDir => AppPaths.RecordingsDir;

    public static string SanitizeStem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "stream";
        var stem = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, '_');
        stem = stem.Replace('/', '_').Replace('\\', '_');
        if (stem.Length > 80) stem = stem[..80];
        return string.IsNullOrWhiteSpace(stem) ? "stream" : stem;
    }

    public static string DefaultOutputPath(string displayName)
    {
        var stem = SanitizeStem(displayName);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(RecordingsDir, $"{stem}_{stamp}.mp4");
    }

    public static string EnsureOutputExtension(string path)
    {
        path = path.Trim();
        if (string.IsNullOrEmpty(path)) return DefaultOutputPath("stream");
        var ext = Path.GetExtension(path);
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            return path;
        return path + ".mp4";
    }

    public static bool PrefersMp4(string path)
        => Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

    public static bool IsMpegTsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".m2ts", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".mts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Temp MPEG-TS path for mpv stream-record (under cache, not next to the final file).
    /// </summary>
    public static string TempTsPath(string finalOutputPath)
    {
        var dir = Path.Combine(AppPaths.CacheDir, "stream-record");
        Directory.CreateDirectory(dir);
        var stem = SanitizeStem(Path.GetFileNameWithoutExtension(finalOutputPath));
        var unique = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(dir, $"{stem}_{unique}.recording.ts");
    }

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    public static string? FindFfmpeg()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppPaths.NativeRoot, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Transub", "_internal", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Transub", "ffmpeg", "ffmpeg.exe"),
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        try
        {
            var fromPath = Environment.GetEnvironmentVariable("PATH")?
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => Path.Combine(p.Trim().Trim('"'), "ffmpeg.exe"))
                .FirstOrDefault(File.Exists);
            if (fromPath is not null) return Path.GetFullPath(fromPath);
        }
        catch { /* ignore */ }

        return null;
    }

    /// <summary>Delete path with short retries (mpv may still hold the handle briefly).</summary>
    public static async Task TryDeleteAsync(string? path, int attempts = 8, int delayMs = 150)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                return;
            }
            catch
            {
                if (i + 1 >= attempts) return;
                try { await Task.Delay(delayMs).ConfigureAwait(false); } catch { /* ignore */ }
            }
        }
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Remux / repair raw stream-record TS into a single playable file.
    /// Prefer MP4; fall back to MKV, then a repaired TS. Always leaves at most one output.
    /// </summary>
    public static async Task<StreamRecordStopResult> FinalizeAsync(
        string rawTsPath,
        string desiredPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(rawTsPath) || new FileInfo(rawTsPath).Length <= 0)
            return new StreamRecordStopResult(false, desiredPath, 0, "录制文件为空。");

        desiredPath = EnsureOutputExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(ext))
        {
            var mp4Path = string.IsNullOrEmpty(ext)
                ? desiredPath + ".mp4"
                : desiredPath;
            var remux = await RemuxToMp4Async(rawTsPath, mp4Path, ct).ConfigureAwait(false);
            if (remux.Ok)
            {
                await TryDeleteAsync(rawTsPath).ConfigureAwait(false);
                return new StreamRecordStopResult(true, mp4Path, new FileInfo(mp4Path).Length, null);
            }

            TryDelete(mp4Path);

            var mkvPath = Path.ChangeExtension(mp4Path, ".mkv");
            var mkv = await RemuxToMkvAsync(rawTsPath, mkvPath, ct).ConfigureAwait(false);
            if (mkv.Ok)
            {
                await TryDeleteAsync(rawTsPath).ConfigureAwait(false);
                TryDelete(mp4Path);
                return new StreamRecordStopResult(true, mkvPath, new FileInfo(mkvPath).Length,
                    "MP4 封装失败，已保存为 MKV。");
            }

            TryDelete(mkvPath);

            var repairedTs = Path.ChangeExtension(mp4Path, ".ts");
            var fix = await RepairTsAsync(rawTsPath, repairedTs, ct).ConfigureAwait(false);
            if (fix.Ok)
            {
                await TryDeleteAsync(rawTsPath).ConfigureAwait(false);
                TryDelete(mp4Path);
                TryDelete(mkvPath);
                return new StreamRecordStopResult(true, repairedTs, new FileInfo(repairedTs).Length,
                    remux.Error ?? "未能封装为 MP4，已保存为可播放的 TS。");
            }

            TryDelete(repairedTs);
            TryDelete(mp4Path);
            TryDelete(mkvPath);
            return await KeepAsTsAsync(rawTsPath, mp4Path, remux.Error).ConfigureAwait(false);
        }

        if (IsMpegTsPath(desiredPath))
        {
            var fix = await RepairTsAsync(rawTsPath, desiredPath, ct).ConfigureAwait(false);
            if (fix.Ok)
            {
                await TryDeleteAsync(rawTsPath).ConfigureAwait(false);
                return new StreamRecordStopResult(true, desiredPath, new FileInfo(desiredPath).Length, null);
            }

            TryDelete(desiredPath);
            return await KeepAsTsAsync(rawTsPath, desiredPath, fix.Error).ConfigureAwait(false);
        }

        if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            var mkv = await RemuxToMkvAsync(rawTsPath, desiredPath, ct).ConfigureAwait(false);
            if (mkv.Ok)
            {
                await TryDeleteAsync(rawTsPath).ConfigureAwait(false);
                return new StreamRecordStopResult(true, desiredPath, new FileInfo(desiredPath).Length, null);
            }

            TryDelete(desiredPath);
        }

        return await KeepAsTsAsync(rawTsPath, desiredPath, "封装失败。").ConfigureAwait(false);
    }

    private static Task<(bool Ok, string? Error)> RemuxToMp4Async(string tsPath, string mp4Path, CancellationToken ct)
        => RemuxWithToolsAsync(tsPath, mp4Path, "mp4", ct);

    private static Task<(bool Ok, string? Error)> RemuxToMkvAsync(string tsPath, string mkvPath, CancellationToken ct)
        => RemuxWithToolsAsync(tsPath, mkvPath, "matroska", ct);

    private static Task<(bool Ok, string? Error)> RepairTsAsync(string srcTs, string dstTs, CancellationToken ct)
        => RemuxWithToolsAsync(srcTs, dstTs, "mpegts", ct);

    private static async Task<(bool Ok, string? Error)> RemuxWithToolsAsync(
        string input,
        string output,
        string format,
        CancellationToken ct)
    {
        if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)
            && format.Equals("mpegts", StringComparison.OrdinalIgnoreCase))
        {
            // In-place repair via temp file.
            var tmp = output + ".fix.tmp";
            var r = await RemuxWithToolsAsync(input, tmp, format, ct).ConfigureAwait(false);
            if (!r.Ok)
            {
                TryDelete(tmp);
                return r;
            }

            try
            {
                TryDelete(output);
                File.Move(tmp, output);
                return (true, null);
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                return (false, ex.Message);
            }
        }

        TryDelete(output);

        var ffmpeg = FindFfmpeg();
        if (ffmpeg is not null)
        {
            var ff = await RunFfmpegRemuxAsync(ffmpeg, input, output, format, ct).ConfigureAwait(false);
            if (ff.Ok) return ff;
            TryDelete(output);
        }

        var mpv = MpvLocator.Find();
        if (mpv is not null)
        {
            var mv = await RunMpvRemuxAsync(mpv, input, output, format, ct).ConfigureAwait(false);
            if (mv.Ok) return mv;
            TryDelete(output);
            return mv;
        }

        TryDelete(output);
        return (false, ffmpeg is null
            ? "未找到 ffmpeg，且 mpv 封装失败。"
            : "封装失败。");
    }

    private static async Task<(bool Ok, string? Error)> RunFfmpegRemuxAsync(
        string ffmpeg,
        string input,
        string output,
        string format,
        CancellationToken ct)
    {
        // stream-record dumps often need genpts; AAC-in-TS needs aac_adtstoasc for MP4.
        var sb = new StringBuilder();
        sb.Append("-hide_banner -y -nostdin ");
        sb.Append("-fflags +genpts+discardcorrupt -probesize 10M -analyzeduration 10M ");
        sb.Append($"-i \"{input}\" -map 0 -c copy -avoid_negative_ts make_zero ");
        if (format.Equals("mp4", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-bsf:a aac_adtstoasc -f mp4 -movflags +faststart ");
        }
        else if (format.Equals("matroska", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("-f matroska ");
        }
        else
        {
            sb.Append("-f mpegts ");
        }

        sb.Append($"\"{output}\"");

        var (code, err) = await RunProcessAsync(ffmpeg, sb.ToString(), ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
            return (true, null);

        return (false, SummarizeFfmpegError(err) is { Length: > 0 } tip ? tip : "ffmpeg 封装失败。");
    }

    private static async Task<(bool Ok, string? Error)> RunMpvRemuxAsync(
        string mpv,
        string input,
        string output,
        string format,
        CancellationToken ct)
    {
        // Use a separate mpv process to remux; demux with genpts for broken live dumps.
        var of = format.Equals("mp4", StringComparison.OrdinalIgnoreCase) ? "mp4"
            : format.Equals("matroska", StringComparison.OrdinalIgnoreCase) ? "matroska"
            : "mpegts";

        var args =
            $"--no-config --idle=no --force-window=no --no-audio-display --really-quiet " +
            $"--demuxer-lavf-o=fflags=+genpts+discardcorrupt " +
            $"\"{input}\" " +
            $"-o \"{output}\" --of={of} --ovc=copy --oac=copy";

        var (code, err) = await RunProcessAsync(mpv, args, ct).ConfigureAwait(false);
        if (code == 0 && File.Exists(output) && new FileInfo(output).Length > 0)
            return (true, null);

        // AAC ADTS → MP4 often needs bitstream filter; try mkv-style path already separate.
        return (false, string.IsNullOrWhiteSpace(err) ? "mpv 封装失败。" : err.Trim().Split('\n')[^1].Trim());
    }

    private static async Task<(int ExitCode, string Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var proc = new Process { StartInfo = psi };
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) err.AppendLine(e.Data);
        };
        if (!proc.Start())
            return (-1, "无法启动进程。");

        ChildProcessLifetime.Track(proc);
        proc.BeginErrorReadLine();
        try { proc.BeginOutputReadLine(); } catch { /* ignore */ }

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, "已取消。");
        }

        return (proc.ExitCode, err.ToString());
    }

    private static async Task<StreamRecordStopResult> KeepAsTsAsync(string rawTsPath, string desiredPath, string? note)
    {
        var tsPath = IsMpegTsPath(desiredPath) ? desiredPath : Path.ChangeExtension(desiredPath, ".ts");
        // Drop failed remux siblings so the user only sees one file in the save folder.
        if (!IsMpegTsPath(desiredPath))
        {
            TryDelete(desiredPath);
            TryDelete(Path.ChangeExtension(desiredPath, ".mp4"));
            TryDelete(Path.ChangeExtension(desiredPath, ".mkv"));
        }

        try
        {
            if (!string.Equals(rawTsPath, tsPath, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tsPath);
                File.Move(rawTsPath, tsPath);
            }
        }
        catch
        {
            tsPath = rawTsPath;
        }

        await Task.CompletedTask;
        var size = File.Exists(tsPath) ? new FileInfo(tsPath).Length : 0;
        return new StreamRecordStopResult(true, tsPath, size,
            note ?? "已保存为原始 TS（部分播放器需用兼容模式打开）。");
    }

    private static string SummarizeFfmpegError(string stderr)
    {
        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                return line.Length > 200 ? line[..200] : line;
        }

        return lines.Length > 0 ? (lines[^1].Length > 200 ? lines[^1][..200] : lines[^1]) : "";
    }
}
