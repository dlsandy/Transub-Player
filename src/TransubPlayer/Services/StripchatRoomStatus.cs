using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Maps Stripchat cam API / preload room state to user-facing resolve errors.</summary>
internal enum StripchatRoomKind
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

internal static class StripchatRoomStatus
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

    public static StripchatRoomKind Classify(
        string? status,
        bool available,
        bool isLive,
        bool camActive,
        bool isDeleted = false,
        bool isGeoBanned = false,
        string? apiError = null)
    {
        if (isDeleted || IsNotFoundError(apiError))
            return StripchatRoomKind.NotFound;
        if (isGeoBanned)
            return StripchatRoomKind.Restricted;

        if (GroupShowStatuses.Contains(status ?? ""))
            return StripchatRoomKind.GroupShow;
        if (PrivateStatuses.Contains(status ?? ""))
            return StripchatRoomKind.Private;
        if (IsOffline(status) || (!isLive && !available && !camActive && !IsPublic(status)))
            return StripchatRoomKind.Offline;

        if (IsPublic(status))
        {
            if (available && camActive)
                return StripchatRoomKind.Public;
            return StripchatRoomKind.Unavailable;
        }

        if (!string.IsNullOrWhiteSpace(status))
            return StripchatRoomKind.Unknown;

        if (!isLive && !available && !camActive)
            return StripchatRoomKind.Offline;

        return StripchatRoomKind.Unknown;
    }

    public static bool IsDefinitiveUnplayable(StripchatRoomKind kind)
        => kind is StripchatRoomKind.Offline
            or StripchatRoomKind.Private
            or StripchatRoomKind.GroupShow
            or StripchatRoomKind.Restricted
            or StripchatRoomKind.NotFound
            or StripchatRoomKind.Unavailable;

    public static StreamResolveException ToException(StripchatRoomKind kind, string username, string? rawStatus = null)
    {
        var name = string.IsNullOrWhiteSpace(username) ? "?" : username.Trim();
        return kind switch
        {
            StripchatRoomKind.Private => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.Private", name), StreamResolveKind.Private),
            StripchatRoomKind.GroupShow => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.GroupShow", name), StreamResolveKind.Private),
            StripchatRoomKind.Restricted => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.Restricted", name), StreamResolveKind.Restricted),
            StripchatRoomKind.NotFound => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.ModelNotFound", name), StreamResolveKind.NotFound),
            StripchatRoomKind.Unavailable => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.Unavailable", name), StreamResolveKind.Offline),
            StripchatRoomKind.Unknown when !string.IsNullOrWhiteSpace(rawStatus) => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.StatusUnknown", name, rawStatus), StreamResolveKind.Generic),
            _ => new StreamResolveException(
                Loc.Format("StreamResolve.Stripchat.Offline", name), StreamResolveKind.Offline),
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
        if (!IsDefinitiveUnplayable(kind) && kind != StripchatRoomKind.Unknown)
            return null;
        if (kind == StripchatRoomKind.Unknown && string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(apiError))
            return null;

        return ToException(kind == StripchatRoomKind.Public ? StripchatRoomKind.Unavailable : kind, username, status);
    }

    public static bool IsNotFoundError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Equals("Not Found", StringComparison.OrdinalIgnoreCase)
               || error.Equals("Model not found", StringComparison.OrdinalIgnoreCase)
               || error.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }
}
