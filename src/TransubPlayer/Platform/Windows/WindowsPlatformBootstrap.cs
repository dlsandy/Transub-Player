using TransubPlayer.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Platform.Windows;

internal static class WindowsPlatformBootstrap
{
    public static void Register(AppSettings settings, Func<System.Windows.Window?> mainWindow)
    {
        PlatformServices.Register(
            new WindowsMpvIpcFactory(),
            new WindowsChildProcessPolicy(),
            new VulkanAsrGpuRuntime(),
            new WindowsOsIntegration(settings, mainWindow));
    }
}
