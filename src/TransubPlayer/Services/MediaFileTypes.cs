namespace TransubPlayer.Services;

/// <summary>Supported media extensions for playback and optional file associations.</summary>
internal static class MediaFileTypes
{
    public sealed record Category(string Id, string LabelKey, IReadOnlyList<string> Extensions);

    public static readonly Category[] Categories =
    [
        new("video", "Settings.Association.Category.Video",
        [
            ".mkv", ".mp4", ".webm", ".avi", ".mov", ".m4v", ".ts", ".m2ts", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".ogv",
        ]),
        new("audio", "Settings.Association.Category.Audio",
        [
            ".mp3", ".flac", ".wav", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".ape",
        ]),
        new("subtitle", "Settings.Association.Category.Subtitle",
        [
            ".srt", ".ass", ".ssa", ".vtt", ".sub",
        ]),
        new("playlist", "Settings.Association.Category.Playlist",
        [
            ".m3u", ".m3u8", ".pls",
        ]),
    ];

    public static IReadOnlyList<string> AllExtensions { get; } =
        Categories.SelectMany(c => c.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> VideoExtensions => Categories[0].Extensions;

    /// <summary>Video + audio only (excludes playlists and subtitle sidecars).</summary>
    public static IReadOnlyList<string> PlaybackExtensions { get; } =
        Categories[0].Extensions.Concat(Categories[1].Extensions).ToArray();

    public static IReadOnlyList<string> PlaylistExtensions => Categories[3].Extensions;

    public static string BuildOpenFileFilter(string allFilesLabel)
        => $"媒体|{string.Join(';', AllExtensions.Select(e => "*" + e))}|{allFilesLabel}|*.*";

    public static bool IsKnown(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return AllExtensions.Contains(ext);
    }

    public static string NormalizeExtension(string ext)
    {
        ext = ext.Trim();
        if (ext.Length == 0) return "";
        if (!ext.StartsWith('.')) ext = "." + ext;
        return ext.ToLowerInvariant();
    }
}
