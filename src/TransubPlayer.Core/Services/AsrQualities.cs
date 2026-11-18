namespace TransubPlayer.Services;



/// <summary>

/// Legacy fast/better prefs remain in JSON as <see cref="Better"/>;

/// runtime ASR choice is <see cref="AppSettings.AsrModel"/> via <see cref="PickAsr"/>.

/// </summary>

internal static class AsrQualities

{

    /// <summary>Legacy JSON value — treated as turbo quality tier.</summary>

    public const string Fast = "fast";

    public const string Better = "better";



    public static string Normalize(string? raw)

    {

        _ = raw;

        return Better;

    }



    public static bool WantsTurbo(string? quality)

    {

        _ = quality;

        return true;

    }



    /// <summary>

    /// Resolve the preferred concrete ASR id for this media (before install fallback).

    /// Manual locks win; <see cref="ModelPicker.Auto"/> currently maps to turbo.

    /// </summary>

    public static string PickAsr(string? preferredModel, RuntimePacks packs, string? sourceLanguage = null)
    {
        _ = sourceLanguage;
        var preferred = ModelPicker.InstallTarget(preferredModel);
        if (IsUsable(preferred, packs))
            return preferred;
        if (IsUsable(ModelPicker.Turbo, packs))
            return ModelPicker.Turbo;
        return ModelPicker.Turbo;
    }

    public static string ResolvePreferred(string? preferredModel, string? sourceLanguage, RuntimePacks packs)
    {
        _ = sourceLanguage;
        _ = packs;
        return ModelPicker.InstallTarget(preferredModel);
    }



    public static bool IsUsable(string modelId, RuntimePacks packs)

    {

        var id = ModelPicker.InstallTarget(modelId);

        var installed = string.Equals(id, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)

            ? packs.TurboInstalled || packs.IsAsrInstalled(id)

            : packs.IsAsrInstalled(id);

        if (!installed)
            return false;

        return true;

    }

}


