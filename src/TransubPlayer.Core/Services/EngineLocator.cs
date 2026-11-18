using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal enum EngineKind
{
    Embedded,
}

internal sealed record EngineRoot(string Path, EngineKind Kind, string Label);

/// <summary>Embedded ASR model root resolution.</summary>
internal static class EngineLocator
{
    public static void Invalidate() => PresetReadiness.InvalidateDiskProbe();

    public static EngineRoot? Find(AppSettings settings)
    {
        var root = AsrModelStore.ResolveModelsRoot(settings);
        if (!AsrModelStore.IsTurboInstalled(root))
            return null;
        return new EngineRoot(root, EngineKind.Embedded, Loc.Get("Settings.Engine.Embedded"));
    }

    public static string ResolveModelsRoot(AppSettings settings, EngineRoot? root = null)
        => root?.Path ?? AsrModelStore.ResolveModelsRoot(settings);

    public static bool IsAsrInstalled(string modelsRoot, string modelId)
        => AsrModelStore.IsInstalled(modelsRoot, modelId);
}
