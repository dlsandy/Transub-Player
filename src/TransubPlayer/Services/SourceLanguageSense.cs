using System.Text.Json;

namespace TransubPlayer.Services;

/// <summary>
/// Policy for short-window Whisper LID when source is <c>auto</c>.
/// Filename priors seed a guess; audio can confirm or override (weak priors more easily).
/// </summary>
internal static class SourceLanguageSense
{
    /// <summary>Match Transub desktop auto-apply bar (~0.55 with real LID probs).</summary>
    public const double ApplyConfidence = 0.55;

    /// <summary>Higher bar when overriding a filename prior (esp. false en / anime→ja on dubs).</summary>
    public const double OverrideFilenameConfidence = 0.68;

    /// <summary>Strong genre priors (e.g. JAV codes) need clearer audio disagreement.</summary>
    public const double OverrideStrongFilenameConfidence = 0.78;

    public const double DurationSec = 10.0;

    /// <summary>Legacy single-offset; prefer <see cref="ProbeStartsSec"/>.</summary>
    public const double StartSec = 25.0;

    /// <summary>
    /// Skip cold open / logo; sample dialogue-heavy regions. Short files clamp in the detector.
    /// </summary>
    public static readonly double[] ProbeStartsSec = [25.0, 90.0, 180.0];

    /// <summary>
    /// Filename priors that are often wrong (SxxExx / “欧美” on Asian rips, anime tags on dubs).
    /// </summary>
    public static bool IsWeakFilenamePrior(SceneProfile? matched)
    {
        if (matched is null || SourceLanguages.IsAuto(matched.Language))
            return true;

        var hint = matched.MatchHint ?? "";
        // Latin-script / genre tags are often wrong on Asian rips or multi-audio packs.
        if (hint.StartsWith("en-", StringComparison.OrdinalIgnoreCase)
            || hint.StartsWith("es-", StringComparison.OrdinalIgnoreCase))
            return true;
        if (hint is "lecture" or "podcast" or "ja-anime" or "zh-variety")
            return true;
        return false;
    }

    /// <summary>True when filename locked a concrete language (even if weak).</summary>
    public static bool HasFilenameLanguage(SceneProfile? matched)
        => matched is not null && !SourceLanguages.IsAuto(matched.Language);

    /// <summary>
    /// Probe when source is auto and filename did not strongly lock a language.
    /// Weak priors (anime / SxxExx→en / …) still probe so audio can override.
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

    public static double NeededConfidence(SceneProfile? matched, string sensedLanguage)
    {
        if (!HasFilenameLanguage(matched))
            return ApplyConfidence;
        if (SourceLanguages.EqualsLang(matched!.Language, sensedLanguage))
            return ApplyConfidence;
        return IsWeakFilenamePrior(matched)
            ? OverrideFilenameConfidence
            : OverrideStrongFilenameConfidence;
    }

    /// <summary>
    /// Accept only Player-supported langs (ja/ko/en/es/zh) at/above the needed confidence.
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

        var need = NeededConfidence(matched, normalized);
        if (confidence < need)
            return false;

        language = normalized;
        return true;
    }
}
