using Whisper.net.LibraryLoader;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Configures whisper.cpp native library order (CPU / Vulkan) and CPU thread budget.</summary>
internal static class AsrRuntime
{
    /// <summary>
    /// Cap for Whisper CPU worker threads. whisper.cpp CLI defaults to
    /// <c>min(4, hardware_concurrency)</c>; leaving this unset makes Whisper.net
    /// use every logical core and saturates the machine while playing video.
    /// Keep CPU mode at 2 so concurrent HandBrake / UI / decode stay responsive.
    /// Vulkan still needs a few CPU threads for mel + sampling.
    /// </summary>
    private const int MaxWhisperGpuThreads = 2;
    private const int MaxWhisperCpuThreads = 2;

    public static string ActiveBackend { get; private set; } = AsrBackends.Auto;

    public static void Apply(AppSettings settings)
    {
        var pref = AsrBackends.Normalize(settings.AsrBackend);
        // Only effective before the first WhisperFactory load — native lib is sticky for the process.
        if (RuntimeOptions.LoadedLibrary is null)
        {
            var order = pref switch
            {
                AsrBackends.Vulkan => new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
                AsrBackends.Cpu => new[] { RuntimeLibrary.Cpu },
                _ => new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
            };
            RuntimeOptions.RuntimeLibraryOrder = order.ToList();
        }

        // Before ggml-vulkan loads: pin Vulkan device (auto avoids Intel Arc when NVIDIA/AMD exists).
        if (pref is not AsrBackends.Cpu)
            VulkanGpuProbe.ApplyAsrDeviceFilter(settings.AsrGpuDevice);

        ActiveBackend = pref;
    }

    /// <summary>
    /// True when settings ask for a different Vulkan GPU than the one already pinned this process.
    /// </summary>
    public static bool NeedsRestartForGpuDevice(AppSettings settings)
        => PreferGpu(settings) && VulkanGpuProbe.NeedsRestartForDevice(settings.AsrGpuDevice);

    /// <summary>Whether the preferred backend wants GPU offload when creating a factory.</summary>
    public static bool PreferGpu(AppSettings settings)
        => AsrBackends.Normalize(settings.AsrBackend) is not AsrBackends.Cpu;

    /// <summary>
    /// Threads for <see cref="Whisper.net.WhisperProcessorBuilder.WithThreads"/>.
    /// GPU backends keep a small pool; CPU uses the same whisper.cpp CLI cap.
    /// </summary>
    public static int ResolveThreadCount(bool useGpu)
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        if (useGpu)
            return Math.Clamp(Math.Min(MaxWhisperGpuThreads, cores), 1, MaxWhisperGpuThreads);
        return Math.Clamp(Math.Min(MaxWhisperCpuThreads, cores), 1, MaxWhisperCpuThreads);
    }

    public static bool IsGpuLibraryLoaded()
        => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda;

    public static string LoadedLibraryLabel()
        => RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Vulkan => Loc.Get("Settings.AsrBackend.VulkanShort"),
            RuntimeLibrary.Cuda => "CUDA",
            RuntimeLibrary.Cpu => Loc.Get("Settings.AsrBackend.Cpu"),
            RuntimeLibrary.CpuNoAvx => Loc.Get("Settings.AsrBackend.Cpu"),
            null => Loc.Get("Settings.AsrBackend.NotLoaded"),
            _ => RuntimeOptions.LoadedLibrary.ToString() ?? "?",
        };

    /// <summary>
    /// True when the user asked for Vulkan/GPU but the process already pinned a CPU-only native lib
    /// (cannot remount without restart).
    /// </summary>
    public static bool NeedsRestartForGpu(AppSettings settings)
        => PreferGpu(settings)
           && RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx;

    public static EngineCapabilities EnrichCapabilities(EngineCapabilities disk, AppSettings settings)
    {
        Apply(settings);
        var loaded = RuntimeOptions.LoadedLibrary;
        var preferGpu = PreferGpu(settings);
        var gpuReady = loaded switch
        {
            RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda => true,
            RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => false,
            // Not loaded yet — show preference so settings UI is not blank.
            _ => preferGpu,
        };
        var gpuName = loaded switch
        {
            RuntimeLibrary.Vulkan => Loc.Get("Settings.AsrBackend.VulkanShort"),
            RuntimeLibrary.Cuda => "CUDA",
            RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx => Loc.Get("Settings.AsrBackend.Cpu"),
            _ => preferGpu ? Loc.Get("Settings.AsrBackend.VulkanShort") : Loc.Get("Settings.AsrBackend.Cpu"),
        };

        return new EngineCapabilities
        {
            Kind = disk.Kind,
            EngineRoot = disk.EngineRoot,
            Label = disk.Label,
            SileroVad = disk.SileroVad,
            DetectLanguage = disk.DetectLanguage,
            LiveProbed = true,
            AsrGpuReady = gpuReady,
            HasCuda = loaded == RuntimeLibrary.Cuda,
            GpuName = gpuName,
            AsrBackends = gpuReady ? ["vulkan", "cpu"] : ["cpu"],
        };
    }
}
