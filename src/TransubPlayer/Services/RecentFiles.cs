namespace TransubPlayer.Services;

internal static class RecentFiles
{
    public static void Add(AppSettings settings, string path)
    {
        if (!settings.RememberRecentFiles) return;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (MediaSourceHelper.IsNonLocalMedia(path))
        {
            if (MediaSourceHelper.TryNormalizeMedia(path, out var normalized))
                path = normalized;
            else
                path = path.Trim();
        }
        else
        {
            if (!File.Exists(path)) return;
            path = Path.GetFullPath(path);
        }
        settings.RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        settings.RecentFiles.Insert(0, path);
        Trim(settings);
    }

    public static void Trim(AppSettings settings)
    {
        var max = Math.Clamp(settings.RecentFilesMax, 0, 30);
        if (max == 0)
        {
            settings.RecentFiles.Clear();
            return;
        }

        while (settings.RecentFiles.Count > max)
            settings.RecentFiles.RemoveAt(settings.RecentFiles.Count - 1);
    }

    public static void Clear(AppSettings settings)
        => settings.RecentFiles.Clear();

    public static IEnumerable<string> Valid(AppSettings settings)
        => settings.RecentFiles.Where(p => MediaSourceHelper.IsNonLocalMedia(p) || File.Exists(p));
}
