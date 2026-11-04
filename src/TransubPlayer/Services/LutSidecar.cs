using System.IO;

namespace TransubPlayer.Services;

/// <summary>
/// Discovers a same-stem LUT next to a local media file (.cube / .3dl / .hald).
/// </summary>
internal static class LutSidecar
{
    private static readonly string[] Extensions = [".cube", ".3dl", ".hald", ".hald.png"];
    private static readonly string[] SubFolders = ["", "LUTs", "luts", "Lut", "lut"];

    /// <summary>Prefer .cube, then .3dl, then .hald / .hald.png. Also checks LUTs/ subfolders.</summary>
    public static string? Find(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || MediaSourceHelper.IsNonLocalMedia(mediaPath))
            return null;

        string dir;
        string stem;
        try
        {
            dir = Path.GetDirectoryName(Path.GetFullPath(mediaPath)) ?? "";
            stem = Path.GetFileNameWithoutExtension(mediaPath);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(stem))
            return null;

        foreach (var sub in SubFolders)
        {
            var folder = string.IsNullOrEmpty(sub) ? dir : Path.Combine(dir, sub);
            if (!Directory.Exists(folder))
                continue;

            foreach (var ext in Extensions)
            {
                var candidate = Path.Combine(folder, stem + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static bool IsLutFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".hald.png", StringComparison.OrdinalIgnoreCase))
            return File.Exists(path);
        return Extensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)) && File.Exists(path);
    }
}
