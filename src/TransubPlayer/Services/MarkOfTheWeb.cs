using System.Runtime.InteropServices;

namespace TransubPlayer.Services;

/// <summary>
/// Clears NTFS Zone.Identifier (Mark of the Web) from shipped binaries.
/// Zip downloads tag extracted exe/dll with MOTW; Defender then blocks Player / mpv / python at launch.
/// </summary>
internal static class MarkOfTheWeb
{
    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pyd", ".bat", ".cmd", ".ps1", ".com",
    };

    private const int MaxFiles = 4000;
    private const string FullScanMarkerFile = "motw-full.v1";

    /// <summary>
    /// Fast path for cold start: clear MOTW on binaries we load immediately (player, mpv, whisper runtimes).
    /// Full tree scan can run later via <see cref="ClearInstallDirectoryBackground"/>.
    /// </summary>
    public static int ClearLaunchCritical(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return 0;

        var cleared = 0;
        cleared += ClearTree(root, maxDepth: 0);
        foreach (var rel in new[] { "mpv", "runtimes" })
        {
            var dir = Path.Combine(root, rel);
            if (!Directory.Exists(dir)) continue;
            cleared += ClearTree(dir, maxDepth: 2);
        }

        return cleared;
    }

    /// <summary>Best-effort MOTW removal under the install folder (exe dir).</summary>
    public static int ClearInstallDirectory(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return 0;

        return ClearTree(root, maxDepth: int.MaxValue);
    }

    /// <summary>Run a full install-tree MOTW pass once per build, after the window is visible.</summary>
    public static void ClearInstallDirectoryBackground(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;
        if (!ShouldRunFullScan(root))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var cleared = ClearInstallDirectory(root);
                if (cleared > 0)
                    PlayerLog.Write($"MOTW: background cleared Internet mark on {cleared} file(s)");
                MarkFullScanDone(root);
            }
            catch (Exception ex)
            {
                PlayerLog.Write("MOTW background: " + ex.Message);
            }
        });
    }

    private static int ClearTree(string root, int maxDepth)
    {
        var cleared = 0;
        try
        {
            foreach (var path in EnumerateSensitiveFiles(root, maxDepth))
            {
                if (TryClear(path))
                    cleared++;
                if (cleared >= MaxFiles)
                    break;
            }
        }
        catch (Exception ex)
        {
            PlayerLog.Write("MOTW: " + ex.Message);
        }

        return cleared;
    }

    private static bool ShouldRunFullScan(string root)
    {
        var marker = Path.Combine(AppPaths.CacheDir, FullScanMarkerFile);
        var stamp = BuildInstallStamp(root);
        try
        {
            return !File.Exists(marker) || File.ReadAllText(marker) != stamp;
        }
        catch
        {
            return true;
        }
    }

    private static void MarkFullScanDone(string root)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            File.WriteAllText(Path.Combine(AppPaths.CacheDir, FullScanMarkerFile), BuildInstallStamp(root));
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildInstallStamp(string root)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            exe = Path.Combine(root, "TransubPlayer.exe");
        try
        {
            if (File.Exists(exe))
                return Path.GetFullPath(exe) + "|" + File.GetLastWriteTimeUtc(exe).Ticks;
        }
        catch
        {
            // ignore
        }

        return Path.GetFullPath(root);
    }

    public static bool HasMark(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;
        try
        {
            return File.Exists(filePath + ":Zone.Identifier");
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateSensitiveFiles(string root, int maxDepth)
    {
        foreach (var path in EnumerateSensitiveFilesCore(root, maxDepth, depth: 0))
            yield return path;
    }

    private static IEnumerable<string> EnumerateSensitiveFilesCore(string root, int maxDepth, int depth)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root);
        }
        catch
        {
            yield break;
        }

        foreach (var path in files)
        {
            if (ShouldSkipPath(path))
                continue;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext) || !SensitiveExtensions.Contains(ext))
                continue;
            yield return path;
        }

        if (depth >= maxDepth)
            yield break;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            if (ShouldSkipPath(dir))
                continue;
            foreach (var path in EnumerateSensitiveFilesCore(dir, maxDepth, depth + 1))
                yield return path;
        }
    }

    private static bool ShouldSkipPath(string path)
    {
        // User model weights / caches are not MOTW-tagged and are large trees.
        return path.Contains($"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}models", StringComparison.OrdinalIgnoreCase)
               || path.Contains($"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}cache", StringComparison.OrdinalIgnoreCase)
               || path.Contains($"{Path.DirectorySeparatorChar}data{Path.DirectorySeparatorChar}advanced-llm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryClear(string filePath)
    {
        try
        {
            var zone = filePath + ":Zone.Identifier";
            if (!File.Exists(zone))
                return false;
            return DeleteFile(zone);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteFile(string lpFileName);
}
