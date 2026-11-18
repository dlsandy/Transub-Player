using TransubPlayer.Localization;
using TransubPlayer.Services;
using Whisper.net.LibraryLoader;

namespace TransubPlayer.Platform;

/// <summary>Whisper acceleration for Apple platforms: CoreML then CPU.</summary>
internal sealed class CoreMlAsrGpuRuntime : IAsrGpuRuntime
{
    public string ActiveBackend { get; private set; } = AsrBackends.Auto;

    public void Apply(AppSettings settings)
    {
        var pref = AsrBackends.Normalize(settings.AsrBackend);
        if (RuntimeOptions.LoadedLibrary is null)
        {
            var order = pref switch
            {
                AsrBackends.Cpu => new[] { RuntimeLibrary.Cpu },
                AsrBackends.CoreMl => new[] { RuntimeLibrary.CoreML, RuntimeLibrary.Cpu },
                // "vulkan" settings from shared JSON map to CoreML on Mac
                AsrBackends.Vulkan => new[] { RuntimeLibrary.CoreML, RuntimeLibrary.Cpu },
                _ => new[] { RuntimeLibrary.CoreML, RuntimeLibrary.Cpu },
            };
            RuntimeOptions.RuntimeLibraryOrder = order.ToList();
        }

        ActiveBackend = pref is AsrBackends.Vulkan ? AsrBackends.CoreMl : pref;
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
        => RuntimeOptions.LoadedLibrary is RuntimeLibrary.CoreML;

    public string LoadedLibraryLabel()
        => RuntimeOptions.LoadedLibrary switch
        {
            RuntimeLibrary.CoreML => Loc.Get("Settings.AsrBackend.CoreMlShort"),
            RuntimeLibrary.Cpu => Loc.Get("Settings.AsrBackend.Cpu"),
            RuntimeLibrary.CpuNoAvx => Loc.Get("Settings.AsrBackend.Cpu"),
            null => Loc.Get("Settings.AsrBackend.NotLoaded"),
            _ => RuntimeOptions.LoadedLibrary.ToString() ?? "?",
        };

    public bool NeedsRestartForGpu(AppSettings settings)
        => PreferGpu(settings) && RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx;

    public string DescribeActiveBackend()
        => PreferGpu(new AppSettings { AsrBackend = ActiveBackend })
            ? Loc.Get("Settings.AsrBackend.CoreMl")
            : Loc.Get("Settings.AsrBackend.Cpu");
}
