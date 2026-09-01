namespace TransubPlayer.Services;

internal sealed class RuntimePacks
{
    public bool TurboInstalled { get; private set; }
    public bool GpuReady { get; private set; }
    public HashSet<string> InstalledAsr { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsAsrInstalled(string modelId)
        => InstalledAsr.Contains(modelId);

    public static RuntimePacks FromDisk(string modelsRoot)
    {
        var packs = new RuntimePacks();
        packs.TurboInstalled = AsrModelStore.IsTurboInstalled(modelsRoot);
        if (packs.TurboInstalled)
            packs.InstalledAsr.Add(ModelPicker.Turbo);
        packs.GpuReady = true;
        return packs;
    }
}
