using Whisper.net;
using Whisper.net.Wave;

namespace TransubPlayer.Services;

/// <summary>
/// Multi-window Whisper language-ID (mel auto-detect), not full transcription.
/// </summary>
internal static class AsrLanguageDetect
{
    public static async Task<(string Language, double Confidence, string Detail)> ProbeAsync(
        WhisperProcessor processor,
        string mediaPath,
        double mediaDurationSec,
        IReadOnlyList<double> startOffsetsSec,
        double windowSec,
        Action<string>? log,
        CancellationToken ct)
    {
        var duration = mediaDurationSec > 1 ? mediaDurationSec : 0;
        var starts = BuildStarts(startOffsetsSec, windowSec, duration);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = new List<string>(starts.Count);

        foreach (var start in starts)
        {
            ct.ThrowIfCancellationRequested();
            string? wav = null;
            try
            {
                wav = await AsrAudioExtract.ExtractWavAsync(mediaPath, start, windowSec, ct)
                    .ConfigureAwait(false);
                await using var stream = File.OpenRead(wav);
                var parser = new WaveParser(stream);
                var samples = await parser.GetAvgSamplesAsync(ct).ConfigureAwait(false);
                if (samples.Length < 16000) // <1s @ 16 kHz — skip
                {
                    parts.Add($"{start:0}s:short");
                    continue;
                }

                var (raw, probability) = processor.DetectLanguageWithProbability(samples);
                var lang = MapDetected(raw);
                if (SourceLanguages.IsAuto(lang) || probability < 0.01f)
                {
                    parts.Add($"{start:0}s:{raw ?? "?"}@{probability:0.00}");
                    continue;
                }

                scores[lang] = scores.GetValueOrDefault(lang) + probability;
                hits[lang] = hits.GetValueOrDefault(lang) + 1;
                parts.Add($"{start:0}s:{lang}@{probability:0.00}");

                // One clear hit is enough — avoid 2–3× extract latency on open.
                if (probability >= 0.85f && scores.Count == 1)
                    break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                parts.Add($"{start:0}s:err");
                log?.Invoke($"语种窗 {start:0}s 跳过 · {ex.Message}");
            }
            finally
            {
                AsrAudioExtract.TryDeleteTemp(wav);
            }
        }

        var detail = string.Join(" · ", parts);
        if (scores.Count == 0)
            return (SourceLanguages.Auto, 0, detail);

        var best = scores.OrderByDescending(kv => kv.Value).First();
        var mean = best.Value / Math.Max(1, hits[best.Key]);
        // Relative share of total score — demotes single lucky spikes when windows disagree.
        var share = best.Value / scores.Values.Sum();
        var confidence = Math.Clamp(0.55 * mean + 0.45 * share, 0, 1);
        return (best.Key, confidence, detail);
    }

    private static List<double> BuildStarts(
        IReadOnlyList<double> preferred,
        double windowSec,
        double durationSec)
    {
        var list = new List<double>();
        void Add(double start)
        {
            if (start < 0) start = 0;
            if (durationSec > 1)
                start = Math.Min(start, Math.Max(0, durationSec - Math.Max(1.0, windowSec * 0.5)));
            foreach (var existing in list)
            {
                if (Math.Abs(existing - start) < 5)
                    return;
            }

            list.Add(start);
        }

        if (preferred.Count == 0)
            Add(0);
        else
        {
            foreach (var s in preferred)
                Add(s);
        }

        // Short media: always include head.
        if (durationSec > 1 && durationSec < 90)
            Add(0);

        return list;
    }

    private static string MapDetected(string? raw)
    {
        var t = raw?.Trim().ToLowerInvariant() ?? "";
        // Whisper may return regional / macrolanguage tags.
        if (t is "yue" or "zh-yue" or "cantonese" or "cmn" or "zh-cn" or "zh-tw" or "zh-hk")
            return SourceLanguages.Zh;
        return SourceLanguages.Normalize(t);
    }
}
