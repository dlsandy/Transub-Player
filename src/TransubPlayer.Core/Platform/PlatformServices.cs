namespace TransubPlayer.Platform;

/// <summary>Process-wide platform adapters registered by the UI shell at startup.</summary>
internal static class PlatformServices
{
    private static IMpvIpcFactory? _mpvIpc;
    private static IChildProcessPolicy? _childProcesses;
    private static IAsrGpuRuntime? _asrGpu;
    private static IOsIntegration? _os;

    public static IMpvIpcFactory MpvIpc
        => _mpvIpc ?? throw new InvalidOperationException("PlatformServices.MpvIpc not registered.");

    public static IChildProcessPolicy ChildProcesses
        => _childProcesses ?? throw new InvalidOperationException("PlatformServices.ChildProcesses not registered.");

    public static IAsrGpuRuntime AsrGpu
        => _asrGpu ?? throw new InvalidOperationException("PlatformServices.AsrGpu not registered.");

    public static IOsIntegration? Os => _os;

    public static void Register(
        IMpvIpcFactory mpvIpc,
        IChildProcessPolicy childProcesses,
        IAsrGpuRuntime asrGpu,
        IOsIntegration? os = null)
    {
        _mpvIpc = mpvIpc ?? throw new ArgumentNullException(nameof(mpvIpc));
        _childProcesses = childProcesses ?? throw new ArgumentNullException(nameof(childProcesses));
        _asrGpu = asrGpu ?? throw new ArgumentNullException(nameof(asrGpu));
        _os = os;
    }
}
