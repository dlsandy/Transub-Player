using System.Text.Json;

namespace TransubPlayer.Services;

/// <summary>
/// Thin policy for engine <c>POST /v1/detect-language</c> before ASR when source is <c>auto</c>.
/// Strong filename priors win; weak priors (e.g. SxxExx→en) can be overridden by audio.
/// </summary>
internal static class SourceLanguageSense
{
    /// <summary>Match Transub desktop auto-apply bar (~0.62).</summary>
    public const double ApplyConfidence = 0.62;

    /// <summary>Slightly higher bar when overriding a weak filename prior (esp. false en).</summary>
    public const double OverrideFilenameConfidence = 0.70;

    public const double DurationSec = 12.0;

    /// <summary>Skip typical opening titles / BGM; engine falls back to file head if past EOF.</summary>
    public const double StartSec = 45.0;

    /// <summary>
    /// Filename priors that are often wrong (SxxExx / “欧美” on Asian rips, open-ended lecture).
    /// </summary>
    public static bool IsWeakFilenamePrior(SceneProfile? matched)
    {
        if (matched is null || SourceLanguages.IsAuto(matched.Language))
            return true;

        var hint = matched.MatchHint ?? "";
        if (hint.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            return true;
        if (hint is "lecture" or "podcast")
            return true;
        return false;
    }

    /// <summary>
    /// Probe when user left source on auto, and filename did not strongly lock a language.
    /// </summary>
    public static bool ShouldProbe(string? settingsSourceLanguage, SceneProfile? matched)
    {
        if (!SourceLanguages.IsAuto(settingsSourceLanguage))
            return false;
        if (matched is not null
            && !SourceLanguages.IsAuto(matched.Language)
            && !IsWeakFilenamePrior(matched))
            return false;
        return true;
    }

    /// <summary>
    /// Accept only Player-supported langs (ja/ko/en/zh) at/above the needed confidence.
    /// </summary>
    public static bool TryParse(
        JsonElement json,
        SceneProfile? matched,
        out string language,
        out double confidence)
    {
        language = SourceLanguages.Auto;
        confidence = 0;

        if (json.ValueKind != JsonValueKind.Object)
            return false;
        if (json.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return false;
        if (!json.TryGetProperty("language", out var langEl) || langEl.ValueKind != JsonValueKind.String)
            return false;

        var normalized = SourceLanguages.Normalize(langEl.GetString());
        if (SourceLanguages.IsAuto(normalized))
            return false;

        if (json.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number)
            confidence = c.GetDouble();

        var need = ApplyConfidence;
        if (matched is not null
            && !SourceLanguages.IsAuto(matched.Language)
            && !SourceLanguages.EqualsLang(matched.Language, normalized))
        {
            need = OverrideFilenameConfidence;
        }

        if (confidence < need)
            return false;

        language = normalized;
        return true;
    }
}
