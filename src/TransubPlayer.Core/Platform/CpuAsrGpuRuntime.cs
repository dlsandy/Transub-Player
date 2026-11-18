using TransubPlayer.Localization;
using TransubPlayer.Services;
using Whisper.net.LibraryLoader;

namespace TransubPlayer.Platform;

/// <summary>CPU-only Whisper runtime (Linux CI / fallback).</summary>
internal sealed class CpuAsrGpuRuntime : IAsrGpuRuntime
{
    public string ActiveBackend { get; private set; } = AsrBackends.Cpu;

    public void Apply(AppSettings settings)
    {
        if (RuntimeOptions.LoadedLibrary is null)
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
        ActiveBackend = AsrBackends.Cpu;
    }

    public bool PreferGpu(AppSettings settings) => false;

    public bool NeedsRestartForGpuDevice(AppSettings settings) => false;

    public int ResolveThreadCount(bool useGpu)
        => Math.Clamp(Math.Min(2, Math.Max(1, Environment.ProcessorCount)), 1, 2);

    public bool IsGpuLibraryLoaded() => false;

    public string LoadedLibraryLabel()
        => RuntimeOptions.LoadedLibrary is null
            ? Loc.Get("Settings.AsrBackend.NotLoaded")
            : Loc.Get("Settings.AsrBackend.Cpu");

    public bool NeedsRestartForGpu(AppSettings settings) => false;

    public string DescribeActiveBackend() => Loc.Get("Settings.AsrBackend.Cpu");
}
