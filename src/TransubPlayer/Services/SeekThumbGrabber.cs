using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;

namespace TransubPlayer.Services;

/// <summary>
/// Hover-scrub thumbnails via ffmpeg (when present). Quantizes time and caches recent frames.
/// Remote / capture sources are skipped.
/// </summary>
internal sealed class SeekThumbGrabber : IDisposable
{
    private const int ThumbWidth = 192;
    private const double BucketSec = 2.0;
    private const int MaxCache = 48;

    private readonly ConcurrentDictionary<long, BitmapImage> _cache = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private string? _mediaPath;
    private string? _ffmpeg;
    private string? _tempDir;
    private int _seq;

    public void BindMedia(string? mediaPath)
    {
        lock (_gate)
        {
            if (string.Equals(_mediaPath, mediaPath, StringComparison.OrdinalIgnoreCase))
                return;
            CancelLocked();
            ClearCacheLocked();
            _mediaPath = mediaPath;
            _ffmpeg = StreamRecord.FindFfmpeg();
            EnsureTempDir();
        }
    }

    public bool CanPreview
    {
        get
        {
            lock (_gate)
                return CanPreviewLocked();
        }
    }

    private bool CanPreviewLocked()
        => !string.IsNullOrWhiteSpace(_mediaPath)
           && !MediaSourceHelper.IsNonLocalMedia(_mediaPath)
           && File.Exists(_mediaPath)
           && !string.IsNullOrWhiteSpace(_ffmpeg);

    public async Task<BitmapImage?> GrabAsync(double seconds, CancellationToken outerCt)
    {
        string path;
        string ffmpeg;
        lock (_gate)
        {
            if (!CanPreviewLocked()) return null;
            path = _mediaPath!;
            ffmpeg = _ffmpeg!;
        }

        var bucket = (long)Math.Floor(Math.Max(0, seconds) / BucketSec);
        if (_cache.TryGetValue(bucket, out var hit))
            return hit;

        CancellationTokenSource linked;
        lock (_gate)
        {
            CancelLocked();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            linked = _cts;
        }

        var ct = linked.Token;
        var seq = Interlocked.Increment(ref _seq);
        var outFile = Path.Combine(EnsureTempDir(), $"t-{bucket}-{seq}.jpg");

        try
        {
            var ok = await RunFfmpegAsync(ffmpeg, path, bucket * BucketSec, outFile, ct)
                .ConfigureAwait(false);
            if (!ok || ct.IsCancellationRequested || !File.Exists(outFile))
                return null;

            BitmapImage bmp;
            await using (var fs = new FileStream(outFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = fs;
                bmp.DecodePixelWidth = ThumbWidth;
                bmp.EndInit();
                bmp.Freeze();
            }

            TrimCache(bucket, bmp);
            return bmp;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    private void TrimCache(long bucket, BitmapImage bmp)
    {
        _cache[bucket] = bmp;
        if (_cache.Count <= MaxCache) return;
        foreach (var key in _cache.Keys.OrderBy(k => k).Take(_cache.Count - MaxCache).ToList())
            _cache.TryRemove(key, out _);
    }

    private static async Task<bool> RunFfmpegAsync(
        string ffmpeg,
        string mediaPath,
        double seconds,
        string outFile,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // -ss before -i: keyframe-accurate enough for hover preview, much faster.
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale={ThumbWidth}:-2");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(outFile);

        using var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.Start())
                return false;
            ChildProcessLifetime.Track(proc);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return proc.ExitCode == 0 && File.Exists(outFile);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        catch
        {
            return false;
        }
    }

    private string EnsureTempDir()
    {
        if (!string.IsNullOrWhiteSpace(_tempDir) && Directory.Exists(_tempDir))
            return _tempDir;
        _tempDir = Path.Combine(AppPaths.CacheDir, "seek-thumbs");
        Directory.CreateDirectory(_tempDir);
        return _tempDir;
    }

    private void CancelLocked()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
    }

    private void ClearCacheLocked()
    {
        _cache.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CancelLocked();
            ClearCacheLocked();
            _mediaPath = null;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_tempDir) && Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* ignore */ }
    }
}
