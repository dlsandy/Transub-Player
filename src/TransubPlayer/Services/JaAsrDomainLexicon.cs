using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>
/// Transub JA ASR domain mishear table (免税→メンエス 等).
/// Bundled file merges TDP D01: <c>shared/ja-asr-domain-fixes.json</c> + opaque adult ASR.
/// Sync: <c>tools/sync-preview-lexicon.ps1</c>.
/// </summary>
internal static class JaAsrDomainLexicon
{
    private static readonly object Gate = new();
    private static (string From, string To)[]? _pairs;
    private static string? _loadedFrom;

    public static void Invalidate()
    {
        lock (Gate)
        {
            _pairs = null;
            _loadedFrom = null;
        }
    }

    public static string Apply(string text)
    {
        var cur = text ?? "";
        if (cur.Length == 0) return cur;
        EnsureLoaded();
        var pairs = _pairs;
        if (pairs is null || pairs.Length == 0) return cur;
        foreach (var (from, to) in pairs)
        {
            if (from.Length == 0) continue;
            if (cur.Contains(from, StringComparison.Ordinal))
                cur = cur.Replace(from, to, StringComparison.Ordinal);
        }

        // Mid-line Whisper おはよう glued onto real dialogue
        if (!Regex.IsMatch(cur.Trim(), @"^おはよう|^おはようございます"))
        {
            var stripped = Regex.Replace(
                cur,
                @"(.+[\u3040-\u30ff\u4e00-\u9fff].{0,12}?)(?:あっ?|あぁ)?\s*おはようございます?[。．!！?？\s]*$",
                "$1");
            if (!ReferenceEquals(stripped, cur) && stripped.Trim().Length >= 2)
                cur = stripped.Trim();
        }

        return cur;
    }

    public static string? LoadedFromPath
    {
        get
        {
            EnsureLoaded();
            return _loadedFrom;
        }
    }

    public static int PairCount
    {
        get
        {
            EnsureLoaded();
            return _pairs?.Length ?? 0;
        }
    }

    private static void EnsureLoaded()
    {
        if (_pairs is not null) return;
        lock (Gate)
        {
            if (_pairs is not null) return;
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<(string, string)>();
                    foreach (var row in doc.RootElement.EnumerateArray())
                    {
                        var from = row.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                        var to = row.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
                        if (from.Length > 0 && to.Length > 0)
                            list.Add((from, to));
                    }

                    if (list.Count == 0) continue;
                    list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
                    _pairs = list.ToArray();
                    _loadedFrom = path;
                    return;
                }
                catch
                {
                    // try next
                }
            }

            _pairs = [];
            _loadedFrom = null;
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        foreach (var p in TransubSharedAssets.EnumerateAssetCandidates(
                     "ja-asr-domain-fixes.json",
                     bundledFallbacks:
                     [
                         Path.Combine(AppContext.BaseDirectory, "Assets", "ja-asr-domain-fixes.json"),
                         Path.Combine(AppContext.BaseDirectory, "ja-asr-domain-fixes.json"),
                         Path.Combine(AppPaths.ProjectRoot, "src", "TransubPlayer", "Assets", "ja-asr-domain-fixes.json"),
                         Path.Combine(AppPaths.AppDataDir, "ja-asr-domain-fixes.json"),
                     ]))
            yield return p;

        // Transub Pro active TDP overlay (when Transub desktop applied D01)
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Transub", "tdp", "active", "d01.json");
    }
}
