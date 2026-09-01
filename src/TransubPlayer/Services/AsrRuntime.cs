using TransubPlayer.Localization;
using Whisper.net.LibraryLoader;

namespace TransubPlayer.Services;

/// <summary>Configures whisper.cpp native library order (CPU / Vulkan).</summary>
internal static class AsrRuntime
{
    public static string ActiveBackend { get; private set; } = AsrBackends.Auto;

    public static void Apply(AppSettings settings)
    {
        var pref = AsrBackends.Normalize(settings.AsrBackend);
        var order = pref switch
        {
            AsrBackends.Vulkan => new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
            AsrBackends.Cpu => new[] { RuntimeLibrary.Cpu },
            _ => new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
        };

        RuntimeOptions.RuntimeLibraryOrder = order.ToList();
        ActiveBackend = pref;
    }

    public static EngineCapabilities EnrichCapabilities(EngineCapabilities disk, AppSettings settings)
    {
        Apply(settings);
        var vulkanFirst = AsrBackends.Normalize(settings.AsrBackend) is AsrBackends.Vulkan or AsrBackends.Auto;
        return new EngineCapabilities
        {
            Kind = disk.Kind,
            EngineRoot = disk.EngineRoot,
            Label = disk.Label,
            SileroVad = disk.SileroVad,
            DetectLanguage = disk.DetectLanguage,
            LiveProbed = true,
            AsrGpuReady = vulkanFirst,
            HasCuda = false,
            GpuName = vulkanFirst ? Loc.Get("Settings.AsrBackend.VulkanShort") : null,
            AsrBackends = vulkanFirst ? ["vulkan", "cpu"] : ["cpu"],
        };
    }
}
