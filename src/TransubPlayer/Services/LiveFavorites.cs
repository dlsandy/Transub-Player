namespace TransubPlayer.Services;

internal static class LiveFavorites
{
    public const int MaxCount = 50;

    public static string DisplayLabel(LiveFavoriteEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Name))
            return entry.Name.Trim();
        return MediaSourceHelper.DisplayName(entry.Url);
    }

    public static bool TryNormalizeFavoriteUrl(string input, out string url)
    {
        url = "";
        if (!MediaSourceHelper.TryNormalizeMedia(input, out var normalized))
            return false;
        if (MediaSourceHelper.IsScreenCapture(normalized))
            return false;
        if (!MediaSourceHelper.IsRemoteUrl(normalized))
            return false;
        url = normalized;
        return true;
    }

    public static bool Contains(AppSettings settings, string url)
    {
        if (!TryNormalizeFavoriteUrl(url, out var normalized))
            return false;
        return settings.LiveFavorites.Any(e =>
            string.Equals(e.Url, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds or updates a favorite. Returns false if the URL is invalid or already present with the same name.</summary>
    public static bool Add(AppSettings settings, string url, string? name = null)
    {
        if (!TryNormalizeFavoriteUrl(url, out var normalized))
            return false;

        var label = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var existing = settings.LiveFavorites.FindIndex(e =>
            string.Equals(e.Url, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            if (label is not null)
                settings.LiveFavorites[existing].Name = label;
            // Move to front
            var entry = settings.LiveFavorites[existing];
            settings.LiveFavorites.RemoveAt(existing);
            settings.LiveFavorites.Insert(0, entry);
            return true;
        }

        settings.LiveFavorites.Insert(0, new LiveFavoriteEntry { Url = normalized, Name = label });
        Trim(settings);
        return true;
    }

    public static bool Remove(AppSettings settings, string url)
    {
        if (!TryNormalizeFavoriteUrl(url, out var normalized))
            return false;
        var n = settings.LiveFavorites.RemoveAll(e =>
            string.Equals(e.Url, normalized, StringComparison.OrdinalIgnoreCase));
        return n > 0;
    }

    public static bool Rename(AppSettings settings, string url, string? name)
    {
        if (!TryNormalizeFavoriteUrl(url, out var normalized))
            return false;
        var entry = settings.LiveFavorites.FirstOrDefault(e =>
            string.Equals(e.Url, normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return false;
        entry.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return true;
    }

    public static void Clear(AppSettings settings)
        => settings.LiveFavorites.Clear();

    public static void Trim(AppSettings settings)
    {
        while (settings.LiveFavorites.Count > MaxCount)
            settings.LiveFavorites.RemoveAt(settings.LiveFavorites.Count - 1);
    }

    public static IEnumerable<LiveFavoriteEntry> All(AppSettings settings)
        => settings.LiveFavorites.Where(e => !string.IsNullOrWhiteSpace(e.Url)
                                            && TryNormalizeFavoriteUrl(e.Url, out _));
}
