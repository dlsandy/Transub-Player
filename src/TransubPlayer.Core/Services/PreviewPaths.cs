namespace TransubPlayer.Services;

/// <summary>Preview ASR/MT output layout under data/preview/{hash}/.</summary>
internal static class PreviewPaths
{
    public static string OutDir(string mediaPath)
        => Path.Combine(AppPaths.PreviewDir, HashName(mediaPath));

    public static string Stem(string mediaPath)
        => Path.GetFileNameWithoutExtension(mediaPath);

    public static string SourceSrt(string mediaPath)
        => Path.Combine(OutDir(mediaPath), Stem(mediaPath) + ".srt");

    public static string ZhSrt(string mediaPath)
        => TranslatedPreviewSrt(mediaPath, TranslateTargets.Zh);

    public static string TranslatedPreviewSrt(string mediaPath, string? translateTarget)
        => Path.Combine(
            OutDir(mediaPath),
            Stem(mediaPath) + $".{TranslateTargets.FileSuffix(translateTarget)}.preview.srt");

    public static string TranslationCachePath(string mediaPath, string? translateTarget)
        => Path.Combine(
            OutDir(mediaPath),
            Stem(mediaPath) + $".{TranslateTargets.FileSuffix(translateTarget)}.cache.json");

    public static string DualSrt(string mediaPath)
        => Path.Combine(OutDir(mediaPath), Stem(mediaPath) + ".dual.preview.srt");

    public static string DisplaySrt(string mediaPath)
        => Path.Combine(OutDir(mediaPath), Stem(mediaPath) + ".display.srt");

    public static string AsrDoneMarker(string mediaPath)
        => Path.Combine(OutDir(mediaPath), "asr.done");

    public static bool HasReadyAsr(string mediaPath)
    {
        try
        {
            if (!File.Exists(AsrDoneMarker(mediaPath))) return false;
            var srt = SourceSrt(mediaPath);
            return File.Exists(srt) && new FileInfo(srt).Length > 32;
        }
        catch
        {
            return false;
        }
    }

    public static void MarkAsrDone(string mediaPath)
    {
        try
        {
            Directory.CreateDirectory(OutDir(mediaPath));
            File.WriteAllText(AsrDoneMarker(mediaPath), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }
    }

    public static void ClearAsrDone(string mediaPath)
    {
        try
        {
            var marker = AsrDoneMarker(mediaPath);
            if (File.Exists(marker))
                File.Delete(marker);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Clear marker and preview SRT files so a new run does not flash stale cues.</summary>
    public static void InvalidatePreviewOutputs(string mediaPath)
    {
        ClearAsrDone(mediaPath);
        TryDelete(SourceSrt(mediaPath));
        foreach (var t in TranslateTargets.All)
        {
            TryDelete(TranslatedPreviewSrt(mediaPath, t));
            TryDelete(TranslationCachePath(mediaPath, t));
        }
        TryDelete(DualSrt(mediaPath));
        TryDelete(DisplaySrt(mediaPath));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    public static string HashName(string path)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }
}
