using TransubPlayer.Localization;
using TransubPlayer.Platform;
using TransubPlayer.Services;
using Whisper.net.LibraryLoader;

namespace TransubPlayer.Platform.Windows;

internal sealed class VulkanAsrGpuRuntime : IAsrGpuRuntime
{
    public string ActiveBackend { get; private set; } = AsrBackends.Auto;

    public void Apply(AppSettings settings)
    {
        var pref = AsrBackends.Normalize(settings.AsrBackend);
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

        ActiveBackend = pref;
    }

    public bool PreferGpu(AppSettings settings)
        => AsrBackends.Normalize(settings.AsrBackend) is not AsrBackends.Cpu;

    public bool NeedsRestartForGpuDevice(AppSettings settings) => false;

    public int ResolveThreadCount(bool useGpu)
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(Math.Min(2, cores), 1, 2);
    }

    public bool IsGpuLibraryLoaded()
        => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda;

    public string LoadedLibraryLabel()
        => RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.Vulkan => Loc.Get("Settings.AsrBackend.VulkanShort"),
            RuntimeLibrary.Cuda => "CUDA",
            RuntimeLibrary.Cpu => Loc.Get("Settings.AsrBackend.Cpu"),
            RuntimeLibrary.CpuNoAvx => Loc.Get("Settings.AsrBackend.Cpu"),
            null => Loc.Get("Settings.AsrBackend.NotLoaded"),
            _ => RuntimeOptions.LoadedLibrary.ToString() ?? "?",
        };

    public bool NeedsRestartForGpu(AppSettings settings)
        => PreferGpu(settings) && RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx;

    public string DescribeActiveBackend()
        => PreferGpu(new AppSettings { AsrBackend = ActiveBackend })
            ? Loc.Get("Settings.AsrBackend.Vulkan")
            : Loc.Get("Settings.AsrBackend.Cpu");
}
