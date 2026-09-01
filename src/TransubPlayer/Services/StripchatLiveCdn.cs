using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>Stripchat live CDN URL building (aligned with Covers Download sacdnssedge-first playback).</summary>
internal static class StripchatLiveCdn
{
    internal const string SacdnssedgePlayReferer = "https://juy.bvjiyfh.com:25118/";

    private static readonly Regex HlsNodeRe = new(
        @"(?:^|[^a-z])(?:b-)?(hls-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HlsNodeNumRe = new(
        @"\b(\d{1,3})\b",
        RegexOptions.Compiled);

    private static readonly Regex DoppioAutoRe = new(
        @"/hls/([^/]+)/master/[^/?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSacdnssedge(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains("sacdnssedge.com", StringComparison.OrdinalIgnoreCase);

    public static string? NormalizeHlsNode(string? viewServer)
    {
        var raw = (viewServer ?? "").Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        var m = HlsNodeRe.Match(raw);
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();

        m = HlsNodeNumRe.Match(raw);
        if (m.Success) return "hls-" + m.Groups[1].Value;

        return null;
    }

    public static string SacdnssedgeUrl(string streamName, string? viewServerOrNode = null)
    {
        var sn = streamName.Trim();
        var node = NormalizeHlsNode(viewServerOrNode) ?? "hls-17";
        return $"https://media-hls.sacdnssedge.com/b-{node}/{sn}/{sn}.m3u8";
    }

    public static string DoppiocdnEdgeAutoUrl(string streamName)
    {
        var sn = streamName.Trim();
        return $"https://edge-hls.doppiocdn.com/hls/{sn}/master/{sn}_auto.m3u8";
    }

    public static string? NormalizeDoppiocdnAuto(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.Contains("_auto.m3u8", StringComparison.OrdinalIgnoreCase)) return url;

        var m = DoppioAutoRe.Match(url);
        if (!m.Success) return null;

        return DoppiocdnEdgeAutoUrl(m.Groups[1].Value);
    }

    /// <summary>Preferred play URL: sacdnssedge mirror first, doppiocdn edge as fallback.</summary>
    public static string PickPlayUrl(string streamName, string? viewServer, string? hlsPlaylist = null)
    {
        var sn = streamName.Trim();
        if (string.IsNullOrEmpty(sn)) return "";

        foreach (var url in BuildSacdnssedgeCandidates(sn, viewServer))
            return url;

        if (!string.IsNullOrWhiteSpace(hlsPlaylist))
        {
            var fromPlaylist = NormalizeDoppiocdnAuto(hlsPlaylist) ?? hlsPlaylist.Trim();
            if (fromPlaylist.Contains("_auto.m3u8", StringComparison.OrdinalIgnoreCase)
                || fromPlaylist.Contains("/master/", StringComparison.OrdinalIgnoreCase))
                return fromPlaylist;
        }

        return DoppiocdnEdgeAutoUrl(sn);
    }

    /// <summary>Master playlist URL used for multi-quality listing (doppiocdn).</summary>
    public static string? MasterPlaylistUrl(string streamName, string? hlsPlaylist = null)
    {
        var sn = streamName.Trim();
        if (!string.IsNullOrWhiteSpace(hlsPlaylist))
        {
            var fromPlaylist = NormalizeDoppiocdnAuto(hlsPlaylist) ?? hlsPlaylist.Trim();
            if (fromPlaylist.Contains("_auto.m3u8", StringComparison.OrdinalIgnoreCase)
                || fromPlaylist.Contains("/master/", StringComparison.OrdinalIgnoreCase))
                return fromPlaylist;
        }

        return string.IsNullOrEmpty(sn) ? null : DoppiocdnEdgeAutoUrl(sn);
    }

    public static IReadOnlyList<string> BuildSacdnssedgeCandidates(string streamName, string? viewServer)
    {
        var sn = streamName.Trim();
        if (string.IsNullOrEmpty(sn)) return [];

        var list = new List<string>();
        void Add(string url)
        {
            if (!list.Contains(url, StringComparer.OrdinalIgnoreCase))
                list.Add(url);
        }

        var node = NormalizeHlsNode(viewServer) ?? "hls-17";
        Add(SacdnssedgeUrl(sn, node));
        if (!node.Equals("hls-17", StringComparison.OrdinalIgnoreCase))
            Add(SacdnssedgeUrl(sn, "hls-17"));

        return list;
    }

    public static string PlayReferer(string url)
        => IsSacdnssedge(url) ? SacdnssedgePlayReferer : StripchatHlsPlaylist.CdnReferer;

    /// <summary>
    /// Rewrite doppiocdn media-hls variant URLs to sacdnssedge (same path).
    /// doppiocdn.com media edge often fails TLS from some networks; sacdnssedge is the playable mirror.
    /// </summary>
    public static string? TryRewriteDoppioMediaToSacdnssedge(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase)) return null;

        var path = uri.AbsolutePath;
        // e.g. /b-hls-28/204211824/204211824_480p.m3u8
        if (!path.Contains("/b-hls-", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/b-hls", StringComparison.OrdinalIgnoreCase))
            return null;

        return $"https://media-hls.sacdnssedge.com{path}";
    }

    public static string PreferPlayableCdn(string url)
        => TryRewriteDoppioMediaToSacdnssedge(url) ?? url;

    public static string? HlsNodeFromCam(JsonElement cam)
    {
        if (!cam.TryGetProperty("viewServers", out var servers)) return null;

        if (servers.TryGetProperty("flashphoner-hls", out var flash))
            return NormalizeHlsNode(flash.GetString());

        if (servers.TryGetProperty("flashphonerHls", out var camel))
            return NormalizeHlsNode(camel.GetString());

        if (servers.TryGetProperty("hls", out var hls))
            return NormalizeHlsNode(hls.GetString());

        return null;
    }

    public static string PickPlayUrlFromApi(JsonElement root)
    {
        if (!root.TryGetProperty("cam", out var cam)) return "";

        var user = root.TryGetProperty("user", out var userWrap)
                   && userWrap.TryGetProperty("user", out var u)
            ? u
            : default;

        if (user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("isLive", out var live)
            && live.ValueKind == JsonValueKind.False)
            return "";

        var streamName = cam.TryGetProperty("streamName", out var sn) ? sn.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(streamName)
            && user.ValueKind == JsonValueKind.Object
            && user.TryGetProperty("id", out var id))
            streamName = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(streamName)) return "";

        var hlsPlaylist = cam.TryGetProperty("hlsPlaylist", out var pl) ? pl.GetString() : null;
        var viewServer = HlsNodeFromCam(cam);
        return PickPlayUrl(streamName, viewServer, hlsPlaylist);
    }
}
