using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Live-cam CDN URL building.
/// Prefer primary media CDN (MOUFLON via local proxy). Bare secondary edge
/// <c>/{id}/{id}.m3u8</c> paths often 404; sac edge master is ≤480p only.
/// </summary>
internal static class LiveCamCdn
{
    internal static string SacdnssedgePlayReferer => LiveCamSeal.SacPlayReferer;

    private static readonly Regex HlsNodeRe = new(
        @"(?:^|[^a-z])(?:b-)?(hls-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HlsNodeNumRe = new(
        @"\b(\d{1,3})\b",
        RegexOptions.Compiled);

    private static readonly Regex DoppioAutoRe = new(
        @"/hls/([^/]+)/master/[^/?#]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Lazy<Regex> DoppioHostReLazy = new(() => new Regex(
        LiveCamSeal.DoppioHostRe,
        RegexOptions.IgnoreCase | RegexOptions.Compiled));

    private static Regex DoppioHostRe => DoppioHostReLazy.Value;

    public static bool IsSacdnssedge(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains(LiveCamSeal.SacToken, StringComparison.OrdinalIgnoreCase);

    public static bool IsDoppiocdn(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.Contains(LiveCamSeal.DoppioToken, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Legacy bare media path — often 404 now; kept only for probe/fallback lists.</summary>
    public static string SacdnssedgeUrl(string streamName, string? viewServerOrNode = null)
    {
        var sn = streamName.Trim();
        var node = NormalizeHlsNode(viewServerOrNode) ?? "hls-17";
        return string.Format(LiveCamSeal.SacMediaUrlTpl, node, sn);
    }

    public static string SacdnssedgeEdgeAutoUrl(string streamName)
    {
        var sn = streamName.Trim();
        return string.Format(LiveCamSeal.SacEdgeAutoTpl, sn);
    }

    public static string DoppiocdnEdgeAutoUrl(string streamName)
    {
        var sn = streamName.Trim();
        return string.Format(LiveCamSeal.DoppioEdgeAutoTpl, sn);
    }

    public static string? NormalizeDoppiocdnAuto(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var preferred = PreferPlayableCdn(url);
        if (preferred.Contains("_auto.m3u8", StringComparison.OrdinalIgnoreCase)) return preferred;

        var m = DoppioAutoRe.Match(preferred);
        if (!m.Success) return null;

        return DoppiocdnEdgeAutoUrl(m.Groups[1].Value);
    }

    /// <summary>Preferred play URL: doppiocdn edge master (proxy + MOUFLON), not bare sac media.</summary>
    public static string PickPlayUrl(string streamName, string? viewServer, string? hlsPlaylist = null)
    {
        var sn = streamName.Trim();
        if (string.IsNullOrEmpty(sn)) return "";

        if (!string.IsNullOrWhiteSpace(hlsPlaylist))
        {
            var fromPlaylist = NormalizeDoppiocdnAuto(hlsPlaylist) ?? PreferPlayableCdn(hlsPlaylist.Trim());
            if (fromPlaylist.Contains("_auto.m3u8", StringComparison.OrdinalIgnoreCase)
                || fromPlaylist.Contains("/master/", StringComparison.OrdinalIgnoreCase))
                return fromPlaylist;
        }

        _ = viewServer; // node only mattered for bare sac media paths
        return DoppiocdnEdgeAutoUrl(sn);
    }

    /// <summary>Master playlist URL used for multi-quality listing (doppiocdn).</summary>
    public static string? MasterPlaylistUrl(string streamName, string? hlsPlaylist = null)
    {
        var sn = streamName.Trim();
        if (!string.IsNullOrWhiteSpace(hlsPlaylist))
        {
            var fromPlaylist = NormalizeDoppiocdnAuto(hlsPlaylist) ?? PreferPlayableCdn(hlsPlaylist.Trim());
            if (fromPlaylist.Contains("_auto.m3u8", StringComparison.OrdinalIgnoreCase)
                || fromPlaylist.Contains("/master/", StringComparison.OrdinalIgnoreCase))
                return fromPlaylist;
        }

        return string.IsNullOrEmpty(sn) ? null : DoppiocdnEdgeAutoUrl(sn);
    }

    /// <summary>
    /// Optional sac edge master (≤480p) as a secondary mirror — not the bare media path.
    /// </summary>
    public static IReadOnlyList<string> BuildSacdnssedgeCandidates(string streamName, string? viewServer)
    {
        var sn = streamName.Trim();
        if (string.IsNullOrEmpty(sn)) return [];
        _ = viewServer;
        return [SacdnssedgeEdgeAutoUrl(sn)];
    }

    public static string PlayReferer(string url)
        => IsSacdnssedge(url) ? SacdnssedgePlayReferer : LiveCamHlsPlaylist.CdnReferer;

    /// <summary>
    /// Rewrite unreachable primary CDN .com edge → .net (TLS resets on .com from many networks).
    /// Do not rewrite to secondary CDN: same path with _720p often 404s there (caps at 480p).
    /// </summary>
    public static string? TryRewriteDoppioComToNet(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var m = DoppioHostRe.Match(uri.Host);
        if (!m.Success) return null;
        if (!m.Groups["tld"].Value.Equals("com", StringComparison.OrdinalIgnoreCase))
            return null;

        var edge = m.Groups["edge"].Value.ToLowerInvariant();
        var builder = new UriBuilder(uri) { Host = string.Format(LiveCamSeal.DoppioNetHostTpl, edge) };
        return builder.Uri.ToString();
    }

    public static string PreferPlayableCdn(string url)
        => TryRewriteDoppioComToNet(url) ?? url;

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
