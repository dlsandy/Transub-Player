using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Online subtitle search: SubtitleCat first; if unreachable (common in CN), fall back to Xunlei public API.
/// SubtitleCat (Cloudflare) often gets TLS RST on IPv6 / some PoPs — requests pin IPv4 and retry hosts/IPs.
/// </summary>
internal static class SubtitleCatClient
{
    /// <summary>Prefer apex host — www SNI is reset more often on some CN networks.</summary>
    private static readonly string[] CatHosts =
    [
        "subtitlecat.com",
        "www.subtitlecat.com",
    ];

    private const string XunleiApi = "https://api-shoulei-ssl.xunlei.com/oracle/subtitle";

    private static readonly string[] PreferredLangCodes =
    [
        "zh-CN", "zh-TW", "zh", "chi", "zho", "chs", "cht",
    ];

    private static readonly HttpClient Http = CreateSharedClient();

    private static readonly object CatRouteLock = new();
    private static string? _lastGoodCatHost;
    private static IPAddress? _lastGoodCatIp;

    private static readonly Regex HrefSubsRegex = new(
        @"href\s*=\s*[""'](subs/[^""']+\.html)[""'][^>]*>([^<]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SizeRegex = new(
        @"SIZE\s*([\d.]+\s*[KMG]?B)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DownloadsRegex = new(
        @"(\d+)\s*downloads?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DownloadAnchorRegex = new(
        @"<a[^>]+id\s*=\s*[""']download_([^""']+)[""'][^>]+href\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DownloadAnchorAltRegex = new(
        @"<a[^>]+href\s*=\s*[""']([^""']+)[""'][^>]+id\s*=\s*[""']download_([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ZhSrtHrefRegex = new(
        @"href\s*=\s*[""'](/?subs/[^""']*-zh-(?:CN|TW)\.srt)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnySrtHrefRegex = new(
        @"href\s*=\s*[""'](/?subs/[^""']+\.srt)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SeasonEpisodePattern = new(
        @"(?i)\bS\d{1,2}E\d{1,2}\b",
        RegexOptions.Compiled);

