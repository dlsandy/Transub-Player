using System.Text.Json;

namespace TransubPlayer.Services;

/// <summary>
/// Transub AV domain / actor glossaries (shared/av-*-glossary.json) for ja-soft / av_soft.
/// Sync: <c>tools/sync-preview-lexicon.ps1</c>.
/// </summary>
internal static class AvGlossaryLexicon
{
    private static readonly object Gate = new();
    private static (string Src, string Zh)[]? _pairs;
    private static string? _loadedFrom;
    private static int _pairCount;

    public static void Invalidate()
    {
        lock (Gate)
        {
            _pairs = null;
            _loadedFrom = null;
            _pairCount = 0;
        }
    }

    public static string Apply(string text, string? contentProfile)
    {
        if (!IsAvSoft(contentProfile)) return text ?? "";
        var cur = text ?? "";
        if (cur.Length == 0) return cur;
        EnsureLoaded();
        var pairs = _pairs;
        if (pairs is null || pairs.Length == 0) return cur;
        foreach (var (src, zh) in pairs)
        {
            if (src.Length == 0 || zh.Length == 0) continue;
            if (cur.Contains(src, StringComparison.Ordinal))
                cur = cur.Replace(src, zh, StringComparison.Ordinal);
        }

        return cur;
    }

    public static int PairCount
    {
        get
        {
            EnsureLoaded();
            return _pairCount;
        }
    }

    public static string? LoadedFromPath
    {
        get
        {
            EnsureLoaded();
            return _loadedFrom;
        }
    }

    private static bool IsAvSoft(string? profile)
        => string.Equals(profile?.Trim(), "av_soft", StringComparison.OrdinalIgnoreCase);

    private static void EnsureLoaded()
    {
        if (_pairs is not null) return;
        lock (Gate)
        {
            if (_pairs is not null) return;
            var list = new List<(string, string)>();
            string? firstPath = null;
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("entries", out var arr)
                        || arr.ValueKind != JsonValueKind.Array)
                        continue;

                    var before = list.Count;
                    foreach (var row in arr.EnumerateArray())
                    {
                        var src = row.TryGetProperty("src", out var s) ? (s.GetString() ?? "").Trim() : "";
                        var zh = row.TryGetProperty("zh", out var z) ? (z.GetString() ?? "").Trim() : "";
                        if (src.Length == 0 || zh.Length == 0 || src == zh) continue;
                        // Skip junk / test rows
                        if (src.Length < 2 && !IsCjk(src)) continue;
                        list.Add((src, zh));
                    }

                    if (list.Count > before && firstPath is null)
                        firstPath = path;
                }
                catch
                {
                    // try next file
                }
            }

            if (list.Count == 0)
            {
                _pairs = [];
                _pairCount = 0;
                _loadedFrom = null;
                return;
            }

            // Longer src first so short terms don't steal
            list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
            // Dedupe by src keeping first (longest-ish already sorted)
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<(string, string)>(list.Count);
            foreach (var pair in list)
            {
                if (!seen.Add(pair.Item1)) continue;
                unique.Add(pair);
            }

            _pairs = unique.ToArray();
            _pairCount = unique.Count;
            _loadedFrom = firstPath;
        }
    }

    private static bool IsCjk(string s)
        => s.Any(c => c is >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF');

    private static IEnumerable<string> CandidatePaths()
    {
        // Domain first (terms), then actor names — both merged in EnsureLoaded
        foreach (var name in new[] { "av-domain-glossary.json", "av-actor-glossary.json" })
        {
            foreach (var p in TransubSharedAssets.EnumerateAssetCandidates(
                         name,
                         bundledFallbacks:
                         [
                             Path.Combine(AppContext.BaseDirectory, "Assets", name),
                             Path.Combine(AppPaths.ProjectRoot, "src", "TransubPlayer", "Assets", name),
                         ]))
                yield return p;
        }
    }
}
