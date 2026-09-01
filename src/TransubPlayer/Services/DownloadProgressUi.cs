using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Parsed download progress for progress dialogs.</summary>
internal readonly record struct DownloadProgressSnapshot(
    string Message,
    double? Percent = null,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? SpeedBytesPerSec = null)
{
    public string ToStatusLine()
    {
        if (Percent is >= 0 && DownloadedBytes is >= 0 && TotalBytes is > 0)
            return Loc.Format("DownloadProgress.DownloadingLine", (int)Percent.Value,
                DownloadProgressUi.FormatBytes(DownloadedBytes.Value), DownloadProgressUi.FormatBytes(TotalBytes.Value));
        if (DownloadedBytes is >= 0 && TotalBytes is > 0)
            return Loc.Format("DownloadProgress.SizePair", DownloadProgressUi.FormatBytes(DownloadedBytes.Value),
                DownloadProgressUi.FormatBytes(TotalBytes.Value));
        if (DownloadedBytes is >= 0)
            return Loc.Format("DownloadProgress.DownloadedOnly", DownloadProgressUi.FormatBytes(DownloadedBytes.Value));
        return Message;
    }
}

/// <summary>Shared helpers for download progress dialogs.</summary>
internal static class DownloadProgressUi
{
    public static bool TryParsePercent(string line, out double pct)
    {
        pct = 0;
        var idx = line.IndexOf('%');
        if (idx <= 0) return false;
        var start = idx - 1;
        while (start >= 0 && char.IsDigit(line[start]))
            start--;
        start++;
        if (start >= idx) return false;
        if (!int.TryParse(line.AsSpan(start, idx - start), out var n))
            return false;
        pct = Math.Clamp(n, 0, 100);
        return true;
    }

    public static bool TryParseLine(string line, out DownloadProgressSnapshot snapshot)
    {
        snapshot = new DownloadProgressSnapshot(line);
        if (string.IsNullOrWhiteSpace(line))
            return false;

        double? pct = TryParsePercent(line, out var p) ? p : null;
        long? downloaded = null;
        long? total = null;

        var slash = line.IndexOf(" / ", StringComparison.Ordinal);
        if (slash >= 0)
        {
            var leftStart = line.LastIndexOf('·', slash);
            leftStart = leftStart >= 0 ? leftStart + 1 : 0;
            var left = line.AsSpan(leftStart, slash - leftStart).Trim();
            var right = line.AsSpan(slash + 3).Trim();
            var rightEnd = right.IndexOf('·');
            if (rightEnd >= 0)
                right = right[..rightEnd].Trim();
            if (TryParseByteSize(left, out var d))
                downloaded = d;
            if (TryParseByteSize(right, out var t))
                total = t;
        }
        else if (TryParsePercent(line, out _) && line.Contains('·', StringComparison.Ordinal))
        {
            // "下载中 1.2 MB…" without total
            var dot = line.IndexOf('·', StringComparison.Ordinal);
            if (dot >= 0)
            {
                var chunk = line.AsSpan(dot + 1).Trim().TrimEnd('…').TrimEnd('.').Trim();
                if (TryParseByteSize(chunk, out var d))
                    downloaded = d;
            }
        }

        snapshot = new DownloadProgressSnapshot(line, pct, downloaded, total);
        return pct is not null || downloaded is not null || total is not null;
    }

    public static void ReportDownload(
        Action<string>? status,
        long downloaded,
        long? total,
        double? speedBytesPerSec)
    {
        if (status is null) return;
        int? pct = total is > 0 ? (int)Math.Clamp(100.0 * downloaded / total.Value, 0, 100) : null;
        var snap = new DownloadProgressSnapshot("", pct, downloaded, total, speedBytesPerSec);
        status(snap.ToStatusLine());
    }

    public static async Task CopyStreamWithProgressAsync(
        Stream input,
        Stream output,
        Action<string>? status,
        long? totalBytes,
        CancellationToken ct)
    {
        var buffer = new byte[82_000];
        long readTotal = 0;
        var lastUi = DateTime.UtcNow;
        long lastSpeedBytes = 0;
        double? speed = null;

        while (true)
        {
            var n = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            readTotal += n;

            if ((DateTime.UtcNow - lastUi).TotalMilliseconds < 400)
                continue;

            var now = DateTime.UtcNow;
            var elapsed = (now - lastUi).TotalSeconds;
            if (elapsed > 0.05 && readTotal > lastSpeedBytes)
                speed = (readTotal - lastSpeedBytes) / elapsed;
            lastSpeedBytes = readTotal;
            lastUi = now;

            ReportDownload(status, readTotal, totalBytes, speed);
        }

        ReportDownload(status, readTotal, totalBytes, speed);
    }

    public static string FormatBytes(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024 * 1024) return $"{n / 1024.0:0.#} KB";
        if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024):0.##} MB";
        return $"{n / (1024.0 * 1024 * 1024):0.##} GB";
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:0.#} KB/s";
        return $"{bytesPerSec / (1024.0 * 1024):0.##} MB/s";
    }

    private static bool TryParseByteSize(ReadOnlySpan<char> token, out long bytes)
    {
        bytes = 0;
        token = token.Trim();
        if (token.IsEmpty) return false;

        var unitStart = token.Length - 1;
        while (unitStart >= 0 && !char.IsDigit(token[unitStart]) && token[unitStart] != '.')
            unitStart--;
        if (unitStart < 0) return false;

        var numSpan = token[..(unitStart + 1)].Trim();
        var unitSpan = token[(unitStart + 1)..].Trim();
        if (!double.TryParse(numSpan, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;

        var mult = unitSpan switch
        {
            _ when unitSpan.Equals("B", StringComparison.OrdinalIgnoreCase) => 1d,
            _ when unitSpan.Equals("KB", StringComparison.OrdinalIgnoreCase) => 1024d,
            _ when unitSpan.Equals("MB", StringComparison.OrdinalIgnoreCase) => 1024d * 1024,
            _ when unitSpan.Equals("GB", StringComparison.OrdinalIgnoreCase) => 1024d * 1024 * 1024,
            _ => 0d,
        };
        if (mult <= 0) return false;
        bytes = (long)(value * mult);
        return true;
    }
}
