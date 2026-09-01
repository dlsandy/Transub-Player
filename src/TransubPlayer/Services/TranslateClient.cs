using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

internal sealed class TranslateClient : IDisposable
{
    private static readonly Regex NumberedLine = new(
        @"^\s*(?:#?\s*)?(\d+)\s*[|．.、:：\-)]\s*(.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>Long-lived client shared across MT batches (HttpClient is designed for reuse).</summary>
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    public string BaseUrl { get; }

    public TranslateClient(string baseUrl, HttpClient? http = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        if (http is null)
        {
            _http = SharedHttp;
            _ownsHttp = false;
        }
        else
        {
            _http = http;
            _ownsHttp = true;
        }
    }

    /// <summary>Preferred factory — reuses the shared handler; Dispose is a no-op for the HTTP layer.</summary>
    public static TranslateClient ForUrl(string baseUrl) => new(baseUrl);

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync($"{BaseUrl}/v1/models", ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return true;
        }
        catch { /* try health */ }

        try
        {
            using var res = await _http.GetAsync($"{BaseUrl}/health", ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> TranslateBatchAsync(
        IReadOnlyList<string> lines,
        CancellationToken ct,
        MtRoute route = default,
        string? contentProfile = null,
        string? translateModelId = null)
    {
        if (lines.Count == 0) return "";
        if (route.IsOff)
            route = MtRoute.Resolve(SourceLanguages.Ja, TranslateTargets.Zh, contentProfile);
        var profile = contentProfile ?? route.ContentProfile;
        var numbered = string.Join("\n", lines.Select((t, i) => $"{i + 1}|{t}"));
        // Align with Transub hymt-translate-core subtitle knobs (not raw Hy-MT defaults).
        // Alias stays "hymt" so llama-server -a hymt keeps working for any GGUF.
        var body = new
        {
            model = "hymt",
            temperature = 0.3,
            top_p = 0.6,
            max_tokens = Math.Clamp(lines.Count * 96, 256, 2048),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = SystemPrompt(route, profile, translateModelId),
                },
                new
                {
                    role = "user",
                    content = numbered,
                },
            },
        };

        using var res = await _http.PostAsJsonAsync($"{BaseUrl}/v1/chat/completions", body, ct).ConfigureAwait(false);
        var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"翻译模型 HTTP {(int)res.StatusCode}: {json[..Math.Min(240, json.Length)]}");

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    public static Dictionary<int, string> ParseNumbered(string raw, int expected)
    {
        var map = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(raw)) return map;
        var lines = raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var m = NumberedLine.Match(line);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, out var n)) continue;
            if (n < 1 || n > expected) continue;
            var text = m.Groups[2].Value.Trim();
            if (text.Length == 0) continue;
            map[n] = text;
        }

        // Fallback: model returned plain lines without indices.
        if (map.Count == 0 && lines.Length > 0)
        {
            var take = Math.Min(expected, lines.Length);
            for (var i = 0; i < take; i++)
            {
                var t = lines[i].Trim();
                if (t.Length == 0) continue;
                map[i + 1] = t;
            }
        }

        return map;
    }

    private static string SystemPrompt(MtRoute route, string? contentProfile, string? translateModelId)
    {
        _ = translateModelId;
        return TranslateGemmaPrompt(route, contentProfile);
    }

    /// <summary>
    /// TranslateGemma prefers regional lang codes; keep Player numbered-batch contract for parsing.
    /// </summary>
    private static string TranslateGemmaPrompt(MtRoute route, string? contentProfile)
    {
        var src = SourceLanguages.Normalize(route.Source);
        var tgt = TranslateTargets.Normalize(route.Target);
        var srcCode = IsoLangCode(src, isSource: true);
        var tgtCode = IsoLangCode(tgt, isSource: false);
        var tgtName = TranslateTargets.EnglishLabel(tgt);
        var soft = IsAvSoft(contentProfile)
            ? " Keep adult tone, moans, and onomatopoeia; do not sanitize or refuse."
            : "";
        var scriptRule = tgt switch
        {
            TranslateTargets.Ko =>
                " Target language is Korean. Write Hangul (한글) only — never Chinese characters, never Japanese.",
            TranslateTargets.Zh =>
                " Target language is Simplified Chinese (简体). Use simplified characters only — never Traditional Chinese.",
            TranslateTargets.ZhHant =>
                " Target language is Traditional Chinese (繁體). Use traditional characters only — never Simplified Chinese.",
            _ => $" Target language is {tgtName}.",
        };
        return $"You are a professional {srcCode} to {tgtCode} translator for subtitles." +
               scriptRule +
               " Translate each numbered line accurately and naturally." +
               soft +
               " Output only the translations, no explanations." +
               " Strict format per line: index|translation. Do not merge lines. Do not invent names.";
    }

    /// <summary>BCP-47-ish codes TranslateGemma accepts (prefer regionalized Chinese / Korean).</summary>
    private static string IsoLangCode(string lang, bool isSource)
    {
        // Targets may be zh-Hant; sources only use SourceLanguages ids.
        var t = isSource ? SourceLanguages.Normalize(lang) : TranslateTargets.Normalize(lang);
        if (!isSource)
        {
            return t switch
            {
                TranslateTargets.Ja => "ja",
                TranslateTargets.Ko => "ko-KR",
                TranslateTargets.En => "en",
                TranslateTargets.ZhHant => "zh-TW",
                TranslateTargets.Zh => "zh-CN",
                _ => "en",
            };
        }

        return t switch
        {
            SourceLanguages.Ja => "ja",
            SourceLanguages.Ko => "ko",
            SourceLanguages.En => "en",
            SourceLanguages.Zh => "zh-CN",
            _ => "en",
        };
    }

    private static bool IsAvSoft(string? profile)
        => string.Equals(profile?.Trim(), "av_soft", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
