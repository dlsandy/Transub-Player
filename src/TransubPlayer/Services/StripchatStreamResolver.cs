using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal sealed record ResolvedNetworkStream(
    string StreamUrl,
    string PageUrl,
    string DisplayName,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<StreamQualityOption> Qualities,
    string SelectedQualityId,
    string? MasterPlaylistUrl = null);

/// <summary>Resolves Stripchat model pages (and common proxy pages) to HLS URLs.</summary>
internal static class StripchatStreamResolver
{
    private static readonly HttpClient Http = CreateClient();

    internal static HttpClient SharedHttp => Http;

    private static readonly Regex StripchatPageRe = new(
        @"https?://(?:[\w-]+\.)*stripchat\.[a-z.]+/(?<user>[A-Za-z0-9_-]+(?:@xh)?)(?:[/?#]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StripchatLinkRe = new(
        @"https?://(?:[\w-]+\.)*stripchat\.[a-z.]+/(?<user>[A-Za-z0-9_-]+(?:@xh)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareHandleRe = new(
        @"^[A-Za-z0-9_-]+(?:@xh)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PreloadStateRe = new(
        @"window\.__PRELOADED_STATE__\s*=\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex M3u8Re = new(
        @"https?://[^\s""'<>]+\.m3u8[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "api", "tags", "top", "login", "signup", "register", "help", "about", "girls", "men",
        "couples", "trans", "watch", "favorites", "videos", "static", "assets", "blog", "affiliate",
        "support", "terms", "privacy", "dmca", "2257", "cookies", "app", "mobile", "vr",
    };

    private const string OfficialOrigin = "https://stripchat.com";
    /// <summary>Reachability fallback when stripchat.com is blocked or times out.</summary>
    private const string GlobalMirrorOrigin = "https://zt.stripchat.global";

    public static bool IsStripchatPage(string? url) => TryParsePageUrl(url, out _);

    /// <summary>
    /// Bare streamer username / model id (no scheme) → canonical Stripchat page URL.
    /// Examples: <c>alice</c>, <c>alice@xh</c>, <c>12345678</c>.
    /// </summary>
    public static bool TryNormalizeBareHandle(string? input, out string pageUrl)
    {
        pageUrl = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var t = input.Trim().TrimStart('@');
        if (t.Contains("://", StringComparison.Ordinal) || t.Contains('.') || t.Contains('\\') || t.Contains('/'))
            return false;
        if (!BareHandleRe.IsMatch(t)) return false;
        if (IsReservedUsername(t)) return false;
        pageUrl = PrimaryStripchatOrigin() + "/" + t;
        return true;
    }