    /// <summary>Search SubtitleCat; on network/SSL failure, fall back to Xunlei.</summary>
    public static async Task<(IReadOnlyList<SubtitleCatResult> Results, string? Note)> SearchWithFallbackAsync(
        string keyword,
        CancellationToken ct)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0)
            return ([], null);

        Exception? catError = null;
        try
        {
            var cat = await SearchSubtitleCatAsync(keyword, ct).ConfigureAwait(false);
            if (cat.Count > 0)
                return (cat, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            catError = ex;
        }

        try
        {
            var xunlei = await SearchXunleiAsync(keyword, ct).ConfigureAwait(false);
            if (xunlei.Count > 0)
            {
                var note = catError is null
                    ? "SubtitleCat 无结果 · 已改用迅雷字幕库"
                    : "SubtitleCat 不可达 · 已改用迅雷字幕库";
                return (xunlei, note);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception xunleiEx)
        {
            if (catError is not null)
                throw new InvalidOperationException(
                    $"SubtitleCat：{FormatNetError(catError)}；迅雷：{FormatNetError(xunleiEx)}",
                    xunleiEx);
            throw;
        }

        if (catError is not null)
            throw new InvalidOperationException(FormatNetError(catError), catError);

        return ([], null);
    }

    /// <summary>Search SubtitleCat then Xunlei, trying Primary and Alternates.</summary>
    public static async Task<(IReadOnlyList<SubtitleCatResult> Results, string? Note)> SearchWithFallbackAsync(
        MediaSearchQuery query,
        CancellationToken ct)
    {
        string? lastNote = null;
        foreach (var keyword in EnumerateKeywords(query))
        {
            var (results, note) = await SearchWithFallbackAsync(keyword, ct).ConfigureAwait(false);
            lastNote = note ?? lastNote;
            if (results.Count > 0)
                return (RankResults(results, query), note);
        }

        return ([], lastNote);
    }

    /// <summary>Search a single provider, trying Primary then Alternates.</summary>
    public static async Task<IReadOnlyList<SubtitleCatResult>> SearchProviderAsync(
        OnlineSubtitleProvider provider,
        MediaSearchQuery query,
        CancellationToken ct)
    {
        foreach (var keyword in EnumerateKeywords(query))
        {
            var results = provider == OnlineSubtitleProvider.Xunlei
                ? await SearchXunleiAsync(keyword, ct).ConfigureAwait(false)
                : await SearchSubtitleCatAsync(keyword, ct).ConfigureAwait(false);
            if (results.Count > 0)
                return RankResults(results, query);
        }

        return [];
    }

    public static OnlineSubtitleProvider DetectProvider(IReadOnlyList<SubtitleCatResult> results)
    {
        if (results.Count > 0
            && results.All(r => r.Source.Equals("迅雷", StringComparison.OrdinalIgnoreCase)))
            return OnlineSubtitleProvider.Xunlei;
        return OnlineSubtitleProvider.SubtitleCat;
    }

    private static IEnumerable<string> EnumerateKeywords(MediaSearchQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Primary))
            yield return query.Primary.Trim();
        foreach (var alt in query.Alternates)
        {
            if (string.IsNullOrWhiteSpace(alt)) continue;
            var t = alt.Trim();
            if (!t.Equals(query.Primary, StringComparison.OrdinalIgnoreCase))
                yield return t;
        }
    }

    public static IReadOnlyList<SubtitleCatResult> RankResults(
        IReadOnlyList<SubtitleCatResult> results,
        MediaSearchQuery query)
    {
        if (results.Count == 0) return results;

        var code = query.CatalogCode;
        var primary = query.Primary;
        return results
            .Select(r => r with { Score = ScoreResult(r, query, code, primary) })
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Downloads)
            .Take(40)
            .ToList();
    }

    public static async Task<string> DownloadAndSaveAsync(
        SubtitleCatResult pick,
        string mediaPath,
        CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            if (!string.IsNullOrWhiteSpace(pick.DirectUrl))
            {
                bytes = IsCatUrl(pick.DirectUrl!)
                    ? await GetCatBytesAsync(pick.DirectUrl!, ct).ConfigureAwait(false)
                    : await GetSharedBytesAsync(pick.DirectUrl!, ct).ConfigureAwait(false);
            }
            else
            {
                // Detail + .srt on one pinned IPv4 session (avoids a fresh TLS handshake that often RST).
                bytes = await DownloadCatSubtitleAsync(pick.DetailPath, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(FormatNetError(ex), ex);
        }

        if (!LooksLikeSubtitle(bytes))
            throw new InvalidOperationException("下载内容不是有效字幕文件。");

        var dir = Path.GetDirectoryName(mediaPath)
            ?? throw new InvalidOperationException("Invalid media path.");
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        var outPath = Path.Combine(dir, stem + ".zh.srt");
        await File.WriteAllBytesAsync(outPath, bytes, ct).ConfigureAwait(false);
        return outPath;
    }

    /// <summary>
    /// Fetch detail page then subtitle file using the same host/IP/cookies whenever possible.
    /// </summary>
    private static async Task<byte[]> DownloadCatSubtitleAsync(string detailPathOrUrl, CancellationToken ct)
    {
        var (preferredHost, detailPath) = SplitCatTarget(detailPathOrUrl);
        var hosts = OrderCatHosts(preferredHost);
        Exception? last = null;

        foreach (var host in hosts)
        {
            var detailUri = new Uri($"https://{host}/{detailPath.TrimStart('/')}");
            if (!HasSystemProxyFor(detailUri))
                break;

            try
            {
                var bytes = await DownloadCatSubtitleViaProxyAsync(host, detailPath, ct).ConfigureAwait(false);
                RememberCatRoute(host, null);
                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        foreach (var host in hosts)
        {
            IPAddress[] addrs;
            try
            {
                addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                continue;
            }

            if (addrs.Length == 0)
            {
                last = new HttpRequestException($"No IPv4 address for {host}");
                continue;
            }

            foreach (var addr in OrderCatIps(host, addrs))
            {
                var succeeded = false;
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    try
                    {
                        var bytes = await DownloadCatSubtitleViaIpAsync(host, addr, detailPath, ct)
                            .ConfigureAwait(false);
                        RememberCatRoute(host, addr);
                        succeeded = true;
                        return bytes;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (attempt < 4)
                        {
                            try
                            {
                                await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                        }
                    }
                }

                if (!succeeded)
                    ForgetCatRouteIf(host, addr);
            }
        }

        throw last ?? new HttpRequestException("SubtitleCat download unreachable");
    }

    private static async Task<byte[]> DownloadCatSubtitleViaProxyAsync(
        string host,
        string detailPath,
        CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.Zero,
            UseProxy = true,
            UseCookies = true,
            CookieContainer = cookies,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };
        using var http = CreateCatHttpClient(handler);
        return await FetchDetailThenSubtitleAsync(http, host, detailPath, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> DownloadCatSubtitleViaIpAsync(
        string host,
        IPAddress addr,
        string detailPath,
        CancellationToken ct)
    {
        var cookies = new CookieContainer();
        using var handler = CreatePinnedCatHandler(host, addr, cookies);
        using var http = CreateCatHttpClient(handler);
        return await FetchDetailThenSubtitleAsync(http, host, detailPath, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> FetchDetailThenSubtitleAsync(
        HttpClient http,
        string host,
        string detailPath,
        CancellationToken ct)
    {
        var detailUrl = BuildCatUrl(host, detailPath);
        using var detailResp = await http.GetAsync(detailUrl, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        detailResp.EnsureSuccessStatusCode();
        var detailHtml = Encoding.UTF8.GetString(
            await detailResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));

        var downloadPath = PickDownloadPath(detailHtml)
            ?? throw new InvalidOperationException("详情页没有可用字幕下载链接。");

        if (Uri.TryCreate(downloadPath, UriKind.Absolute, out var abs)
            && abs.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && !IsCatHost(abs.Host))
        {
            return await GetSharedBytesAsync(abs.AbsoluteUri, ct).ConfigureAwait(false);
        }

        var srtUrl = ResolveCatDownloadUrl(host, downloadPath);
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, srtUrl);
                req.Headers.TryAddWithoutValidation("Referer", detailUrl);
                using var srtResp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                srtResp.EnsureSuccessStatusCode();
                return await srtResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < 3)
                    await Task.Delay(200 * attempt, ct).ConfigureAwait(false);
            }
        }

        // Same TLS session failed — fall back to the full host/IP retry matrix for the .srt only.
        try
        {
            return await GetCatBytesAsync(downloadPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw last ?? ex;
        }
    }

    private static string BuildCatUrl(string host, string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs)
            && abs.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (IsCatHost(abs.Host))
                return new Uri(new Uri($"https://{host}/"), abs.PathAndQuery.TrimStart('/')).AbsoluteUri;
            return abs.AbsoluteUri;
        }

        // Uri combines & percent-encodes spaces in filenames (common on SubtitleCat).
        return new Uri(new Uri($"https://{host}/"), pathOrUrl.TrimStart('/')).AbsoluteUri;
    }

    private static string ResolveCatDownloadUrl(string currentHost, string downloadPathOrUrl)
        => BuildCatUrl(currentHost, downloadPathOrUrl);

    private static bool IsCatHost(string host)
        => CatHosts.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));

    private static bool IsCatUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
           && IsCatHost(u.Host);

    private static async Task<IReadOnlyList<SubtitleCatResult>> SearchSubtitleCatAsync(
        string keyword,
        CancellationToken ct)
    {
        var path = "/index.php?search=" + Uri.EscapeDataString(keyword);
        var html = await GetCatStringAsync(path, ct).ConfigureAwait(false);
        return ParseSearchResults(html);
    }

    private static async Task<IReadOnlyList<SubtitleCatResult>> SearchXunleiAsync(
        string keyword,
        CancellationToken ct)
    {
        var url = $"{XunleiApi}?gcid=&cid=&name={Uri.EscapeDataString(keyword)}";
        var json = await GetSharedStringAsync(url, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<SubtitleCatResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                continue;
            var direct = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(direct) || !seen.Add(direct))
                continue;

            var ext = item.TryGetProperty("ext", out var extEl) ? extEl.GetString() ?? "srt" : "srt";
            if (!ext.Equals("srt", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals("ass", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals("ssa", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals("vtt", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
                name = keyword + "." + ext;

            var extra = item.TryGetProperty("extra_name", out var extraEl) ? extraEl.GetString() : null;
            var title = string.IsNullOrWhiteSpace(extra) ? name! : $"{name} {extra}";

            list.Add(new SubtitleCatResult(
                Title: title,
                DetailPath: direct!,
                Size: "",
                Downloads: 0,
                Source: "迅雷",
                DirectUrl: direct));
        }

        return list;
    }

    private static int ScoreResult(SubtitleCatResult r, MediaSearchQuery query, string? code, string primary)
    {
        var title = r.Title;
        var score = 0;

        if (!string.IsNullOrEmpty(code))
        {
            var compact = code.Replace("-", "", StringComparison.Ordinal);
            if (title.Contains(code, StringComparison.OrdinalIgnoreCase)
                || title.Replace("-", "", StringComparison.OrdinalIgnoreCase)
                    .Contains(compact, StringComparison.OrdinalIgnoreCase))
                score += 120;
        }

        if (title.Contains(primary, StringComparison.OrdinalIgnoreCase))
            score += 40;

        if (ContainsChineseHint(title))
            score += 35;

        if (string.Equals(r.Source, "SubtitleCat", StringComparison.OrdinalIgnoreCase))
            score += 5;

        if (r.Downloads >= 50) score += 20;
        else if (r.Downloads >= 10) score += 12;
        else if (r.Downloads >= 3) score += 6;

        var sizeKb = ParseSizeKb(r.Size);
        if (sizeKb >= 8) score += 8;
        else if (r.Size.Length > 0 && sizeKb < 1) score -= 25;

        if (query.IsCatalogCode && SeasonEpisodePattern.IsMatch(title))
            score -= 15;

        return score;
    }

    private static bool ContainsChineseHint(string title)
    {
        if (title.Contains("中文", StringComparison.Ordinal)) return true;
        if (title.Contains("简体", StringComparison.Ordinal) || title.Contains("繁体", StringComparison.Ordinal))
            return true;
        if (title.Contains("网友上传", StringComparison.Ordinal)) return true;

        var lower = title.ToLowerInvariant();
        return lower.Contains("zh-cn")
            || lower.Contains("zh-tw")
            || lower.Contains(".zh")
            || lower.Contains("-zh")
            || lower.Contains(" chs")
            || lower.Contains(" cht")
            || lower.Contains(" chi");
    }

    private static IReadOnlyList<SubtitleCatResult> ParseSearchResults(string html)
    {
        var list = new List<SubtitleCatResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in HrefSubsRegex.Matches(html))
        {
            var path = WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            var title = WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
            if (path.Length == 0 || title.Length == 0) continue;
            if (!seen.Add(path)) continue;

            // Best-effort: look at a window after the match for SIZE / downloads.
            var windowStart = m.Index;
            var windowLen = Math.Min(800, html.Length - windowStart);
            var window = html.AsSpan(windowStart, windowLen).ToString();

            var size = "";
            var sizeMatch = SizeRegex.Match(window);
            if (sizeMatch.Success)
                size = sizeMatch.Groups[1].Value.Trim();

            var downloads = 0;
            var dlMatch = DownloadsRegex.Match(window);
            if (dlMatch.Success && int.TryParse(dlMatch.Groups[1].Value, out var n))
                downloads = n;

            list.Add(new SubtitleCatResult(title, path, size, downloads, Source: "SubtitleCat"));
        }

        return list;
    }

    private static string? PickDownloadPath(string detailHtml)
    {
        var langs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in DownloadAnchorRegex.Matches(detailHtml))
            langs[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);
        foreach (Match m in DownloadAnchorAltRegex.Matches(detailHtml))
            langs[m.Groups[2].Value] = WebUtility.HtmlDecode(m.Groups[1].Value);

        foreach (var code in PreferredLangCodes)
        {
            if (langs.TryGetValue(code, out var href) && href.Length > 0)
                return href;
        }

        foreach (var (code, href) in langs)
        {
            if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && href.Length > 0)
                return href;
        }

        var zh = ZhSrtHrefRegex.Match(detailHtml);
        if (zh.Success)
            return WebUtility.HtmlDecode(zh.Groups[1].Value);

        var any = AnySrtHrefRegex.Match(detailHtml);
        if (any.Success)
            return WebUtility.HtmlDecode(any.Groups[1].Value);

        return langs.Values.FirstOrDefault(v => v.Length > 0);
    }

    private static double ParseSizeKb(string size)
    {
        if (string.IsNullOrWhiteSpace(size)) return 0;
        var m = Regex.Match(size, @"([\d.]+)\s*([KMG]?B)", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, out var n)) return 0;
        var unit = m.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "GB" => n * 1024 * 1024,
            "MB" => n * 1024,
            "KB" => n,
            "B" => n / 1024.0,
            _ => n,
        };
    }

    private static bool LooksLikeSubtitle(byte[] bytes)
    {
        if (bytes.Length < 20) return false;
        var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 256)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("{", StringComparison.Ordinal))
            return false;
        return head.Contains("-->", StringComparison.Ordinal)
            || head.Contains("WEBVTT", StringComparison.Ordinal)
            || head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(head, @"^\d+\s*$", RegexOptions.Multiline);
    }

    private static string FormatNetError(Exception ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("证书", StringComparison.Ordinal)
            || msg.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("远程主机", StringComparison.Ordinal)
            || msg.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
            return "无法建立安全连接（网络可能拦截 subtitlecat.com）";
        return msg.Length > 120 ? msg[..120] + "…" : msg;
    }

    private static async Task<string> GetCatStringAsync(string pathOrUrl, CancellationToken ct)
    {
        var bytes = await GetCatAsync(pathOrUrl, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static Task<byte[]> GetCatBytesAsync(string pathOrUrl, CancellationToken ct)
        => GetCatAsync(pathOrUrl, ct);

    private static async Task<byte[]> GetCatAsync(string pathOrUrl, CancellationToken ct)
    {
        var (preferredHost, pathAndQuery) = SplitCatTarget(pathOrUrl);
        var hosts = OrderCatHosts(preferredHost);
        Exception? last = null;

        // Prefer system proxy (Clash / VPN) when configured — ConnectCallback would bypass it.
        foreach (var host in hosts)
        {
            var uri = new Uri(BuildCatUrl(host, pathAndQuery));
            if (!HasSystemProxyFor(uri))
                break;

            try
            {
                var bytes = await FetchCatViaProxyAsync(uri, ct).ConfigureAwait(false);
                RememberCatRoute(host, null);
                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        foreach (var host in hosts)
        {
            IPAddress[] addrs;
            try
            {
                addrs = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                continue;
            }

            if (addrs.Length == 0)
            {
                last = new HttpRequestException($"No IPv4 address for {host}");
                continue;
            }

            foreach (var addr in OrderCatIps(host, addrs))
            {
                var succeeded = false;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        var bytes = await FetchCatViaIpAsync(host, addr, pathAndQuery, ct).ConfigureAwait(false);
                        RememberCatRoute(host, addr);
                        succeeded = true;
                        return bytes;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (attempt < 3)
                        {
                            try
                            {
                                await Task.Delay(250 * attempt, ct).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                        }
                    }
                }

                if (!succeeded)
                    ForgetCatRouteIf(host, addr);
            }
        }

        throw last ?? new HttpRequestException("SubtitleCat unreachable");
    }

    private static bool HasSystemProxyFor(Uri uri)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            if (proxy.IsBypassed(uri))
                return false;
            var via = proxy.GetProxy(uri);
            return via is not null && via != uri;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<byte[]> FetchCatViaProxyAsync(Uri uri, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.Zero,
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(35),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        ApplyBrowserHeaders(http);
        using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> FetchCatViaIpAsync(
        string host,
        IPAddress addr,
        string pathAndQuery,
        CancellationToken ct)
    {
        using var handler = CreatePinnedCatHandler(host, addr, cookies: null);
        using var http = CreateCatHttpClient(handler);
        var url = BuildCatUrl(host, pathAndQuery);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static SocketsHttpHandler CreatePinnedCatHandler(
        string host,
        IPAddress addr,
        CookieContainer? cookies)
    {
        return new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero,
            UseProxy = false,
            UseCookies = cookies is not null,
            CookieContainer = cookies ?? new CookieContainer(),
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            ConnectCallback = async (ctx, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
                    linked.CancelAfter(TimeSpan.FromSeconds(8));
                    await socket.ConnectAsync(new IPEndPoint(addr, 443), linked.Token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    private static HttpClient CreateCatHttpClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        ApplyBrowserHeaders(http);
        return http;
    }

    private static (string? Host, string PathAndQuery) SplitCatTarget(string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var abs)
            && abs.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var path = string.IsNullOrEmpty(abs.PathAndQuery) ? "/" : abs.PathAndQuery;
            return (abs.Host, path);
        }

        return (null, pathOrUrl.StartsWith('/') ? pathOrUrl : "/" + pathOrUrl);
    }

    private static IEnumerable<string> OrderCatHosts(string? preferred)
    {
        string? lastHost;
        lock (CatRouteLock)
            lastHost = _lastGoodCatHost;

        var list = new List<string>(CatHosts.Length + 1);
        void Add(string? h)
        {
            if (string.IsNullOrWhiteSpace(h)) return;
            if (!list.Contains(h, StringComparer.OrdinalIgnoreCase))
                list.Add(h);
        }

        Add(preferred);
        Add(lastHost);
        foreach (var h in CatHosts)
            Add(h);
        return list;
    }

    private static IEnumerable<IPAddress> OrderCatIps(string host, IPAddress[] addrs)
    {
        IPAddress? lastIp;
        string? lastHost;
        lock (CatRouteLock)
        {
            lastIp = _lastGoodCatIp;
            lastHost = _lastGoodCatHost;
        }

        if (lastIp is not null
            && lastHost is not null
            && lastHost.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            yield return lastIp;
            foreach (var a in addrs)
            {
                if (!a.Equals(lastIp))
                    yield return a;
            }
            yield break;
        }

        foreach (var a in addrs)
            yield return a;
    }

    private static void RememberCatRoute(string host, IPAddress? addr)
    {
        lock (CatRouteLock)
        {
            _lastGoodCatHost = host;
            if (addr is not null)
                _lastGoodCatIp = addr;
        }
    }

    private static void ForgetCatRouteIf(string host, IPAddress addr)
    {
        lock (CatRouteLock)
        {
            if (_lastGoodCatIp is not null
                && _lastGoodCatIp.Equals(addr)
                && string.Equals(_lastGoodCatHost, host, StringComparison.OrdinalIgnoreCase))
            {
                _lastGoodCatIp = null;
            }
        }
    }

    private static async Task<string> GetSharedStringAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> GetSharedBytesAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyBrowserHeaders(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            + "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept", "text/html,application/xhtml+xml,application/json,*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    private static HttpClient CreateSharedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        ApplyBrowserHeaders(client);
        return client;
    }
}
