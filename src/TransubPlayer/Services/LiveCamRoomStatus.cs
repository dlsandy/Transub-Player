using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Maps live-cam API / preload room state to user-facing resolve errors.</summary>
internal enum LiveCamRoomKind
{
    Public,
    Offline,
    Private,
    GroupShow,
    Restricted,
    NotFound,
    Unavailable,
    Unknown,
}

internal static class LiveCamRoomStatus
{
    private static readonly HashSet<string> PrivateStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "private", "virtualPrivate", "p2p", "p2pVoice",
    };

    private static readonly HashSet<string> GroupShowStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "groupShow",
    };

    private static readonly HashSet<string> OfflineStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "off", "idle",
    };

    public static bool IsPrivateLike(string? status)
        => !string.IsNullOrWhiteSpace(status)
           && (PrivateStatuses.Contains(status) || GroupShowStatuses.Contains(status));

    public static bool IsOffline(string? status)
        => !string.IsNullOrWhiteSpace(status) && OfflineStatuses.Contains(status);

    public static bool IsPublic(string? status)
        => string.Equals(status, "public", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Playable only when status is public (or missing) and the cam is actually available.
    /// Private rooms often still report isLive=true — do not treat those as playable.
    /// </summary>
    public static bool IsPlayable(
        string? streamName,
        string? status,
        bool available,
        bool isLive,
        bool camActive,
        bool isDeleted = false,
        bool isGeoBanned = false)
    {
        if (isDeleted || isGeoBanned) return false;
        if (string.IsNullOrWhiteSpace(streamName)) return false;
        if (IsPrivateLike(status) || IsOffline(status)) return false;

        if (IsPublic(status))
            return available && camActive;

        // Mirrors sometimes omit status — fall back to cam flags / live.
        return available || camActive || isLive;
    }

    public static LiveCamRoomKind Classify(
        string? status,
        bool available,
        bool isLive,
        bool camActive,
        bool isDeleted = false,
        bool isGeoBanned = false,
        string? apiError = null)
    {
        if (isDeleted || IsNotFoundError(apiError))
            return LiveCamRoomKind.NotFound;
        if (isGeoBanned)
            return LiveCamRoomKind.Restricted;

        if (GroupShowStatuses.Contains(status ?? ""))
            return LiveCamRoomKind.GroupShow;
        if (PrivateStatuses.Contains(status ?? ""))
            return LiveCamRoomKind.Private;
        if (IsOffline(status) || (!isLive && !available && !camActive && !IsPublic(status)))
            return LiveCamRoomKind.Offline;

        if (IsPublic(status))
        {
            if (available && camActive)
                return LiveCamRoomKind.Public;
            return LiveCamRoomKind.Unavailable;
        }

        if (!string.IsNullOrWhiteSpace(status))
            return LiveCamRoomKind.Unknown;

        if (!isLive && !available && !camActive)
            return LiveCamRoomKind.Offline;

        return LiveCamRoomKind.Unknown;
    }

    public static bool IsDefinitiveUnplayable(LiveCamRoomKind kind)
        => kind is LiveCamRoomKind.Offline
            or LiveCamRoomKind.Private
            or LiveCamRoomKind.GroupShow
            or LiveCamRoomKind.Restricted
            or LiveCamRoomKind.NotFound
            or LiveCamRoomKind.Unavailable;

    public static StreamResolveException ToException(LiveCamRoomKind kind, string username, string? rawStatus = null)
    {
        var name = string.IsNullOrWhiteSpace(username) ? "?" : username.Trim();
        return kind switch
        {
            LiveCamRoomKind.Private => new StreamResolveException(
                Loc.Format("StreamResolve.Live.Private", name), StreamResolveKind.Private),
            LiveCamRoomKind.GroupShow => new StreamResolveException(
                Loc.Format("StreamResolve.Live.GroupShow", name), StreamResolveKind.Private),
            LiveCamRoomKind.Restricted => new StreamResolveException(
                Loc.Format("StreamResolve.Live.Restricted", name), StreamResolveKind.Restricted),
            LiveCamRoomKind.NotFound => new StreamResolveException(
                Loc.Format("StreamResolve.Live.ModelNotFound", name), StreamResolveKind.NotFound),
            LiveCamRoomKind.Unavailable => new StreamResolveException(
                Loc.Format("StreamResolve.Live.Unavailable", name), StreamResolveKind.Offline),
            LiveCamRoomKind.Unknown when !string.IsNullOrWhiteSpace(rawStatus) => new StreamResolveException(
                Loc.Format("StreamResolve.Live.StatusUnknown", name, rawStatus), StreamResolveKind.Generic),
            _ => new StreamResolveException(
                Loc.Format("StreamResolve.Live.Offline", name), StreamResolveKind.Offline),
        };
    }

    public static StreamResolveException? TryUnplayableException(
        string username,
        string? status,
        bool available,
        bool isLive,
        bool camActive,
        bool isDeleted = false,
        bool isGeoBanned = false,
        string? apiError = null,
        string? streamName = null)
    {
        if (IsPlayable(streamName, status, available, isLive, camActive, isDeleted, isGeoBanned))
            return null;

        var kind = Classify(status, available, isLive, camActive, isDeleted, isGeoBanned, apiError);
        if (!IsDefinitiveUnplayable(kind) && kind != LiveCamRoomKind.Unknown)
            return null;
        if (kind == LiveCamRoomKind.Unknown && string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(apiError))
            return null;

        return ToException(kind == LiveCamRoomKind.Public ? LiveCamRoomKind.Unavailable : kind, username, status);
    }

    public static bool IsNotFoundError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Equals("Not Found", StringComparison.OrdinalIgnoreCase)
               || error.Equals("Model not found", StringComparison.OrdinalIgnoreCase)
               || error.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }
}
