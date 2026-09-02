using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

/// <summary>Dedicated update center (mirrors Transub update.html app tab).</summary>
public partial class UpdateWindow : Window
{
    private readonly AppSettings _settings;
    private readonly bool _autoCheck;
    private readonly CancellationTokenSource _workCts = new();

    private AppUpdateCheckResult? _lastCheck;
    private AppUpdateRelease? _pendingRelease;
    private bool _busy;
    private bool _stagedReady;
    private bool _closingForUpdate;

    private UpdateWindow(AppSettings settings, bool autoCheck)
    {
        _settings = settings;
        _autoCheck = autoCheck;
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, WindowChromeUtil.Create(40, canResize: true));
        Title = Loc.Get("Update.Center.Title");
        CurrentVersionText.Text = FormatVersion(AppUpdateService.CurrentVersionText);
        ServerVersionText.Text = "—";
        ChangelogBox.Text = Loc.Get("Update.Center.Changelog.Placeholder");
        SelectSource(_settings.UpdateSource);
        RefreshPendingState();
        if (!_autoCheck)
            SetStatus(Loc.Get("Update.Center.PromptCheck"), StatusKind.Normal);
    }

    public static void Show(Window owner, AppSettings settings, bool autoCheck = true)
    {
        var win = new UpdateWindow(settings, autoCheck) { Owner = owner };
        win.ShowDialog();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (!_autoCheck) return;
        await RunCheckAsync().ConfigureAwait(true);
    }

    private void RefreshPendingState()
    {
        if (!AppUpdateService.TryReadPendingUpdate(out var version))
        {
            _stagedReady = false;
            return;
        }

        _stagedReady = true;
        ServerVersionText.Text = FormatVersion(version);
        SetStatus(Loc.Get("Update.Center.Status.DownloadDone"), StatusKind.Ok);
        SetMeta(Loc.Get("Update.Center.Meta.ReadyInstall"));
        ShowInstallButton(true);
        HideDownloadButton();
    }

    private async Task RunCheckAsync()
    {
        if (_busy) return;
        SetBusy(true);
        ResetActionButtons();
        HideProgress();
        SetStatus(Loc.Get("Update.Status.Checking"), StatusKind.Normal);
        SetMeta("");

        try
        {
            var result = await AppUpdateService.CheckAsync(_settings, _workCts.Token).ConfigureAwait(true);
            _lastCheck = result;
            PresentCheckResult(result);
        }
        catch (OperationCanceledException)
        {
            SetStatus(Loc.Get("DownloadProgress.Cancelled"), StatusKind.Normal);
        }
        catch (Exception ex)
        {
            _lastCheck = null;
            SetStatus(Loc.Format("Update.Error.CheckFailed", ex.Message), StatusKind.Error);
            SetMeta("");
            ShowOpenReleasesButton(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PresentCheckResult(AppUpdateCheckResult result)
    {
        var serverVersion = result.Release?.VersionText ?? "";
        ServerVersionText.Text = string.IsNullOrWhiteSpace(serverVersion) ? "—" : FormatVersion(serverVersion);
        ServerVersionText.Foreground = result.Kind is AppUpdateCheckKind.Available or AppUpdateCheckKind.NoAsset
            ? (Brush)FindResource("UpdAccentBrush")
            : new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));

        switch (result.Kind)
        {
            case AppUpdateCheckKind.UpToDate:
                SetChangelog(result.Release?.Body);
                SetStatus(Loc.Format("Update.UpToDate", AppUpdateService.CurrentVersionText), StatusKind.Ok);
                SetMeta(Loc.Get("Update.Center.Meta.Synced"));
                break;

            case AppUpdateCheckKind.Failed:
                SetChangelog("");
                SetStatus(Loc.Format("Update.Error.CheckFailed", result.ErrorMessage ?? ""), StatusKind.Error);
                SetMeta(FormatFailureMeta(result.TriedSources, result.ErrorMessage));
                ShowOpenReleasesButton(true);
                break;

            case AppUpdateCheckKind.NoAsset:
                _pendingRelease = result.Release;
                SetChangelog(result.Release?.Body);
                SetStatus(Loc.Format("Update.Center.Status.Available", FormatVersion(result.Release!.VersionText)),
                    StatusKind.Info);
                SetMeta(Loc.Format("Update.Center.Meta.NoAsset", result.Release.SourceDisplayName));
                ShowOpenReleasesButton(true);
                break;

            case AppUpdateCheckKind.Available:
                _pendingRelease = result.Release;
                SetChangelog(result.Release?.Body);
                SetStatus(Loc.Format("Update.Center.Status.Available", FormatVersion(result.Release!.VersionText)),
                    StatusKind.Info);
                PresentAvailableMeta(result.Release);
                if (AppUpdateService.CanApplyInPlace())
                    ShowDownloadButton(true);
                else
                {
                    SetMeta(Loc.Get("Update.Center.Meta.InstallerHint"));
                    ShowOpenReleasesButton(true);
                }
                break;
        }

        if (_stagedReady)
            RefreshPendingState();
    }

    private void PresentAvailableMeta(AppUpdateRelease release)
    {
        var source = AppUpdateEndpoints.Normalize(_settings.UpdateSource);
        var nodeHint = source == AppUpdateEndpoints.Auto
            ? Loc.Get("Update.Center.Meta.AutoNode")
            : Loc.Format("Update.Center.Meta.FixedNode",
                source == AppUpdateEndpoints.GitCode ? "GitCode" : "GitHub");
        var portable = Loc.Get("Update.Center.Meta.PortableHint");
        SetMeta(Loc.Format("Update.Center.Meta.AvailableDetail", release.SourceDisplayName, nodeHint, portable));
    }

    private async Task RunDownloadAsync()
    {
        if (_busy || _pendingRelease is null) return;
        SetBusy(true);
        HideDownloadButton();
        HideOpenReleasesButton();
        ShowProgress();
        SetStatus(Loc.Format("Update.Center.Status.Downloading", FormatVersion(_pendingRelease.VersionText)),
            StatusKind.Info);
        SetMeta("");

        try
        {
            await AppUpdateService.DownloadAndStageAsync(
                _pendingRelease,
                ReportDownloadStatus,
                _workCts.Token).ConfigureAwait(true);

            _stagedReady = true;
            ProgressBar.Value = 100;
            ProgressPercentText.Text = Loc.Format("DownloadProgress.Percent", 100);
            ProgressLabelText.Text = Loc.Format("Update.Center.Status.DownloadComplete", FormatVersion(_pendingRelease.VersionText));
            ProgressDetailText.Text = Loc.Get("Update.Center.Meta.ReadyInstall");
            SetStatus(Loc.Get("Update.Center.Status.DownloadDone"), StatusKind.Ok);
            SetMeta("");
            ShowInstallButton(true);
        }
        catch (OperationCanceledException)
        {
            HideProgress();
            SetStatus(Loc.Get("DownloadProgress.Cancelled"), StatusKind.Normal);
            ShowDownloadButton(true);
        }
        catch (Exception ex)
        {
            HideProgress();
            SetStatus(Loc.Format("Update.Error.CheckFailed", ex.Message), StatusKind.Error);
            SetMeta(Loc.Get("Update.Center.Meta.DownloadFailed"));
            ShowDownloadButton(true);
            ShowOpenReleasesButton(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ReportDownloadStatus(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportDownloadStatus(line));
            return;
        }

        if (DownloadProgressUi.TryParseLine(line, out var snap))
        {
            if (snap.Percent is >= 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = snap.Percent.Value;
                ProgressPercentText.Text = Loc.Format("DownloadProgress.Percent", (int)snap.Percent.Value);
            }
            else if (snap.DownloadedBytes is >= 0 && snap.TotalBytes is > 0)
            {
                var pct = 100.0 * snap.DownloadedBytes.Value / snap.TotalBytes.Value;
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = Math.Clamp(pct, 0, 100);
                ProgressPercentText.Text = Loc.Format("DownloadProgress.Percent", (int)Math.Clamp(pct, 0, 100));
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
            }

            if (snap.DownloadedBytes is >= 0 && snap.TotalBytes is > 0)
            {
                ProgressDetailText.Text = Loc.Format("DownloadProgress.SizePair",
                    DownloadProgressUi.FormatBytes(snap.DownloadedBytes.Value),
                    DownloadProgressUi.FormatBytes(snap.TotalBytes.Value));
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                ProgressDetailText.Text = line;
            }
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            ProgressLabelText.Text = line;
            ProgressBar.IsIndeterminate = true;
        }
    }

    private void RunInstall()
    {
        if (_busy) return;
        SetBusy(true);
        SetStatus(Loc.Get("Update.Center.Status.Installing"), StatusKind.Info);
        SetMeta(Loc.Get("Update.Center.Meta.Installing"));
        ShowInstallButton(false);
        ShowProgress();
        ProgressBar.IsIndeterminate = true;
        ProgressLabelText.Text = Loc.Get("Update.Center.Status.Installing");
        ProgressDetailText.Text = Loc.Get("Update.Center.Meta.Installing");

        try
        {
            AppUpdateService.LaunchApplyAndExit();
            _closingForUpdate = true;
            if (Owner is MainWindow main)
                main.RequestCloseForUpdate();
            else
                Close();
        }
        catch (Exception ex)
        {
            HideProgress();
            SetStatus(Loc.Format("Update.Error.LaunchApplyDetail", ex.Message), StatusKind.Error);
            SetMeta("");
            ShowInstallButton(true);
            SetBusy(false);
        }
    }

    private void SelectSource(string? source)
    {
        var normalized = AppUpdateEndpoints.Normalize(source);
        SourceAutoBtn.IsChecked = normalized == AppUpdateEndpoints.Auto;
        SourceGitCodeBtn.IsChecked = normalized == AppUpdateEndpoints.GitCode;
        SourceGitHubBtn.IsChecked = normalized == AppUpdateEndpoints.GitHub;
    }

    private void PersistSource(string source)
    {
        _settings.UpdateSource = AppUpdateEndpoints.Normalize(source);
        _settings.Save();
    }

    private void SetChangelog(string? body)
    {
        ChangelogBox.Text = string.IsNullOrWhiteSpace(body)
            ? Loc.Get("Update.NoNotes")
            : body.Trim();
    }

    private enum StatusKind { Normal, Ok, Error, Info }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusText.Foreground = kind switch
        {
            StatusKind.Ok => new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)),
            StatusKind.Error => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            StatusKind.Info => (Brush)FindResource("UpdAccentBrush"),
            _ => new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)),
        };
    }

    private void SetMeta(string text) => MetaText.Text = text;

    private static string FormatVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        var t = raw.Trim().TrimStart('v', 'V');
        return $"v{t}";
    }

    private static string FormatFailureMeta(string? triedSources, string? error)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(triedSources))
            parts.Add(Loc.Format("Update.Center.Meta.Tried", triedSources));
        if (!string.IsNullOrWhiteSpace(error))
            parts.Add(error);
        parts.Add(Loc.Get("Update.Center.Meta.FailureTip"));
        return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy;
        OpenReleasesButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        SourceAutoBtn.IsEnabled = !busy;
        SourceGitCodeBtn.IsEnabled = !busy;
        SourceGitHubBtn.IsEnabled = !busy;
    }

    private void ResetActionButtons()
    {
        HideDownloadButton();
        HideOpenReleasesButton();
        if (!_stagedReady)
            HideInstallButton();
    }

    private void ShowDownloadButton(bool visible)
    {
        DownloadButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DownloadButton.IsEnabled = visible && !_busy;
    }

    private void HideDownloadButton() => ShowDownloadButton(false);

    private void ShowOpenReleasesButton(bool visible)
    {
        OpenReleasesButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OpenReleasesButton.IsEnabled = visible && !_busy;
    }

    private void HideOpenReleasesButton() => ShowOpenReleasesButton(false);

    private void ShowInstallButton(bool visible)
    {
        InstallButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = visible && !_busy;
    }

    private void HideInstallButton() => ShowInstallButton(false);

    private void ShowProgress()
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressBar.IsIndeterminate = false;
        ProgressPercentText.Text = Loc.Format("DownloadProgress.Percent", 0);
        ProgressLabelText.Text = Loc.Get("Update.Status.Downloading");
        ProgressDetailText.Text = "";
    }

    private void HideProgress() => ProgressPanel.Visibility = Visibility.Collapsed;

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => WindowChromeUtil.DragOrToggle(this, e, allowMaximize: false);

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
        => await RunCheckAsync().ConfigureAwait(true);

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        => await RunDownloadAsync().ConfigureAwait(true);

    private void OpenReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateService.TryOpenReleasesPage(_settings);
        SetStatus(Loc.Get("Update.Center.Status.OpenedReleases"), StatusKind.Ok);
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e) => RunInstall();

    private void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not ToggleButton btn) return;
        if (btn.IsChecked != true)
        {
            btn.IsChecked = true;
            return;
        }

        var tag = btn.Tag as string ?? AppUpdateEndpoints.Auto;
        SelectSource(tag);
        PersistSource(tag);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (!_closingForUpdate)
        {
            try { _workCts.Cancel(); } catch { /* ignore */ }
        }
        try { _workCts.Dispose(); } catch { /* ignore */ }
        base.OnClosed(e);
    }
}
