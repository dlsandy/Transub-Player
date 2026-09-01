namespace TransubPlayer.Services;

/// <summary>Local MT GGUF preference. Extend <see cref="Selectable"/> and <see cref="ManagedLlmCatalog"/> together.</summary>
internal static class TranslateModels
{
    public const string TranslateGemma4B = "translategemma-4b-q4";

    private static readonly string[] TranslateGemmaFilenames =
    [
        "translategemma-4b-it-Q4_K_M.gguf",
        "translategemma-4b-it.Q4_K_M.gguf",
        "translategemma-4b-it-q4_k_m.gguf",
        "translategemma-4b-it-Q4_K_S.gguf",
        "translategemma-4b-it-Q5_K_M.gguf",
    ];

    public static IReadOnlyList<string> Selectable { get; } = [TranslateGemma4B];

    public static IReadOnlyList<string> PreferredFilenames => TranslateGemmaFilenames;

    public static string Normalize(string? raw)
    {
        var id = (raw ?? "").Trim();
        if (string.Equals(id, TranslateGemma4B, StringComparison.OrdinalIgnoreCase))
            return TranslateGemma4B;
        if (IsLegacyRemoved(id))
            return TranslateGemma4B;
        return TranslateGemma4B;
    }

    public static bool IsTranslateGemma(string? raw)
        => Normalize(raw) == TranslateGemma4B;

    public static ManagedGgufSpec ResolveSpec(string? raw)
        => ManagedLlmCatalog.TranslateGemma4B;

    public static bool MatchesFamily(string pathOrName, string? modelId)
    {
        _ = modelId;
        var name = Path.GetFileName(pathOrName);
        return !string.IsNullOrWhiteSpace(name)
               && name.Contains("translategemma", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegacyRemoved(string id)
        => string.Equals(id, "hy-mt2-1.8b-q4", StringComparison.OrdinalIgnoreCase)
           || id.StartsWith("hy-mt", StringComparison.OrdinalIgnoreCase);
}