    public static bool MayResolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !MediaSourceHelper.IsRemoteUrl(url)) return false;
        if (StreamMediaResolver.IsDirectStreamUrl(url)) return false;
        if (MediaSourceHelper.IsScreenCapture(url)) return false;
        if (IsStripchatPage(url)) return true;
        if (url.Contains("stripchat", StringComparison.OrdinalIgnoreCase)) return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrEmpty(ext)
            || ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".php", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".asp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aspx", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static async Task<ResolvedNetworkStream> ResolveAsync(string pageUrl, CancellationToken ct)
    {
        if (!MediaSourceHelper.TryNormalizeMedia(pageUrl, out var normalized))
            throw new StreamResolveException(Loc.Get("StreamResolve.InvalidUrl"));

        pageUrl = normalized;
        var referer = pageUrl;

        if (TryParsePageUrl(pageUrl, out var directUser))
            return await ResolveUsernameAsync(directUser, pageUrl, referer, ct).ConfigureAwait(false);

        var html = await FetchTextAsync(pageUrl, referer, ct).ConfigureAwait(false);

        foreach (Match link in StripchatLinkRe.Matches(html))
        {
            var user = link.Groups["user"].Value;
            if (IsReservedUsername(user)) continue;
            try
            {
                var scUrl = link.Value.Contains("stripchat", StringComparison.OrdinalIgnoreCase)
                    ? link.Value
                    : $"{PrimaryStripchatOrigin()}/{user}";
                return await ResolveUsernameAsync(user, scUrl, referer, ct, html).ConfigureAwait(false);
            }
            catch (StreamResolveException ex) when (ex.Kind is not StreamResolveKind.Generic)
            {
                throw;
            }
            catch
            {
                // try next embedded link
            }
        }

        if (TryParsePreloadedState(html, out var preloadUser, out var preloadModelId, out var hosts, out var isLive, out var preloadStatus)
            && (!string.IsNullOrWhiteSpace(preloadModelId) || !string.IsNullOrWhiteSpace(preloadStatus)))
        {
            if (StripchatRoomStatus.TryUnplayableException(
                    preloadUser, preloadStatus, available: isLive, isLive: isLive, camActive: isLive,
                    streamName: preloadModelId) is { } preloadFail)
                throw preloadFail;

            if (!isLive || string.IsNullOrWhiteSpace(preloadModelId))
                throw StripchatRoomStatus.ToException(StripchatRoomKind.Offline, preloadUser);

            var streamUrl = BuildHlsUrl(preloadModelId, hosts);
            return await CreateResultAsync(streamUrl, pageUrl, referer, preloadUser, ct,
                streamName: preloadModelId).ConfigureAwait(false);
        }

        var m3u8 = FindM3u8(html);
        if (m3u8 is not null)
            return await CreateResultAsync(m3u8, pageUrl, referer, MediaSourceHelper.DisplayName(pageUrl), ct).ConfigureAwait(false);

        throw new StreamResolveException(Loc.Get("StreamResolve.Stripchat.NotFound"));
    }

    private static async Task<ResolvedNetworkStream> ResolveUsernameAsync(
        string username,
        string stripchatUrl,
        string referer,
        CancellationToken ct,
        string? prefetchedHtml = null)
    {
        var apiUser = ApiUsername(username);
        var apiBases = BuildApiBases(referer, stripchatUrl);
        CamApiResult? lastCam = null;
        var apiBlocked = false;

        // Numeric tokens are often model ids — try /models/{id}/cam before username lookup.
        if (apiUser.Length >= 4 && apiUser.All(char.IsDigit))
        {
            foreach (var apiBase in apiBases)
            {
                var apiReferer = RefererForApiBase(apiBase, apiUser, referer);
                var idAttempt = await TryResolveCamFromApiAsync(apiBase, apiUser, apiReferer, stripchatUrl, ct, byModelId: true)
                    .ConfigureAwait(false);
                if (idAttempt.Blocked) apiBlocked = true;
                if (TryTakePlayable(idAttempt.Result, username, apiBase, apiUser, stripchatUrl, apiReferer, ct, ref lastCam) is { } playable)
                    return await playable.ConfigureAwait(false);
            }
        }

        foreach (var apiBase in apiBases)
        {
            var apiReferer = RefererForApiBase(apiBase, apiUser, referer);
            var usernameAttempt = await TryResolveCamFromApiAsync(apiBase, apiUser, apiReferer, stripchatUrl, ct).ConfigureAwait(false);
            if (usernameAttempt.Blocked) apiBlocked = true;
            if (TryTakePlayable(usernameAttempt.Result, username, apiBase, apiUser, stripchatUrl, apiReferer, ct, ref lastCam) is { } playable)
                return await playable.ConfigureAwait(false);

            // Mirror sites often return 418 for username/cam but allow user-ids + model-id/cam.
            if (await TryFetchUserIdAsync(apiBase, apiUser, apiReferer, ct).ConfigureAwait(false) is { } userId)
            {
                var idAttempt = await TryResolveCamFromApiAsync(apiBase, userId.ToString(), apiReferer, stripchatUrl, ct, byModelId: true).ConfigureAwait(false);
                if (idAttempt.Blocked) apiBlocked = true;
                if (TryTakePlayable(idAttempt.Result, username, apiBase, apiUser, stripchatUrl, apiReferer, ct, ref lastCam) is { } byIdPlayable)
                    return await byIdPlayable.ConfigureAwait(false);
            }
        }

        // Known room state from API — do not invent HLS from stale HTML.
        if (lastCam is { } knownCam
            && StripchatRoomStatus.TryUnplayableException(
                username, knownCam.Status, knownCam.Available, knownCam.IsLive, knownCam.CamActive,
                knownCam.IsDeleted, knownCam.IsGeoBanned, knownCam.ApiError, knownCam.StreamName) is { } knownFail)
            throw knownFail;

        var htmlSources = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefetchedHtml))
            htmlSources.Add(prefetchedHtml);
        if (ShouldTryOfficialFallbacks(referer, stripchatUrl))
        {
            foreach (var origin in PreferredStripchatOrigins())
                htmlSources.Add($"{origin}/{Uri.EscapeDataString(apiUser)}");
        }

        htmlSources.Add(stripchatUrl);
        if (Uri.TryCreate(referer, UriKind.Absolute, out _)
            && !string.Equals(stripchatUrl, referer, StringComparison.OrdinalIgnoreCase))
            htmlSources.Add(referer);

        string? html = null;
        string? htmlFromUrl = null;
        foreach (var source in htmlSources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    html = await FetchTextAsync(source, source, ct).ConfigureAwait(false);
                    htmlFromUrl = source;
                    break;
                }
                catch (Exception ex) { PlayerLog.Write("Stripchat page: " + ex.Message); }
            }
            else
            {
                html = source;
                break;
            }
        }

        var resultPage = htmlFromUrl ?? stripchatUrl;
        var resultReferer = htmlFromUrl ?? referer;

        if (html is not null
            && TryParsePreloadedState(html, out var preloadUser, out var modelId, out var hosts, out var modelLive, out var preloadStatus))
        {
            var display = string.IsNullOrWhiteSpace(preloadUser) ? username : preloadUser;
            if (StripchatRoomStatus.TryUnplayableException(
                    display, preloadStatus, available: modelLive, isLive: modelLive, camActive: modelLive,
                    streamName: modelId) is { } preloadFail)
                throw preloadFail;

            if (!modelLive)
                throw StripchatRoomStatus.ToException(StripchatRoomKind.Offline, display);

            if (!string.IsNullOrWhiteSpace(modelId))
            {
                var streamUrl = BuildHlsUrl(modelId, hosts);
                return await CreateResultAsync(streamUrl, resultPage, resultReferer, display, ct,
                    streamName: modelId).ConfigureAwait(false);
            }
        }

        var m3u8 = html is not null ? FindM3u8(html) : null;
        if (m3u8 is not null)
            return await CreateResultAsync(m3u8, resultPage, resultReferer, username, ct).ConfigureAwait(false);

        if (apiBlocked && lastCam is null)
            throw new StreamResolveException(Loc.Get("StreamResolve.Stripchat.Blocked"));

        throw StripchatRoomStatus.ToException(StripchatRoomKind.Offline, username);
    }

    private static Task<ResolvedNetworkStream>? TryTakePlayable(
        CamApiResult? cam,
        string username,
        string apiBase,
        string apiUser,
        string stripchatUrl,
        string apiReferer,
        CancellationToken ct,
        ref CamApiResult? lastCam)
    {
        if (cam is null) return null;
        lastCam = PreferRicherCam(lastCam, cam);
        if (!StripchatRoomStatus.IsPlayable(
                cam.StreamName, cam.Status, cam.Available, cam.IsLive, cam.CamActive, cam.IsDeleted, cam.IsGeoBanned))
            return null;

        var streamUrl = PickHlsUrl(cam);
        var page = PageUrlForApiBase(apiBase, apiUser, stripchatUrl);
        return CreateResultAsync(streamUrl, page, apiReferer, username, ct,
            cam.StreamName, cam.ViewServer, cam.HlsPlaylist);
    }

    private static CamApiResult PreferRicherCam(CamApiResult? previous, CamApiResult next)
    {
        if (previous is null) return next;
        // Prefer a result that carries an explicit status / geo / delete signal.
        var prevScore = (string.IsNullOrWhiteSpace(previous.Status) ? 0 : 2)
                        + (previous.IsGeoBanned || previous.IsDeleted ? 3 : 0)
                        + (string.IsNullOrWhiteSpace(previous.ApiError) ? 0 : 2);
        var nextScore = (string.IsNullOrWhiteSpace(next.Status) ? 0 : 2)
                        + (next.IsGeoBanned || next.IsDeleted ? 3 : 0)
                        + (string.IsNullOrWhiteSpace(next.ApiError) ? 0 : 2);
        return nextScore >= prevScore ? next : previous;
    }

    private sealed record CamApiResult(
        string StreamName,
        string? ViewServer,
        string? HlsPlaylist,
        bool IsLive,
        string Status,
        bool Available,
        bool CamActive,
        bool IsDeleted = false,
        bool IsGeoBanned = false,
        string? ApiError = null);

    private readonly record struct CamApiAttempt(CamApiResult? Result, bool Blocked);

    private static async Task<CamApiAttempt> TryResolveCamFromApiAsync(
        string apiBase,
        string userOrId,
        string referer,
        string stripchatUrl,
        CancellationToken ct,
        bool byModelId = false)
    {
        var path = byModelId
            ? $"/api/front/v2/models/{Uri.EscapeDataString(userOrId)}/cam"
            : $"/api/front/v2/models/username/{Uri.EscapeDataString(userOrId)}/cam";
        var apiUrl = $"{apiBase}{path}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            ApplyHeaders(req, referer, forApi: true);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if ((int)res.StatusCode == 418)
                return new CamApiAttempt(null, true);

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (TryParseApiError(doc.RootElement, out var errResult))
                return new CamApiAttempt(errResult, false);
            if (!res.IsSuccessStatusCode) return default;
            if (!TryParseApi(doc.RootElement, out var parsed))
                return default;

            return new CamApiAttempt(parsed, false);
        }
        catch (Exception ex)
        {
            if (!ShouldSuppressApiError(apiBase, referer, stripchatUrl))
                PlayerLog.Write($"Stripchat API ({apiUrl}): " + ex.Message);
            return default;
        }
    }

    private static async Task<long?> TryFetchUserIdAsync(string apiBase, string username, string referer, CancellationToken ct)
    {
        var apiUrl = $"{apiBase}/api/front/users/user-ids/{Uri.EscapeDataString(username)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            ApplyHeaders(req, referer, forApi: true);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("id", out var id) && id.TryGetInt64(out var modelId))
                return modelId;
        }
        catch (Exception ex)
        {
            PlayerLog.Write($"Stripchat user-id ({apiUrl}): " + ex.Message);
        }

        return null;
    }

    private static IReadOnlyList<string> BuildApiBases(string referer, string stripchatUrl)
    {
        var bases = new List<string>();
        void Add(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return;
            var root = $"{uri.Scheme}://{uri.Host}";
            if (!bases.Contains(root, StringComparer.OrdinalIgnoreCase))
                bases.Add(root);
        }

        // UI language decides try order: zh → zt.stripchat.global first; en → stripchat.com first.
        if (ShouldTryOfficialFallbacks(referer, stripchatUrl))
        {
            foreach (var origin in PreferredStripchatOrigins())
                Add(origin);
        }

        Add(referer);
        Add(stripchatUrl);
        return bases;
    }

    /// <summary>
    /// Chinese UI: global mirror then official. English (and other) UI: official then global mirror.
    /// </summary>
    private static IReadOnlyList<string> PreferredStripchatOrigins()
        => PrefersChineseStripchatMirror()
            ? [GlobalMirrorOrigin, OfficialOrigin]
            : [OfficialOrigin, GlobalMirrorOrigin];

    private static string PrimaryStripchatOrigin()
        => PreferredStripchatOrigins()[0];

    private static bool PrefersChineseStripchatMirror()
        => Loc.CurrentTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldTryOfficialFallbacks(string referer, string stripchatUrl)
        => UsesOfficialSite(referer, stripchatUrl)
           || IsGlobalMirrorHost(referer)
           || IsGlobalMirrorHost(stripchatUrl)
           || (IsStripchatFamilyHost(referer) && !IsMirrorSession(referer, stripchatUrl));

    private static bool UsesOfficialSite(string referer, string stripchatUrl)
        => IsOfficialStripchatHost(referer) || IsOfficialStripchatHost(stripchatUrl);

    private static bool IsOfficialStripchatHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Host.Equals("stripchat.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGlobalMirrorHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Host.Equals("zt.stripchat.global", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStripchatFamilyHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri.Host.Contains("stripchat", StringComparison.OrdinalIgnoreCase);
    }

    private static string RefererForApiBase(string apiBase, string apiUser, string fallbackReferer)
    {
        if (string.Equals(apiBase, GlobalMirrorOrigin, StringComparison.OrdinalIgnoreCase)
            && !IsGlobalMirrorHost(fallbackReferer))
            return $"{GlobalMirrorOrigin}/{Uri.EscapeDataString(apiUser)}";
        if (string.Equals(apiBase, OfficialOrigin, StringComparison.OrdinalIgnoreCase)
            && !IsOfficialStripchatHost(fallbackReferer)
            && !IsGlobalMirrorHost(fallbackReferer))
            return $"{OfficialOrigin}/{Uri.EscapeDataString(apiUser)}";
        return fallbackReferer;
    }

    private static string PageUrlForApiBase(string apiBase, string apiUser, string fallbackPage)
    {
        if (string.Equals(apiBase, GlobalMirrorOrigin, StringComparison.OrdinalIgnoreCase))
            return $"{GlobalMirrorOrigin}/{Uri.EscapeDataString(apiUser)}";
        if (string.Equals(apiBase, OfficialOrigin, StringComparison.OrdinalIgnoreCase)
            && !IsOfficialStripchatHost(fallbackPage))
            return $"{OfficialOrigin}/{Uri.EscapeDataString(apiUser)}";
        return fallbackPage;
    }

    private static bool IsMirrorSession(string referer, string stripchatUrl)
        => !UsesOfficialSite(referer, stripchatUrl)
           && !IsGlobalMirrorHost(referer)
           && !IsGlobalMirrorHost(stripchatUrl)
           && (referer.Contains("stripchat", StringComparison.OrdinalIgnoreCase)
               || stripchatUrl.Contains("stripchat", StringComparison.OrdinalIgnoreCase));

    private static bool ShouldSuppressApiError(string apiBase, string referer, string stripchatUrl)
        => IsMirrorSession(referer, stripchatUrl)
           && (apiBase.Contains("stripchat.com", StringComparison.OrdinalIgnoreCase)
               || apiBase.Contains("zt.stripchat.global", StringComparison.OrdinalIgnoreCase));

    private static bool TryParsePageUrl(string? url, out string username)
    {
        username = "";
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();

        var m = StripchatPageRe.Match(trimmed);
        if (m.Success)
        {
            username = m.Groups["user"].Value;
            return !IsReservedUsername(username);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Contains("stripchat", StringComparison.OrdinalIgnoreCase)) return false;

        var segment = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(segment) || IsReservedUsername(segment)) return false;
        username = segment;
        return true;
    }

    private static bool IsReservedUsername(string username)
    {
        var bare = ApiUsername(username);
        return ReservedPaths.Contains(bare);
    }

    private static string ApiUsername(string username)
    {
        var i = username.IndexOf('@');
        return i > 0 ? username[..i] : username;
    }

    private static bool TryParseApiError(JsonElement root, out CamApiResult? result)
    {
        result = null;
        string? error = null;
        if (root.TryGetProperty("error", out var errEl))
            error = errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : errEl.GetRawText();
        if (string.IsNullOrWhiteSpace(error)) return false;

        var deleted = StripchatRoomStatus.IsNotFoundError(error);
        result = new CamApiResult(
            StreamName: "",
            ViewServer: null,
            HlsPlaylist: null,
            IsLive: false,
            Status: "",
            Available: false,
            CamActive: false,
            IsDeleted: deleted,
            ApiError: error);
        return true;
    }

    private static bool TryParseApi(JsonElement root, out CamApiResult result)
    {
        result = new CamApiResult("", null, null, false, "", false, false);

        string streamName = "";
        string? viewServer = null;
        string? hlsPlaylist = null;
        var isLive = false;
        var status = "";
        var available = false;
        var camActive = false;
        var isDeleted = false;
        var isGeoBanned = false;

        if (root.TryGetProperty("cam", out var cam) && cam.ValueKind == JsonValueKind.Object)
        {
            if (cam.TryGetProperty("streamName", out var sn))
                streamName = sn.GetString() ?? "";
            if (cam.TryGetProperty("isCamAvailable", out var avail) && avail.ValueKind is JsonValueKind.True or JsonValueKind.False)
                available = avail.GetBoolean();
            if (cam.TryGetProperty("isCamActive", out var active) && active.ValueKind is JsonValueKind.True or JsonValueKind.False)
                camActive = active.GetBoolean();
            if (cam.TryGetProperty("hlsPlaylist", out var pl))
                hlsPlaylist = pl.GetString();
            viewServer = StripchatLiveCdn.HlsNodeFromCam(cam);
        }

        JsonElement user = default;
        if (root.TryGetProperty("user", out var userWrap))
        {
            if (userWrap.TryGetProperty("isGeoBanned", out var geo) && geo.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isGeoBanned = geo.GetBoolean();
            if (userWrap.TryGetProperty("user", out var u))
                user = u;
        }

        if (user.ValueKind == JsonValueKind.Object)
        {
            if (user.TryGetProperty("isLive", out var live) && live.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isLive = live.GetBoolean();
            if (user.TryGetProperty("status", out var st))
                status = st.GetString() ?? "";
            if (user.TryGetProperty("isDeleted", out var del) && del.ValueKind is JsonValueKind.True or JsonValueKind.False)
                isDeleted = del.GetBoolean();
            if (string.IsNullOrWhiteSpace(streamName) && user.TryGetProperty("id", out var id))
                streamName = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "";
        }

        // Need at least status / cam flags / stream identity to be useful.
        if (string.IsNullOrWhiteSpace(streamName)
            && string.IsNullOrWhiteSpace(status)
            && !available && !camActive && !isLive && !isDeleted && !isGeoBanned)
            return false;

        result = new CamApiResult(streamName, viewServer, hlsPlaylist, isLive, status, available, camActive, isDeleted, isGeoBanned);
        return true;
    }

    private static bool TryParsePreloadedState(
        string html,
        out string username,
        out string modelId,
        out IReadOnlyList<string> hosts,
        out bool isLive,
        out string status)
    {
        username = "";
        modelId = "";
        hosts = [];
        isLive = false;
        status = "";

        var m = PreloadStateRe.Match(html);
        if (!m.Success) return false;
        var start = m.Index + m.Length;
        if (!TryExtractJsonObject(html, start, out var json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("viewCam", out var viewCam)
                && viewCam.TryGetProperty("model", out var model))
            {
                if (model.TryGetProperty("username", out var un))
                    username = un.GetString() ?? "";
                if (model.TryGetProperty("id", out var id))
                    modelId = id.ValueKind == JsonValueKind.Number
                        ? id.GetRawText()
                        : id.GetString() ?? "";
                if (model.TryGetProperty("isLive", out var live) && live.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    isLive = live.GetBoolean();
                if (model.TryGetProperty("status", out var st))
                    status = st.GetString() ?? "";
            }

            hosts = ExtractHlsHosts(root);
            return !string.IsNullOrWhiteSpace(modelId) || !string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(username);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ExtractHlsHosts(JsonElement root)
    {
        var hosts = new List<string>();
        void Walk(JsonElement el, int depth)
        {
            if (depth > 8) return;
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.NameEquals("hlsStreamHost") && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var h = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(h)) hosts.Add(h!);
                        }
                        else if (prop.NameEquals("fallbackDomains") && prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    var h = item.GetString();
                                    if (!string.IsNullOrWhiteSpace(h)) hosts.Add(h!);
                                }
                            }
                        }
                        else
                            Walk(prop.Value, depth + 1);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        Walk(item, depth + 1);
                    break;
            }
        }

        Walk(root, 0);
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string BuildHlsUrl(string modelId, IReadOnlyList<string> hosts)
    {
        foreach (var host in hosts)
        {
            if (!string.IsNullOrWhiteSpace(host))
                return StripchatLiveCdn.PickPlayUrl(modelId, host);
        }

        return StripchatLiveCdn.PickPlayUrl(modelId, null);
    }

    private static string PickHlsUrl(CamApiResult cam)
        => StripchatLiveCdn.PickPlayUrl(cam.StreamName, cam.ViewServer, cam.HlsPlaylist);

    private static async Task<ResolvedNetworkStream> CreateResultAsync(
        string streamUrl,
        string pageUrl,
        string referer,
        string displayUser,
        CancellationToken ct,
        string? streamName = null,
        string? viewServer = null,
        string? hlsPlaylist = null)
    {
        var masterPlaylist = !string.IsNullOrWhiteSpace(streamName)
            ? StripchatLiveCdn.MasterPlaylistUrl(streamName, hlsPlaylist)
            : null;
        if (string.IsNullOrWhiteSpace(masterPlaylist) && StripchatHlsPlaylist.IsMasterPlaylist(streamUrl))
            masterPlaylist = streamUrl;

        var qualities = await BuildQualitiesAsync(streamUrl, referer, streamName, viewServer, hlsPlaylist, ct)
            .ConfigureAwait(false);

        var selected = qualities.FirstOrDefault(q =>
                           string.Equals(q.Url, streamUrl, StringComparison.OrdinalIgnoreCase))
                       ?? qualities.FirstOrDefault();
        if (selected is not null)
            streamUrl = selected.Url;

        try
        {
            if (StripchatHlsPlaylist.NeedsProxy(streamUrl))
                streamUrl = await StripchatHlsPlaylist.ResolvePlayUrlAsync(streamUrl, referer, ct, masterPlaylist).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PlayerLog.Write("Stripchat HLS: " + ex.Message);
        }

        // Proxied / rewritten play URLs must be reflected on options for later switches.
        var playQualities = new List<StreamQualityOption>();
        foreach (var q in qualities)
        {
            var playUrl = StripchatLiveCdn.PreferPlayableCdn(q.Url);
            try
            {
                if (StripchatHlsPlaylist.NeedsProxy(playUrl))
                    playUrl = await StripchatHlsPlaylist.ResolvePlayUrlAsync(playUrl, referer, ct, masterPlaylist).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PlayerLog.Write("Stripchat quality proxy: " + ex.Message);
                continue; // drop unusable doppiocdn qualities instead of offering a black-screen option
            }

            if (StripchatLiveCdn.IsSacdnssedge(playUrl))
            {
                try
                {
                    playUrl = await StripchatHlsPlaylist.ResolveSacdnssedgeForMpvAsync(playUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    PlayerLog.Write("Stripchat sacdnssedge resolve: " + ex.Message);
                }
            }

            playQualities.Add(q with { Url = playUrl, Kind = StripchatLiveCdn.IsSacdnssedge(playUrl) ? "sacdnssedge" : q.Kind });
        }

        var selectedId = selected?.Id
                         ?? playQualities.FirstOrDefault()?.Id
                         ?? "default";
        if (playQualities.Count == 0)
        {
            playQualities.Add(new StreamQualityOption("default", Loc.Get("Main.StreamQuality.Default"), streamUrl, "default", 0));
            selectedId = "default";
        }
        else if (!playQualities.Any(q => q.Id == selectedId))
            selectedId = playQualities[0].Id;

        var chosen = playQualities.FirstOrDefault(q => q.Id == selectedId) ?? playQualities[0];
        return CreateResult(chosen.Url, pageUrl, referer, displayUser, playQualities, chosen.Id, masterPlaylist);
    }

    private static async Task<IReadOnlyList<StreamQualityOption>> BuildQualitiesAsync(
        string primaryUrl,
        string referer,
        string? streamName,
        string? viewServer,
        string? hlsPlaylist,
        CancellationToken ct)
    {
        var list = new List<StreamQualityOption>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(StreamQualityOption opt)
        {
            if (string.IsNullOrWhiteSpace(opt.Url) || !seenUrls.Add(opt.Url)) return;
            list.Add(opt);
        }

        if (!string.IsNullOrWhiteSpace(streamName))
        {
            var mirrors = StripchatLiveCdn.BuildSacdnssedgeCandidates(streamName, viewServer);
            for (var i = 0; i < mirrors.Count; i++)
            {
                var label = i == 0
                    ? Loc.Get("Main.StreamQuality.Mirror")
                    : Loc.Format("Main.StreamQuality.MirrorN", i + 1);
                Add(new StreamQualityOption("mirror:" + i, label, mirrors[i], "sacdnssedge", 1100 - i));
            }

            var master = StripchatLiveCdn.MasterPlaylistUrl(streamName, hlsPlaylist);
            if (!string.IsNullOrWhiteSpace(master))
            {
                try
                {
                    var masterQs = await StripchatHlsPlaylist.ListMasterQualitiesAsync(master, referer, ct)
                        .ConfigureAwait(false);
                    foreach (var q in masterQs)
                        Add(q);
                }
                catch (Exception ex)
                {
                    PlayerLog.Write("Stripchat master qualities: " + ex.Message);
                    Add(new StreamQualityOption("doppio:auto", Loc.Get("Main.StreamQuality.Auto"), master!, "doppio", 900));
                }
            }
        }
        else if (StripchatHlsPlaylist.IsMasterPlaylist(primaryUrl)
                 || primaryUrl.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var masterQs = await StripchatHlsPlaylist.ListMasterQualitiesAsync(primaryUrl, referer, ct)
                    .ConfigureAwait(false);
                foreach (var q in masterQs)
                    Add(q);
            }
            catch (Exception ex)
            {
                PlayerLog.Write("Stripchat master qualities: " + ex.Message);
            }
        }

        if (list.Count == 0 && !string.IsNullOrWhiteSpace(primaryUrl))
            Add(new StreamQualityOption("default", Loc.Get("Main.StreamQuality.Default"), primaryUrl, "default", 0));

        return list
            .OrderByDescending(q => q.Rank)
            .ThenBy(q => q.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsMasterPlaylist(string url) => StripchatHlsPlaylist.IsMasterPlaylist(url);

    private static string? FindM3u8(string html)
    {
        foreach (Match m in M3u8Re.Matches(html))
        {
            var url = WebUtility.HtmlDecode(m.Value);
            if (url.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase)
                || url.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
                return url;
        }

        return null;
    }

    private static ResolvedNetworkStream CreateResult(
        string streamUrl,
        string pageUrl,
        string referer,
        string displayUser,
        IReadOnlyList<StreamQualityOption> qualities,
        string selectedQualityId,
        string? masterPlaylistUrl = null)
    {
        var playReferer = streamUrl.Contains("127.0.0.1", StringComparison.Ordinal)
            ? StripchatHlsPlaylist.CdnReferer
            : StripchatLiveCdn.PlayReferer(streamUrl);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = BrowserUserAgent,
            ["Referer"] = playReferer,
        };

        if (!StripchatLiveCdn.IsSacdnssedge(streamUrl))
        {
            headers["Origin"] = IsGlobalMirrorHost(pageUrl) || IsGlobalMirrorHost(referer)
                ? GlobalMirrorOrigin
                : OfficialOrigin;
        }

        var name = displayUser.Contains('@') ? ApiUsername(displayUser) : displayUser;
        return new ResolvedNetworkStream(
            streamUrl,
            pageUrl,
            $"Stripchat / {name}",
            headers,
            qualities,
            selectedQualityId,
            masterPlaylistUrl);
    }

    private static async Task<string> FetchTextAsync(string url, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, referer);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyHeaders(HttpRequestMessage req, string referer, bool forApi = false)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", forApi
            ? "application/json, text/plain, */*"
            : "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("Referer", referer);
        if (forApi)
        {
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
            req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        }
    }

    private static string OriginFrom(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        return $"{uri.Scheme}://{uri.Host}";
    }

    private static bool TryExtractJsonObject(string text, int start, out string json)
    {
        json = "";
        while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        if (start >= text.Length || text[start] != '{') return false;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(i + 1)];
                    return true;
                }
            }
        }

        return false;
    }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        + "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                      | System.Security.Authentication.SslProtocols.Tls13,
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
    }
}

internal enum StreamResolveKind
{
    Generic,
    Offline,
    Private,
    Restricted,
    NotFound,
}

internal sealed class StreamResolveException : Exception
{
    public StreamResolveKind Kind { get; }

    public StreamResolveException(string message, StreamResolveKind kind = StreamResolveKind.Generic)
        : base(message)
    {
        Kind = kind;
    }
}
