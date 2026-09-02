using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

/// <summary>Entry points for Help menu, About, and startup update checks.</summary>
internal static class AppUpdateUi
{
    public static Task CheckInteractiveAsync(Window owner, AppSettings settings, bool quietIfCurrent = false)
    {
        if (quietIfCurrent)
            return StartupCheckAsync(owner, settings);

        UpdateWindow.Show(owner, settings, autoCheck: true);
        return Task.CompletedTask;
    }

    public static async Task StartupCheckAsync(Window owner, AppSettings settings)
    {
        if (!AppUpdateService.ShouldAutoCheck(settings))
            return;

        try
        {
            var result = await AppUpdateService.CheckAsync(settings, CancellationToken.None).ConfigureAwait(true);
            if (result.Kind is AppUpdateCheckKind.Available or AppUpdateCheckKind.NoAsset && result.Release is not null)
            {
                var latest = result.Release.VersionText;
                var prompt = MessageBox.Show(
                    owner,
                    Loc.Format("Update.Startup.Prompt.Message",
                        FormatVersion(latest),
                        FormatVersion(AppUpdateService.CurrentVersionText)),
                    Loc.Format("Update.Startup.Prompt.Title", FormatVersion(latest)),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information,
                    MessageBoxResult.Yes);
                if (prompt == MessageBoxResult.Yes)
                    UpdateWindow.Show(owner, settings, autoCheck: true);
            }
        }
        catch (Exception ex)
        {
            PlayerLog.Write("启动更新检查：" + ex.Message);
        }
    }

    private static string FormatVersion(string raw)
    {
        var t = raw.Trim().TrimStart('v', 'V');
        return string.IsNullOrWhiteSpace(t) ? raw : $"v{t}";
    }
}
