using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

internal sealed class Cue
{
    public int Index { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public string? Zh { get; set; }
}

internal static class SubtitleFile
{
    private static readonly Regex TimeLine = new(
        @"^(\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}[,.]\d{3})",
        RegexOptions.Compiled);

    public static List<Cue> ParseSrt(string path)
    {
        if (!File.Exists(path)) return [];
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch { return []; }
        if (string.IsNullOrWhiteSpace(text)) return [];

        var cues = new List<Cue>();
        var blocks = Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n");
        var n = 0;
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2) continue;
            var timeIdx = TimeLine.IsMatch(lines[0]) ? 0 : (lines.Length > 1 && TimeLine.IsMatch(lines[1]) ? 1 : -1);
            if (timeIdx < 0) continue;
            var m = TimeLine.Match(lines[timeIdx]);
            var start = ParseTs(m.Groups[1].Value);
            var end = ParseTs(m.Groups[2].Value);
            var body = string.Join("\n", lines.Skip(timeIdx + 1)).Trim();
            if (string.IsNullOrWhiteSpace(body)) continue;
            n++;
            cues.Add(new Cue { Index = n, Start = start, End = Math.Max(end, start + 0.05), Text = body });
        }

        return cues;
    }

    public static void WriteSrt(string path, IEnumerable<Cue> cues, bool chinese)
        => WriteDisplaySrt(path, cues, chinese ? SubtitleDisplayMode.Zh : SubtitleDisplayMode.Source);

    public static void WriteDisplaySrt(string path, IEnumerable<Cue> cues, SubtitleDisplayMode mode)
    {
        var sb = new StringBuilder();
        var i = 0;
        foreach (var cue in cues)
        {
            var text = FormatCueBody(cue, mode);
            if (string.IsNullOrWhiteSpace(text)) continue;
            i++;
            sb.Append(i).Append('\n');
            sb.Append(FormatTs(cue.Start)).Append(" --> ").Append(FormatTs(cue.End)).Append('\n');
            sb.Append(text.Replace("\r\n", "\n")).Append("\n\n");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string FormatCueBody(Cue cue, SubtitleDisplayMode mode)
    {
        var src = (cue.Text ?? "").Trim();
        // Ellipsis-only / punct placeholders are not real translations — fall back to source.
        var zh = PreviewTextSanitize.IsPlaceholderText(cue.Zh) ? "" : (cue.Zh ?? "").Trim();
        // Soft-voice ASR/MT spam (プププ… / 哈哈哈哈…) must never paint the whole screen.
        if (PreviewTextSanitize.LooksLikeAsrHallucination(src)) src = "";
        if (PreviewTextSanitize.LooksLikeAsrHallucination(zh)) zh = "";
        return mode switch
        {
            SubtitleDisplayMode.Off => "",
            SubtitleDisplayMode.Source => src,
            SubtitleDisplayMode.Dual when zh.Length > 0 && src.Length > 0 => zh + "\n" + src,
            SubtitleDisplayMode.Dual when zh.Length > 0 => zh,
            SubtitleDisplayMode.Dual => src,
            // Zh: prefer translation; until MT catches up, keep source so the screen is never blank.
            _ when zh.Length > 0 => zh,
            _ => src,
        };
    }

    public static void MergePreserveZh(List<Cue> into, IReadOnlyList<Cue> incoming)
    {
        var byKey = into
            .Where(c => !PreviewTextSanitize.IsPlaceholderText(c.Zh))
            .GroupBy(c => $"{c.Start:0.00}|{c.Text}")
            .ToDictionary(g => g.Key, g => g.First().Zh!);
        into.Clear();
        foreach (var c in incoming)
        {
            var key = $"{c.Start:0.00}|{c.Text}";
            if (byKey.TryGetValue(key, out var zh))
                c.Zh = zh;
            into.Add(c);
        }
    }

    public static double Frontier(IReadOnlyList<Cue> cues)
        => cues.Count == 0 ? 0 : cues.Max(c => c.End);

    public static double TranslatedFrontier(IReadOnlyList<Cue> cues)
    {
        double max = 0;
        foreach (var c in cues)
        {
            if (PreviewTextSanitize.IsPlaceholderText(c.Zh)) continue;
            if (c.End > max) max = c.End;
        }

        return max;
    }

    public static string? FindExistingSubtitle(string mediaPath)
    {
        foreach (var p in EnumerateSidecarCandidates(mediaPath))
        {
            try
            {
                if (File.Exists(p) && new FileInfo(p).Length > 40)
                    return p;
            }
            catch
            {
                // ignore unreadable
            }
        }

        return null;
    }

    public static bool IsSidecarExtension(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext)) return false;
        return ext.Equals(".srt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ass", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ssa", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Preference-ordered sidecar paths Transub / Player may write next to media.</summary>
    public static IEnumerable<string> EnumerateSidecarCandidates(string mediaPath)
    {
        var dir = Path.GetDirectoryName(mediaPath);
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (dir is null || string.IsNullOrWhiteSpace(stem))
            yield break;

        foreach (var name in new[]
        {
            // Prefer finished Chinese / bilingual exports from Transub.
            $"{stem}.zh.srt", $"{stem}.zh.ass", $"{stem}.zh.vtt",
            $"{stem}.bilingual.srt", $"{stem}.bilingual.ass", $"{stem}.dual.ass",
            $"{stem}.srt", $"{stem}.ass", $"{stem}.vtt",
            $"{stem}.zh-Hant.srt", $"{stem}.zh-Hant.ass",
        })
            yield return Path.Combine(dir, name);
    }

    public static Dictionary<string, SidecarFingerprint> SnapshotSidecars(string mediaPath)
    {
        var map = new Dictionary<string, SidecarFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in EnumerateSidecarCandidates(mediaPath))
        {
            try
            {
                if (!File.Exists(p)) continue;
                var info = new FileInfo(p);
                if (info.Length <= 40) continue;
                map[Path.GetFullPath(p)] = new SidecarFingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
            }
            catch
            {
                // ignore
            }
        }

        return map;
    }

    /// <summary>
    /// Best sidecar that is new or rewritten versus <paramref name="baseline"/> (handoff-time snapshot).
    /// </summary>
    public static string? FindUpdatedSidecar(
        string mediaPath,
        IReadOnlyDictionary<string, SidecarFingerprint> baseline)
    {
        foreach (var p in EnumerateSidecarCandidates(mediaPath))
        {
            try
            {
                if (!File.Exists(p)) continue;
                var info = new FileInfo(p);
                if (info.Length <= 40) continue;
                var full = Path.GetFullPath(p);
                var fp = new SidecarFingerprint(info.Length, info.LastWriteTimeUtc.Ticks);
                if (!baseline.TryGetValue(full, out var old) || old != fp)
                    return full;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static double ParseTs(string raw)
    {
        raw = raw.Replace(',', '.');
        if (TimeSpan.TryParseExact(raw, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out var ts))
            return ts.TotalSeconds;
        return 0;
    }

    private static string FormatTs(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }
}
