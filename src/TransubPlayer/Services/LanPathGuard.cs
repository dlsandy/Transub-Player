namespace TransubPlayer.Services;

/// <summary>Resolves media paths only under configured LAN share roots.</summary>
internal static class LanPathGuard
{
    public static IReadOnlyList<string> NormalizedRoots(IEnumerable<string>? roots)
    {
        var list = new List<string>();
        if (roots is null) return list;
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var full = Path.GetFullPath(raw.Trim());
                if (!Directory.Exists(full)) continue;
                if (list.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
                    continue;
                list.Add(full);
            }
            catch
            {
                // skip invalid
            }
        }

        return list;
    }

    public static bool TryResolveMedia(
        IReadOnlyList<string> roots,
        int rootIndex,
        string? relativePath,
        out string fullPath,
        out string error)
    {
        fullPath = "";
        error = "";
        if (roots.Count == 0)
        {
            error = "no roots";
            return false;
        }

        if (rootIndex < 0 || rootIndex >= roots.Count)
        {
            error = "bad root";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "missing path";
            return false;
        }

        var root = roots[rootIndex];
        string combined;
        try
        {
            var rel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (rel.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.EndsWith("..", StringComparison.Ordinal)
                || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel == "..")
            {
                error = "path escape";
                return false;
            }

            combined = Path.GetFullPath(Path.Combine(root, rel));
        }
        catch
        {
            error = "bad path";
            return false;
        }

        if (!IsUnderRoot(root, combined))
        {
            error = "outside root";
            return false;
        }

        if (!File.Exists(combined))
        {
            error = "not found";
            return false;
        }

        var ext = Path.GetExtension(combined);
        if (!MediaFileTypes.PlaybackExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            error = "unsupported";
            return false;
        }

        fullPath = combined;
        return true;
    }

    public static bool IsUnderRoot(string root, string candidate)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            var c = Path.GetFullPath(candidate);
            return c.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(
                       c.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string EncodeMediaId(int rootIndex, string relativePath)
    {
        var raw = rootIndex + "|" + relativePath.Replace('\\', '/');
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecodeMediaId(string? id, out int rootIndex, out string relativePath)
    {
        rootIndex = -1;
        relativePath = "";
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            var s = id.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }

            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s));
            var pipe = text.IndexOf('|');
            if (pipe <= 0) return false;
            if (!int.TryParse(text.AsSpan(0, pipe), out rootIndex)) return false;
            relativePath = text[(pipe + 1)..];
            return relativePath.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
