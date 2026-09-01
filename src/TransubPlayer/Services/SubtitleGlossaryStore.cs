using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Transub editor glossary (aliases → canonical). Loads player data/ or Transub install copy.
/// </summary>
internal sealed class SubtitleGlossaryStore
{
    private static readonly object Gate = new();
    private static SubtitleGlossaryStore? _cached;
    private static DateTime _cachedUtc = DateTime.MinValue;
    private static string? _cachedPath;

    public IReadOnlyList<GlossaryEntry> Entries { get; }
    public string? SourcePath { get; }

    private SubtitleGlossaryStore(IReadOnlyList<GlossaryEntry> entries, string? sourcePath)
    {
        Entries = entries;
        SourcePath = sourcePath;
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
            _cachedPath = null;
            _cachedUtc = DateTime.MinValue;
        }
    }

    public static SubtitleGlossaryStore Load(AppSettings settings)
    {
        var path = ResolvePath(settings);
        lock (Gate)
        {
            if (_cached is not null
                && string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase)
                && path is not null
                && File.Exists(path)
                && File.GetLastWriteTimeUtc(path) == _cachedUtc)
            {
                return _cached;
            }

            var store = LoadFromPath(path);
            _cached = store;
            _cachedPath = path;
            _cachedUtc = path is not null && File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;
            return store;
        }
    }

    public string Apply(string text)
    {
        if (Entries.Count == 0 || string.IsNullOrEmpty(text)) return text ?? "";
        var cur = text;
        foreach (var entry in Entries)
        {
            if (!entry.Enabled || entry.Aliases.Count == 0) continue;
            foreach (var alias in entry.Aliases)
            {
                if (alias.Length == 0) continue;
                cur = ReplaceTerm(cur, alias, entry.Canonical, entry.CaseSensitive);
            }
        }

        return cur;
    }

    public static string? ResolvePath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.GlossaryPath) && File.Exists(settings.GlossaryPath))
            return Path.GetFullPath(settings.GlossaryPath);

        var local = Path.Combine(AppPaths.AppDataDir, "glossary.json");
        if (File.Exists(local)) return local;

        foreach (var shared in TransubSharedAssets.EnumerateSharedDirs(settings))
        {
            var g = Path.Combine(shared, "transub-glossary.json");
            if (File.Exists(g)) return g;
        }

        foreach (var candidate in TransubInstallRoots(settings))
        {
            var g = Path.Combine(candidate, "transub-glossary.json");
            if (File.Exists(g)) return g;
        }

        return null;
    }

    private static IEnumerable<string> TransubInstallRoots(AppSettings settings)
    {
        var exe = TransubInstall.FindExe(settings);
        if (exe is not null)
        {
            var dir = Path.GetDirectoryName(exe);
            if (!string.IsNullOrWhiteSpace(dir))
                yield return dir;
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localApp, "Programs", "Transub");
        yield return Path.Combine(localApp, "Transub");
    }

    private static SubtitleGlossaryStore LoadFromPath(string? path)
    {
        if (path is null || !File.Exists(path))
            return new SubtitleGlossaryStore([], null);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new SubtitleGlossaryStore([], path);

            var list = new List<GlossaryEntry>();
            foreach (var row in arr.EnumerateArray())
            {
                var canonical = row.TryGetProperty("canonical", out var c) ? (c.GetString() ?? "").Trim() : "";
                if (canonical.Length == 0) continue;
                var enabled = !row.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                var caseSensitive = row.TryGetProperty("caseSensitive", out var cs) && cs.ValueKind == JsonValueKind.True;
                var aliases = new List<string>();
                if (row.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in al.EnumerateArray())
                    {
                        var s = (a.GetString() ?? "").Trim();
                        if (s.Length == 0) continue;
                        if (string.Equals(s, canonical, StringComparison.OrdinalIgnoreCase)) continue;
                        aliases.Add(s);
                    }
                }

                aliases.Sort((a, b) => b.Length.CompareTo(a.Length));
                if (aliases.Count == 0) continue;
                list.Add(new GlossaryEntry(canonical, aliases, caseSensitive, enabled));
            }

            // Longer canonicals first so short terms don't steal
            list.Sort((a, b) =>
            {
                var maxA = Math.Max(a.Canonical.Length, a.Aliases.Count == 0 ? 0 : a.Aliases.Max(x => x.Length));
                var maxB = Math.Max(b.Canonical.Length, b.Aliases.Count == 0 ? 0 : b.Aliases.Max(x => x.Length));
                return maxB.CompareTo(maxA);
            });
            return new SubtitleGlossaryStore(list, path);
        }
        catch
        {
            return new SubtitleGlossaryStore([], path);
        }
    }

    private static string ReplaceTerm(string text, string from, string to, bool caseSensitive)
    {
        if (IsAsciiWord(from))
        {
            var opts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            return Regex.Replace(text, $@"\b{Regex.Escape(from)}\b", to, opts);
        }

        if (caseSensitive)
            return text.Replace(from, to, StringComparison.Ordinal);
        // Case-insensitive for CJK is effectively ordinal (no case)
        return text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiWord(string term)
        => Regex.IsMatch(term, @"^[A-Za-z0-9][A-Za-z0-9_'’\-]*$");
}

internal readonly record struct GlossaryEntry(
    string Canonical,
    IReadOnlyList<string> Aliases,
    bool CaseSensitive,
    bool Enabled);
