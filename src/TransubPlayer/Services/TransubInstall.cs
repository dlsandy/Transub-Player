using System.Text.Json;

namespace TransubPlayer.Services;

/// <summary>Optional local Transub install discovery (lexicon / MT reuse only — no handoff).</summary>
internal static class TransubInstall
{
    public static string? FindExe(AppSettings settings)
    {
        foreach (var candidate in ExeCandidates(settings))
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> ExeCandidates(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.TransubInstallPath))
        {
            foreach (var fromInstall in ExeBesideInstall(settings.TransubInstallPath.Trim()))
                yield return fromInstall;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "Transub", "Transub.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Transub", "Transub.exe");

        foreach (var dir in new[]
        {
            Path.Combine(local, "Transub"),
            Path.Combine(local, "Programs", "Transub"),
        })
        {
            var settingsFile = Path.Combine(dir, "transub-settings.json");
            if (!File.Exists(settingsFile)) continue;
            string? extra = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
                if (doc.RootElement.TryGetProperty("installRoot", out var el))
                {
                    var root = el.GetString();
                    if (!string.IsNullOrWhiteSpace(root))
                        extra = Path.Combine(root, "Transub.exe");
                }
            }
            catch
            {
                extra = null;
            }

            if (!string.IsNullOrWhiteSpace(extra))
                yield return extra;
        }
    }

    private static IEnumerable<string> ExeBesideInstall(string installPath)
    {
        string? root = null;
        try
        {
            if (Directory.Exists(installPath))
                root = Path.GetFullPath(installPath);
        }
        catch
        {
            root = null;
        }

        if (string.IsNullOrWhiteSpace(root))
            yield break;

        yield return Path.Combine(root, "Transub.exe");
        var parent = Path.GetDirectoryName(root);
        if (!string.IsNullOrWhiteSpace(parent))
            yield return Path.Combine(parent, "Transub.exe");
    }
}
