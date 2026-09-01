namespace TransubPlayer.Services;

/// <summary>Persisted live / stream favorite (page or stream URL, not ephemeral HLS).</summary>
public sealed class LiveFavoriteEntry
{
    public string Url { get; set; } = "";
    /// <summary>Optional display label; empty = use <see cref="MediaSourceHelper.DisplayName"/>.</summary>
    public string? Name { get; set; }
}
