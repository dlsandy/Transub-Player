using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

internal sealed record StreamQualityOption(
    string Id,
    string Label,
    string Url,
    string Kind,
    int Rank);

/// <summary>Picks the real Stripchat media playlist from a master m3u8 (skips ad variants).</summary>
internal static class StripchatHlsPlaylist
{
    private static readonly HttpClient Http = StripchatStreamResolver.SharedHttp;

    private static readonly string[] AdMarkers =
    [
        "/ad/", "/ad.", "advert", "promo", "preview", "placeholder", "slate", "countdown",
    ];

    private static readonly HashSet<string> PreferredPkeys = new(StringComparer.Ordinal)
    {
        "Zeechoej4aleeshi",
        "Zokee2OhPh9kugh4",
        "Ook7quaiNgiyuhai",
    };

    private static readonly Regex MouflonRe = new(
        @"#EXT-X-MOUFLON:PSCH:(?<psch>v\d+):(?<pkey>\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsMasterPlaylist(string url)
        => url.Contains("/master/", StringComparison.OrdinalIgnoreCase)
           || url.Contains("master_", StringComparison.OrdinalIgnoreCase);

    public static Task<string> ResolvePlayUrlAsync(string playUrl, string referer, CancellationToken ct, string? masterUrl = null)
    {
        if (!NeedsProxy(playUrl))
            return Task.FromResult(playUrl);

        return Task.FromResult(StripchatHlsProxy.Wrap(playUrl, referer, masterUrl));
    }

    public static async Task<string> ResolveMediaUrlAsync(string masterUrl, string referer, CancellationToken ct)
    {
        var text = await FetchTextAsync(masterUrl, referer, ct).ConfigureAwait(false);
        var (psch, pkey) = ExtractMouflonKeys(text);
        var variant = PickBestVariant(text, masterUrl);
        if (variant is null)
            throw new InvalidOperationException("Stripchat master playlist has no playable variant.");
        return AuthOrSacMirror(variant.Url, psch, pkey);
    }

    /// <summary>Re-resolve a specific quality variant with fresh master auth (for live proxy polls).</summary>
    public static async Task<string> ResolveVariantMediaUrlAsync(
        string masterUrl,
        string variantHintUrl,
        string referer,
        CancellationToken ct)
    {
        var text = await FetchTextAsync(masterUrl, referer, ct).ConfigureAwait(false);
        var (psch, pkey) = ExtractMouflonKeys(text);
        var variants = ParseVariants(text, masterUrl).Where(v => !IsAdVariant(v)).ToList();
        if (variants.Count == 0)
            throw new InvalidOperationException("Stripchat master playlist has no playable variant.");

        var hintSac = StripchatLiveCdn.PreferPlayableCdn(variantHintUrl);
        var hintFile = VariantFileKey(hintSac);

        var match = variants.FirstOrDefault(v =>
        {
            var sac = StripchatLiveCdn.TryRewriteDoppioMediaToSacdnssedge(v.Url) ?? v.Url;
            return string.Equals(VariantFileKey(sac), hintFile, StringComparison.OrdinalIgnoreCase)
                   || sac.Contains(hintFile, StringComparison.OrdinalIgnoreCase);
        });

        match ??= PickBestVariant(text, masterUrl);
        if (match is null)
            throw new InvalidOperationException("Stripchat master playlist has no playable variant.");

        return AuthOrSacMirror(match.Url, psch, pkey);
    }

    private static string AuthOrSacMirror(string variantUrl, string psch, string pkey)
    {
        var sac = StripchatLiveCdn.TryRewriteDoppioMediaToSacdnssedge(variantUrl);
        if (!string.IsNullOrWhiteSpace(sac))
            return sac;
        return AppendAuth(variantUrl, psch, pkey);
    }

    private static string VariantFileKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var file = Path.GetFileName(uri.AbsolutePath);
        var dot = file.IndexOf('.');
        return dot > 0 ? file[..dot] : file;
    }

    /// <summary>Parse master playlist into selectable non-ad quality options (auth query attached).</summary>
    public static async Task<IReadOnlyList<StreamQualityOption>> ListMasterQualitiesAsync(
        string masterUrl,
        string referer,
        CancellationToken ct)
    {
        var text = await FetchTextAsync(masterUrl, referer, ct).ConfigureAwait(false);
        var (psch, pkey) = ExtractMouflonKeys(text);
        var variants = ParseVariants(text, masterUrl)
            .Where(v => !IsAdVariant(v))
            .OrderByDescending(ResolutionScore)
            .ThenByDescending(v => v.Bandwidth)
            .ToList();

        if (variants.Count == 0) return [];

        var list = new List<StreamQualityOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variants)
        {
            var label = FormatVariantLabel(v);
            var id = "q:" + label.ToLowerInvariant();
            if (!seen.Add(id))
                id = "q:" + v.Bandwidth;

            var sac = StripchatLiveCdn.TryRewriteDoppioMediaToSacdnssedge(v.Url);
            string url;
            string kind;
            int rank;
            if (!string.IsNullOrWhiteSpace(sac))
            {
                url = sac;
                kind = "sacdnssedge";
                rank = ResolutionScore(v);
            }
            else
            {
                url = AppendAuth(v.Url, psch, pkey);
                kind = "doppio";
                rank = ResolutionScore(v);
            }

            list.Add(new StreamQualityOption(id, label, url, kind, rank));
        }

