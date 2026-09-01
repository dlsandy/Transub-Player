namespace TransubPlayer.Services;

/// <summary>Selectable Player ASR model ids. Extend <see cref="Selectable"/> and <see cref="AsrModelCatalog"/> together.</summary>
internal static class ModelPicker
{
    /// <summary>Default: pick the best installed model (currently turbo).</summary>
    public const string Auto = "auto";

    public const string Turbo = "whisper-large-v3-turbo";

    /// <summary>User-facing choices in menus / settings. Add future models here.</summary>
    public static IReadOnlyList<string> Selectable { get; } = [Auto, Turbo];

    public static string Normalize(string? raw)
    {
        var id = (raw ?? "").Trim();
        if (string.Equals(id, Auto, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(id))
            return Auto;
        if (string.Equals(id, Turbo, StringComparison.OrdinalIgnoreCase)
            || id.Contains("turbo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "whisper-tiny", StringComparison.OrdinalIgnoreCase))
            return Turbo;
        if (IsLegacyRemoved(id))
            return Auto;
        // Unknown → auto until registered in Selectable / AsrModelCatalog.
        return Auto;
    }

    public static bool IsAuto(string? raw)
        => Normalize(raw) == Auto;

    /// <summary>Concrete weight folder to ensure/download for the preference (auto → turbo).</summary>
    public static string InstallTarget(string? raw)
    {
        var normalized = Normalize(raw);
        return normalized == Auto ? Turbo : normalized;
    }

    private static bool IsLegacyRemoved(string id)
        => string.Equals(id, "anime-whisper", StringComparison.OrdinalIgnoreCase)
           || id.StartsWith("anime-whisper", StringComparison.OrdinalIgnoreCase)
           || string.Equals(id, "whisper-ja-1.5b", StringComparison.OrdinalIgnoreCase);
}
