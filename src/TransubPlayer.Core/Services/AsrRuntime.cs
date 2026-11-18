using TransubPlayer.Localization;
using TransubPlayer.Platform;

namespace TransubPlayer.Services;

/// <summary>Configures whisper.cpp native library order via <see cref="PlatformServices.AsrGpu"/>.</summary>
internal static class AsrRuntime
{
    public static string ActiveBackend => PlatformServices.AsrGpu.ActiveBackend;

    public static void Apply(AppSettings settings)
        => PlatformServices.AsrGpu.Apply(settings);

    public static bool PreferGpu(AppSettings settings)
        => PlatformServices.AsrGpu.PreferGpu(settings);

    public static bool NeedsRestartForGpuDevice(AppSettings settings)
        => PlatformServices.AsrGpu.NeedsRestartForGpuDevice(settings);

    public static int ResolveThreadCount(bool useGpu)
        => PlatformServices.AsrGpu.ResolveThreadCount(useGpu);

    public static bool IsGpuLibraryLoaded()
        => PlatformServices.AsrGpu.IsGpuLibraryLoaded();

    public static string LoadedLibraryLabel()
        => PlatformServices.AsrGpu.LoadedLibraryLabel();

    public static bool NeedsRestartForGpu(AppSettings settings)
        => PlatformServices.AsrGpu.NeedsRestartForGpu(settings);

    public static EngineCapabilities EnrichCapabilities(EngineCapabilities disk, AppSettings settings)
    {
        Apply(settings);
        var gpu = PreferGpu(settings);
        var label = OperatingSystem.IsMacOS()
            ? Loc.Get("Settings.AsrBackend.CoreMlShort")
            : Loc.Get("Settings.AsrBackend.VulkanShort");
        return new EngineCapabilities
        {
            Kind = disk.Kind,
            EngineRoot = disk.EngineRoot,
            Label = disk.Label,
            SileroVad = disk.SileroVad,
            DetectLanguage = disk.DetectLanguage,
            LiveProbed = true,
            AsrGpuReady = gpu,
            HasCuda = false,
            GpuName = gpu ? label : null,
            AsrBackends = gpu
                ? (OperatingSystem.IsMacOS() ? ["coreml", "cpu"] : ["vulkan", "cpu"])
                : ["cpu"],
        };
    }
}
