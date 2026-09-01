namespace TransubPlayer.Services;

/// <summary>Playlist row subtitle readiness (prefetch cache, live ASR, or sidecar).</summary>
internal enum PlaylistSubReadyKind
{
    None,
    Ready,
    External,
    Queued,
    Running,
    Failed,
    Generating,
}

internal static class PlaylistSubReady
{
    public static PlaylistSubReadyKind Resolve(
        string path,
        bool isCurrent,
        bool currentLiveGenerating,
        bool currentUsingExternal,
        Func<string, bool> isPrefetchRunning,
        Func<string, bool> isPrefetchQueued,
        Func<string, bool> isPrefetchFailed)
    {
        if (string.IsNullOrWhiteSpace(path) || MediaSourceHelper.IsNonLocalMedia(path))
            return PlaylistSubReadyKind.None;

        if (isCurrent)
        {
            if (currentUsingExternal)
                return PlaylistSubReadyKind.External;
            if (PreviewPaths.HasReadyAsr(path))
                return PlaylistSubReadyKind.Ready;
            if (currentLiveGenerating)
                return PlaylistSubReadyKind.Generating;
            if (SubtitleFile.FindExistingSubtitle(path) is not null)
                return PlaylistSubReadyKind.External;
            return PlaylistSubReadyKind.None;
        }

        if (isPrefetchRunning(path))
            return PlaylistSubReadyKind.Running;
        if (PreviewPaths.HasReadyAsr(path))
            return PlaylistSubReadyKind.Ready;
        if (SubtitleFile.FindExistingSubtitle(path) is not null)
            return PlaylistSubReadyKind.External;
        if (isPrefetchFailed(path))
            return PlaylistSubReadyKind.Failed;
        if (isPrefetchQueued(path))
            return PlaylistSubReadyKind.Queued;
        return PlaylistSubReadyKind.None;
    }

    public static string KindKey(PlaylistSubReadyKind kind) => kind switch
    {
        PlaylistSubReadyKind.Ready => "ready",
        PlaylistSubReadyKind.External => "external",
        PlaylistSubReadyKind.Queued => "queued",
        PlaylistSubReadyKind.Running => "running",
        PlaylistSubReadyKind.Failed => "failed",
        PlaylistSubReadyKind.Generating => "generating",
        _ => "none",
    };

    public static string? BadgeLocKey(PlaylistSubReadyKind kind) => kind switch
    {
        PlaylistSubReadyKind.Ready => "Main.Playlist.PrefetchReady",
        PlaylistSubReadyKind.External => "Main.Playlist.External",
        PlaylistSubReadyKind.Queued => "Main.Playlist.PrefetchQueued",
        PlaylistSubReadyKind.Running => "Main.Playlist.PrefetchRunning",
        PlaylistSubReadyKind.Failed => "Main.Playlist.PrefetchFailed",
        PlaylistSubReadyKind.Generating => "Main.Playlist.Generating",
        _ => null,
    };
}
