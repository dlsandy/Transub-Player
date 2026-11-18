namespace TransubPlayer.Services;

/// <summary>Browser playback friendliness for LAN mobile clients (no transcoding).</summary>
internal static class LanMediaCompat
{
    public const string Ok = "ok";
    public const string Warn = "warn";

    /// <summary>MP4/WebM/common audio tend to work in mobile browsers; MKV/HEVC often fail.</summary>
    public static string Classify(string? path)
    {
        var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" or ".webm" or ".mov"
                or ".mp3" or ".m4a" or ".aac" or ".flac" or ".wav" or ".ogg" or ".opus"
                => Ok,
            _ => Warn,
        };
    }

    public static bool IsWarn(string? path) => Classify(path) == Warn;
}
