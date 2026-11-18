namespace TransubPlayer.Services;

internal static class PlaylistFileParser
{
    private static readonly HashSet<string> PlaylistExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m3u", ".m3u8", ".pls",
    };

    public static bool IsPlaylist(string path) => PlaylistExt.Contains(Path.GetExtension(path));

    public static IEnumerable<string> Expand(string playlistPath)
    {
        if (string.IsNullOrWhiteSpace(playlistPath) || !File.Exists(playlistPath))
            yield break;

        string baseDir;
        try { baseDir = Path.GetDirectoryName(Path.GetFullPath(playlistPath)) ?? ""; }
        catch { yield break; }

        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(playlistPath);
        }
        catch
        {
            yield break;
        }

        if (Path.GetExtension(playlistPath).Equals(".pls", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var path in ParsePls(lines, baseDir))
                yield return path;
            yield break;
        }

        foreach (var path in ParseM3u(lines, baseDir))
            yield return path;
    }

    private static IEnumerable<string> ParseM3u(IEnumerable<string> lines, string baseDir)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (TryResolveEntry(line, baseDir, out var path))
                yield return path;
        }
    }

    private static IEnumerable<string> ParsePls(IEnumerable<string> lines, string baseDir)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            if (TryResolveEntry(value, baseDir, out var path))
                yield return path;
        }
    }

    private static bool TryResolveEntry(string entry, string baseDir, out string path)
    {
        path = "";
        if (string.IsNullOrWhiteSpace(entry)) return false;

        try
        {
            path = Path.IsPathRooted(entry)
                ? Path.GetFullPath(entry)
                : Path.GetFullPath(Path.Combine(baseDir, entry));
        }
        catch
        {
            return false;
        }

        return File.Exists(path);
    }
}
