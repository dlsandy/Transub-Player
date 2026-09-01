namespace TransubPlayer.Services;

/// <summary>Runtime translation direction. Off when disabled, same-language, or unknown.</summary>
internal readonly record struct MtRoute(string? Source, string? Target, string ContentProfile = "general")
{
    public static MtRoute Off { get; } = new(null, null, "general");

    public bool IsOff =>
        string.IsNullOrWhiteSpace(Source)
        || string.IsNullOrWhiteSpace(Target)
        || SourceLanguages.EqualsLang(Source, Target);

    public static MtRoute Resolve(string? sourceLanguage, string? translateTarget, string? contentProfile = null)
    {
        var src = SourceLanguages.Normalize(sourceLanguage);
        var tgt = TranslateTargets.Normalize(translateTarget);
        // auto source: still translate with generic prompts (model sees text).
        // zh → zh-Hant is NOT identity (script conversion).
        if (src != SourceLanguages.Auto && IsIdentityRoute(src, tgt))
            return Off;
        return new MtRoute(src, tgt, string.IsNullOrWhiteSpace(contentProfile) ? "general" : contentProfile.Trim());
    }

    /// <summary>True when source and target are the same language and no MT is needed.</summary>
    private static bool IsIdentityRoute(string src, string tgt)
    {
        if (tgt == TranslateTargets.ZhHant)
            return false;
        return string.Equals(src, tgt, StringComparison.OrdinalIgnoreCase);
    }

    public static bool WantsTranslation(MtRoute route) => !route.IsOff;
}
