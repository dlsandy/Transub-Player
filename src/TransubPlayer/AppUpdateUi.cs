using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

/// <summary>Shared check / download / apply UI for Help menu, About, and startup.</summary>
internal static class AppUpdateUi
{
    public static async Task CheckInteractiveAsync(Window owner, AppSettings settings, bool quietIfCurrent)
    {
        string? priorStatus = null;
        if (owner is MainWindow mainBefore)
            priorStatus = mainBefore.StatusTextSnapshot;

        SetBusyStatus(owner, Loc.Get("Update.Status.Checking"));
        try
        {
            AppUpdateCheckResult result;
            try
            {
                result = await AppUpdateService.CheckAsync(settings, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, Loc.Format("Update.Error.CheckFailed", ex.Message),
                    Loc.Get("Update.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            switch (result.Kind)
            {
                case AppUpdateCheckKind.UpToDate:
                    if (!quietIfCurrent)
                    {
                        MessageBox.Show(owner,
                            Loc.Format("Update.UpToDate", AppUpdateService.CurrentVersionText),
                            Loc.Get("Update.Title"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    return;

                case AppUpdateCheckKind.Failed:
                    MessageBox.Show(owner,
                        Loc.Format("Update.Error.CheckFailed", result.ErrorMessage ?? ""),
                        Loc.Get("Update.Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;

                case AppUpdateCheckKind.NoAsset:
                {
                    var open = MessageBox.Show(owner,
                        Loc.Format("Update.AvailableNoAsset",
                            result.Release!.VersionText,
                            result.Release.SourceDisplayName),
                        Loc.Get("Update.Title"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (open == MessageBoxResult.Yes)
                        AppUpdateService.TryOpenReleasesPage(settings);
                    return;
                }

                case AppUpdateCheckKind.Available:
                    await OfferAndApplyAsync(owner, settings, result.Release!).ConfigureAwait(true);
                    return;
            }
        }
        finally
        {
            RestoreStatus(owner, priorStatus);
        }
    }

    public static async Task StartupCheckAsync(Window owner, AppSettings settings)
    {
        if (!AppUpdateService.ShouldAutoCheck(settings))
            return;

        try
        {
            var result = await AppUpdateService.CheckAsync(settings, CancellationToken.None).ConfigureAwait(true);
            if (result.Kind == AppUpdateCheckKind.Available && result.Release is not null)
                await OfferAndApplyAsync(owner, settings, result.Release).ConfigureAwait(true);
            else if (result.Kind == AppUpdateCheckKind.NoAsset && result.Release is not null)
            {
                var open = MessageBox.Show(owner,
                    Loc.Format("Update.AvailableNoAsset",
                        result.Release.VersionText,
                        result.Release.SourceDisplayName),
                    Loc.Get("Update.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (open == MessageBoxResult.Yes)
                    AppUpdateService.TryOpenReleasesPage(settings);
            }
        }
        catch (Exception ex)
        {
            PlayerLog.Write("启动更新检查：" + ex.Message);
        }
    }

    private static async Task OfferAndApplyAsync(Window owner, AppSettings settings, AppUpdateRelease release)
    {
        var notes = TruncateNotes(release.Body);
        var canApply = AppUpdateService.CanApplyInPlace();
        var prompt = canApply
            ? Loc.Format("Update.AvailableApply",
                AppUpdateService.CurrentVersionText,
                release.VersionText,
                release.SourceDisplayName,
                notes)
            : Loc.Format("Update.AvailableOpen",
                AppUpdateService.CurrentVersionText,
                release.VersionText,
                release.SourceDisplayName,
                notes);

        var go = MessageBox.Show(owner, prompt, Loc.Get("Update.Title"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (go != MessageBoxResult.Yes)
            return;

        if (!canApply)
        {
            AppUpdateService.TryOpenReleasesPage(settings);
            return;
        }

        var dl = DownloadProgressWindow.ShowAndRun(
            owner,
            Loc.Format("Update.DownloadHeading", release.VersionText),
            (status, ct) => AppUpdateService.DownloadAndStageAsync(release, status, ct));

        if (dl != DownloadProgressResult.Ok)
            return;

        try
        {
            AppUpdateService.LaunchApplyAndExit();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, Loc.Format("Update.Error.LaunchApplyDetail", ex.Message),
                Loc.Get("Update.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (owner is MainWindow main)
            main.RequestCloseForUpdate();
        else
            owner.Close();
    }

    private static string TruncateNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Loc.Get("Update.NoNotes");

        var t = body.Replace("\r\n", "\n").Trim();
        if (t.Length > 400)
            t = t[..400].TrimEnd() + "…";
        return t;
    }

    private static void SetBusyStatus(Window owner, string text)
    {
        if (owner is MainWindow main)
            main.SetUpdateStatus(text);
    }

    private static void RestoreStatus(Window owner, string? priorStatus)
    {
        if (owner is not MainWindow main) return;
        if (!string.IsNullOrWhiteSpace(priorStatus))
            main.SetUpdateStatus(priorStatus);
        else
            main.SetUpdateStatus(Loc.Get("Main.Status.Ready"));
    }
}
