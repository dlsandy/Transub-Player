using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Local files vs network streams vs mpv capture inputs.</summary>
internal static class MediaSourceHelper
{
    private static readonly HashSet<string> RemoteSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "rtmp", "rtsp", "rtp", "udp", "mms", "mmsh", "mmst",
    };

    public const string DesktopCaptureUrl = "av://lavfi:gdigrab=desktop";

    public static bool IsRemoteUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)
               && RemoteSchemes.Contains(uri.Scheme);
    }

    public static bool IsScreenCapture(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var t = input.Trim();
        return t.StartsWith("av://", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("lavfi://", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("ffmpeg://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNonLocalMedia(string? input)
        => IsRemoteUrl(input) || IsScreenCapture(input);

    public static bool TryNormalizeMedia(string input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var trimmed = input.Trim().Trim('"', '\'');

        if (IsScreenCapture(trimmed))
        {
            normalized = trimmed;
            return true;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Contains('.') && !trimmed.Contains('\\') && !Path.IsPathRooted(trimmed)))
            {
                trimmed = "https://" + trimmed;
            }
            else if (StripchatStreamResolver.TryNormalizeBareHandle(trimmed, out var stripchatPage))
            {
                normalized = stripchatPage;
                return true;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (!RemoteSchemes.Contains(uri.Scheme)) return false;
        normalized = uri.ToString();
        return true;
    }

    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (IsScreenCapture(path))
        {
            if (path.Contains("gdigrab=desktop", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("MediaSource.Screen.Desktop");
            return Loc.Get("MediaSource.Screen.Capture");
        }

        if (IsStripchatPage(path))
        {
            if (TryParseStripchatUsername(path, out var user))
                return Loc.Format("MediaSource.Stripchat.User", user);
        }

        if (IsRemoteUrl(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            var seg = uri.Segments.LastOrDefault(s => s.Length > 1)?.TrimEnd('/');
            if (!string.IsNullOrEmpty(seg))
                return $"{uri.Host} / {seg}";
            return uri.Host;
        }

        return Path.GetFileName(path);
    }

    public static bool IsStripchatPage(string? url)
        => StripchatStreamResolver.IsStripchatPage(url);

    private static bool TryParseStripchatUsername(string url, out string username)
    {
        username = "";
        var m = System.Text.RegularExpressions.Regex.Match(
            url,
            @"(?:[\w-]+\.)*stripchat\.[a-z.]+/(?<user>[A-Za-z0-9_-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        username = m.Groups["user"].Value;
        var bare = username.Contains('@') ? username[..username.IndexOf('@')] : username;
        return !string.IsNullOrWhiteSpace(bare);
    }
}
