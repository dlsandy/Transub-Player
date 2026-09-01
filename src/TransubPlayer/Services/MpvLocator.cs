namespace TransubPlayer.Services;

internal static class MpvLocator
{
    private static string? _cachedPath;
    private static bool _cached;
    private static DateTime _cachedUtc;

    public static string? Find()
    {
        if (_cached && (DateTime.UtcNow - _cachedUtc).TotalSeconds < 60)
            return _cachedPath;

        string? found = null;
        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                found = Path.GetFullPath(candidate);
                break;
            }
        }

        _cachedPath = found;
        _cached = true;
        _cachedUtc = DateTime.UtcNow;
        return found;
    }

    public static void Invalidate()
    {
        _cached = false;
        _cachedPath = null;
    }

    public static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "mpv", "mpv.exe");
        yield return Path.Combine(AppPaths.NativeMpvDir, "mpv.exe");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "mpv", "mpv.exe"));

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, "mpv", "mpv.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "mpv", "mpv.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "mpv", "current", "mpv.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "mpv.exe");

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim(), "mpv.exe"); }
            catch { continue; }
            yield return full;
        }
    }
}
