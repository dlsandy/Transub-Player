using System.Text.Json;

namespace TransubPlayer.Services;

/// <summary>
/// Transub console-trained ZH remaps (shared/mt-trained-remaps.json).
/// Applied on av_soft preview MT sanitize when JA source matches jaIncludes.
/// </summary>
internal static class MtTrainedRemapLexicon
{
    private static readonly object Gate = new();
    private static TrainedRule[]? _rules;
    private static string? _loadedFrom;

    public static void Invalidate()
    {
        lock (Gate)
        {
            _rules = null;
            _loadedFrom = null;
        }
    }

    public static string Apply(string zh, string source, string? contentProfile)
    {
        if (!IsAvSoft(contentProfile)) return zh ?? "";
        var cur = (zh ?? "").Trim();
        var src = source ?? "";
        if (cur.Length == 0) return cur;
        EnsureLoaded();
        var rules = _rules;
        if (rules is null || rules.Length == 0) return cur;

        foreach (var rule in rules)
        {
            if (rule.JaIncludes.Length > 0 && !rule.JaIncludes.All(j => src.Contains(j, StringComparison.Ordinal)))
                continue;

            if (rule.Mode == "blank")
            {
                // Require a concrete ZH fragment or a non-trivial JA anchor — bare 1–2 char
                // jaIncludes with empty zhFrom blanked half the reel to「…」.
                if (rule.ZhFrom.Length == 0
                    && (rule.JaIncludes.Length == 0 || rule.JaIncludes.Any(j => j.Length < 3)))
                    continue;
                if (rule.ZhFrom.Length > 0 && !cur.Contains(rule.ZhFrom, StringComparison.Ordinal))
                    continue;
                // Empty — caller treats placeholder as missing so UI shows source, not「…」.
                cur = "";
                continue;
            }

            if (rule.ZhFrom.Length == 0 || !cur.Contains(rule.ZhFrom, StringComparison.Ordinal))
                continue;
            cur = cur.Replace(rule.ZhFrom, rule.ZhTo, StringComparison.Ordinal);
        }

        return cur.Trim();
    }

    public static int RuleCount
    {
        get
        {
            EnsureLoaded();
            return _rules?.Length ?? 0;
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
        if (_rules is not null) return;
        lock (Gate)
        {
            if (_rules is not null) return;
            foreach (var path in CandidatePaths())
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (!doc.RootElement.TryGetProperty("zhRemaps", out var arr)
                        || arr.ValueKind != JsonValueKind.Array)
                        continue;

                    var list = new List<TrainedRule>();
                    foreach (var row in arr.EnumerateArray())
                    {
                        if (row.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False)
                            continue;

                        var jaIncludes = DecodeJaIncludes(row);
                        var zhFrom = DecodeField(row, "zhFromB64", "zhFrom");
                        var zhTo = DecodeField(row, "zhToB64", "zhTo");
                        var mode = row.TryGetProperty("mode", out var m) ? m.GetString() ?? "replace" : "replace";
                        if (mode != "blank" && zhFrom.Length == 0) continue;

                        list.Add(new TrainedRule(jaIncludes, zhFrom, zhTo, mode));
                    }

                    if (list.Count == 0) continue;
                    _rules = list.ToArray();
                    _loadedFrom = path;
                    return;
                }
                catch
                {
                    // try next
                }
            }

            _rules = [];
            _loadedFrom = null;
        }
    }

    private static string[] DecodeJaIncludes(JsonElement row)
    {
        if (row.TryGetProperty("jaIncludesB64", out var b64) && b64.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in b64.EnumerateArray())
            {
                var s = DecodeB64(item.GetString());
                if (s.Length > 0) list.Add(s);
            }

            return list.ToArray();
        }

        if (row.TryGetProperty("jaIncludes", out var plain) && plain.ValueKind == JsonValueKind.Array)
        {
            return plain.EnumerateArray()
                .Select(x => x.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }

        return [];
    }

    private static string DecodeField(JsonElement row, string b64Name, string plainName)
    {
        if (row.TryGetProperty(b64Name, out var b64) && b64.ValueKind == JsonValueKind.String)
            return DecodeB64(b64.GetString());
        if (row.TryGetProperty(plainName, out var plain) && plain.ValueKind == JsonValueKind.String)
            return plain.GetString() ?? "";
        return "";
    }

    private static string DecodeB64(string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return "";
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64.Trim()));
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<string> CandidatePaths()
    {
        foreach (var p in TransubSharedAssets.EnumerateAssetCandidates(
                     "mt-trained-remaps.json",
                     bundledFallbacks:
                     [
                         Path.Combine(AppContext.BaseDirectory, "Assets", "mt-trained-remaps.json"),
                         Path.Combine(AppContext.BaseDirectory, "mt-trained-remaps.json"),
                         Path.Combine(AppPaths.ProjectRoot, "src", "TransubPlayer", "Assets", "mt-trained-remaps.json"),
                         Path.Combine(AppPaths.AppDataDir, "mt-trained-remaps.json"),
                     ]))
            yield return p;
    }

    private readonly record struct TrainedRule(string[] JaIncludes, string ZhFrom, string ZhTo, string Mode);
}
