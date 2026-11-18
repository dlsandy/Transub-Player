using TransubPlayer.Services;

namespace TransubPlayer.Platform;

/// <summary>Whisper.net native library order and GPU preference (Vulkan / CoreML / CPU).</summary>
internal interface IAsrGpuRuntime
{
    string ActiveBackend { get; }

    void Apply(AppSettings settings);

    bool PreferGpu(AppSettings settings);

    bool NeedsRestartForGpuDevice(AppSettings settings);

    int ResolveThreadCount(bool useGpu);

    bool IsGpuLibraryLoaded();

    string LoadedLibraryLabel();

    bool NeedsRestartForGpu(AppSettings settings);

    string DescribeActiveBackend();
}