        return list;
    }

    public static bool NeedsProxy(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (IsMasterPlaylist(url)) return true;
        // sacdnssedge: mpv polls CDN directly (live). Proxy snapshot was freezing ~2min in.
        var playable = StripchatLiveCdn.PreferPlayableCdn(url);
        if (StripchatLiveCdn.IsSacdnssedge(playable)) return false;
        return playable.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase)
               || playable.Contains("strpst.com", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<string> EnsureProxiedPlayUrlAsync(
        string url,
        string referer,
        CancellationToken ct,
        string? masterUrl = null)
    {
        if (!NeedsProxy(url) || url.Contains("127.0.0.1", StringComparison.Ordinal))
            return url;
        return await ResolvePlayUrlAsync(url, referer, ct, masterUrl).ConfigureAwait(false);
    }

    /// <summary>Make mpv/ffmpeg treat the playlist as ongoing live/event, not finite VOD.</summary>
    public static string NormalizeLivePlaylist(string playlist)
    {
        var sb = new StringBuilder();
        var hasEvent = false;
        var hasHeader = false;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)) continue;
            if (t.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.OrdinalIgnoreCase))
            {
                if (t.Contains("VOD", StringComparison.OrdinalIgnoreCase)) continue;
                hasEvent = t.Contains("EVENT", StringComparison.OrdinalIgnoreCase);
            }

            if (t.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                hasHeader = true;
                sb.AppendLine("#EXTM3U");
                if (!hasEvent)
                {
                    sb.AppendLine("#EXT-X-PLAYLIST-TYPE:EVENT");
                    hasEvent = true;
                }
                continue;
            }

            sb.AppendLine(line);
        }

        if (!hasHeader)
            sb.Insert(0, "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:EVENT\n");
        else if (!hasEvent)
        {
            var s = sb.ToString();
            sb.Clear();
            sb.AppendLine("#EXTM3U");
            sb.AppendLine("#EXT-X-PLAYLIST-TYPE:EVENT");
            sb.Append(s.AsSpan(s.IndexOf('\n') + 1));
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string FormatVariantLabel(HlsVariant v)
    {
        var name = (v.Name ?? "").Trim();
        if (!string.IsNullOrEmpty(name))
        {
            if (name.Equals("source", StringComparison.OrdinalIgnoreCase))
                return "源画质";
            if (Regex.IsMatch(name, @"^\d{3,4}p?$", RegexOptions.IgnoreCase))
                return name.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? name : name + "p";
            return name;
        }

        if (!string.IsNullOrWhiteSpace(v.Resolution))
        {
            var parts = v.Resolution.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[1], out var h) && h > 0)
                return h + "p";
        }

        if (v.Bandwidth >= 2_500_000) return "高清";
        if (v.Bandwidth >= 1_000_000) return "标清";
        if (v.Bandwidth > 0) return Math.Max(1, v.Bandwidth / 1000) + "k";
        return "画质";
    }

    private static (string Psch, string Pkey) ExtractMouflonKeys(string m3u8)
    {
        string? fallbackPsch = null;
        string? fallbackPkey = null;

        foreach (Match m in MouflonRe.Matches(m3u8))
        {
            var p = m.Groups["psch"].Value;
            var k = m.Groups["pkey"].Value;
            fallbackPsch = p;
            fallbackPkey = k;
            if (PreferredPkeys.Contains(k))
                return (p, k);
        }

        if (fallbackPsch is not null && fallbackPkey is not null)
            return (fallbackPsch, fallbackPkey);

        return ("v1", "");
    }

    private static HlsVariant? PickBestVariant(string m3u8, string masterUrl)
    {
        var variants = ParseVariants(m3u8, masterUrl);
        if (variants.Count == 0) return null;

        var candidates = variants.Where(v => !IsAdVariant(v)).ToList();
        if (candidates.Count == 0)
            candidates = variants;

        return candidates
            .OrderByDescending(ResolutionScore)
            .ThenByDescending(v => v.Bandwidth)
            .First();
    }

    private static bool IsAdVariant(HlsVariant v)
    {
        if (!string.IsNullOrWhiteSpace(v.Name))
        {
            var name = v.Name.Trim();
            if (name.Equals("ad", StringComparison.OrdinalIgnoreCase)
                || name.Contains("advert", StringComparison.OrdinalIgnoreCase)
                || name.Contains("promo", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var marker in AdMarkers)
        {
            if (v.Url.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (v.Bandwidth > 0 && v.Bandwidth < 200_000 && ResolutionScore(v) <= 0)
            return true;

        return false;
    }

    private static int ResolutionScore(HlsVariant v)
    {
        var name = (v.Name ?? "").ToLowerInvariant();
        if (name.Contains("source")) return 1000;
        if (name.Contains("1080")) return 900;
        if (name.Contains("720")) return 800;
        if (name.Contains("480")) return 600;
        if (name.Contains("360")) return 400;
        if (name.Contains("240")) return 200;
        if (name.Contains("auto")) return 100;

        if (!string.IsNullOrWhiteSpace(v.Resolution))
        {
            var parts = v.Resolution.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[1], out var h))
                return h;
        }

        return v.Bandwidth / 1000;
    }

    private static List<HlsVariant> ParseVariants(string m3u8, string masterUrl)
    {
        var list = new List<HlsVariant>();
        var lines = m3u8.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                continue;

            var bandwidth = ParseAttrInt(line, "BANDWIDTH");
            var name = ParseAttrString(line, "NAME");
            var resolution = ParseAttrString(line, "RESOLUTION");

            for (var j = i + 1; j < lines.Length && j < i + 4; j++)
            {
                var uri = lines[j].Trim();
                if (string.IsNullOrEmpty(uri) || uri.StartsWith('#'))
                    continue;
                list.Add(new HlsVariant(ResolveUrl(masterUrl, uri), bandwidth, name, resolution));
                break;
            }
        }

        return list;
    }

    private static string AppendAuth(string url, string psch, string pkey)
    {
        if (string.IsNullOrWhiteSpace(pkey))
            return url;

        var sep = url.Contains('?') ? "&" : "?";
        return $"{url}{sep}psch={Uri.EscapeDataString(psch)}&pkey={Uri.EscapeDataString(pkey)}&playlistType=lowLatency";
    }

    internal static string CdnReferer => "https://stripchat.com/";

    private static string ResolveUrl(string baseUrl, string relative)
    {
        if (Uri.TryCreate(relative, UriKind.Absolute, out var abs))
            return abs.ToString();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return relative;
        return new Uri(baseUri, relative).ToString();
    }

    private static int ParseAttrInt(string line, string key)
    {
        var s = ParseAttrString(line, key);
        return int.TryParse(s, out var n) ? n : 0;
    }

    private static string? ParseAttrString(string line, string key)
    {
        var pattern = $@"{Regex.Escape(key)}=""([^""]*)""|{Regex.Escape(key)}=([^,]+)";
        var m = Regex.Match(line, pattern, RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value.Trim();
    }

    /// <summary>
    /// Resolve sacdnssedge entry URL for mpv (Covers Download: fetch playlist, pick ≤720p media variant).
    /// For rolling media playlists, keep the stable entry URL so mpv re-fetches each poll — not a one-shot redirect.
    /// </summary>
    public static async Task<string> ResolveSacdnssedgeForMpvAsync(string entryUrl, CancellationToken ct, int maxHeight = 720)
    {
        if (!StripchatLiveCdn.IsSacdnssedge(entryUrl))
            return entryUrl;

        string baseUrl = entryUrl;
        string text;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, entryUrl);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
            req.Headers.TryAddWithoutValidation("Referer", StripchatLiveCdn.SacdnssedgePlayReferer);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            baseUrl = res.RequestMessage?.RequestUri?.ToString() ?? entryUrl;
            text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PlayerLog.Write("sacdnssedge resolve: " + ex.Message);
            return entryUrl;
        }

        if (!text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            // Media playlist: give mpv a stable path (no one-shot signed query). Covers also
            // follows redirects to the active node, then lets mpv re-poll that URL.
            var chosen = baseUrl;
            if (Uri.TryCreate(chosen, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Query))
                chosen = u.GetLeftPart(UriPartial.Path);
            if (!string.Equals(chosen, entryUrl, StringComparison.OrdinalIgnoreCase))
                PlayerLog.Write("sacdnssedge node → " + chosen);
            return chosen;
        }

        var variants = ParseVariants(text, baseUrl).Where(v => !IsAdVariant(v)).ToList();
        if (variants.Count == 0)
            variants = ParseVariants(text, baseUrl);

        if (variants.Count == 0)
            return entryUrl;

        var capped = variants
            .Where(v =>
            {
                var h = ResolutionScore(v);
                return h > 0 && h <= maxHeight;
            })
            .ToList();
        var pool = capped.Count > 0 ? capped : variants;
        var pick = pool
            .OrderByDescending(ResolutionScore)
            .ThenByDescending(v => v.Bandwidth)
            .First();

        return StripchatLiveCdn.PreferPlayableCdn(pick.Url);
    }

    public static async Task<string> FetchTextAsync(string url, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        var playReferer = StripchatLiveCdn.IsSacdnssedge(url)
            ? StripchatLiveCdn.SacdnssedgePlayReferer
            : CdnReferer;
        req.Headers.TryAddWithoutValidation("Referer", playReferer);
        if (!StripchatLiveCdn.IsSacdnssedge(url))
            req.Headers.TryAddWithoutValidation("Origin", "https://stripchat.com");
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private sealed record HlsVariant(string Url, int Bandwidth, string? Name, string? Resolution);
}
