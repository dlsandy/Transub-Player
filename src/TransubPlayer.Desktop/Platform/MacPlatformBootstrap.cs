using TransubPlayer.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop.Platform;

internal static class MacPlatformBootstrap
{
    public static void Register(AppSettings settings)
    {
        IAsrGpuRuntime asr = OperatingSystem.IsMacOS()
            ? new CoreMlAsrGpuRuntime()
            : new CpuAsrGpuRuntime();

        PlatformServices.Register(
            new UnixMpvIpcFactory(),
            new UnixChildProcessPolicy(),
            asr,
            new MacOsIntegration(settings));
    }
}
