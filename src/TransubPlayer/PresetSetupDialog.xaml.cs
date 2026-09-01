using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class PresetSetupDialog : Window
{
    private readonly Func<Action<string>, CancellationToken, Task>? _runAutoInstall;
    private CancellationTokenSource? _installCts;
    private bool _installing;

    internal PresetSetupChoice Choice { get; private set; } = PresetSetupChoice.Cancel;

    internal PresetSetupDialog(
        PresetGapReport report,
        Func<Action<string>, CancellationToken, Task>? runAutoInstall = null)
    {
        _runAutoInstall = runAutoInstall;
        InitializeComponent();
        Title = Loc.Get("Main.Deps.DialogTitle") + " · " + report.PresetName;
        BodyText.Text = report.DialogBody();
        AutoButton.IsEnabled = report.CanAutoInstallAny;
        if (!report.CanAutoInstallAny)
        {
            HintText.Text = Loc.Get("Main.Deps.ManualOnlyHint");
        }
    }

    /// <summary>
    /// Shows the dialog. When <paramref name="runAutoInstall"/> is set,「自动安装」keeps the
    /// window open and streams status into the progress panel until finished.
    /// </summary>
    internal static PresetSetupChoice Show(
        Window owner,
        PresetGapReport report,
        Func<Action<string>, CancellationToken, Task>? runAutoInstall = null)
    {
        var dlg = new PresetSetupDialog(report, runAutoInstall) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Choice;
    }

    private async void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_runAutoInstall is null)
        {
            Choice = PresetSetupChoice.AutoInstall;
            DialogResult = true;
            Close();
            return;
        }

        if (_installing) return;
        _installing = true;
        _installCts = new CancellationTokenSource();
        SetInstallingUi(true);
        ProgressLabel.Text = Loc.Get("Main.Deps.InstallStarting");

        try
        {
            await _runAutoInstall(UpdateProgress, _installCts.Token).ConfigureAwait(true);
            Choice = PresetSetupChoice.AutoInstall;
            ProgressLabel.Text = Loc.Get("Main.Deps.InstallDone");
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            _installing = false;
            SetInstallingUi(false);
            ProgressLabel.Text = Loc.Get("Main.Deps.InstallCancelled");
            Choice = PresetSetupChoice.Cancel;
        }
        catch (Exception ex)
        {
            _installing = false;
            SetInstallingUi(false);
            ProgressLabel.Text = Loc.Format("Main.Deps.InstallFailed", ex.Message);
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 0;
        }
        finally
        {
            _installCts?.Dispose();
            _installCts = null;
        }
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        Choice = PresetSetupChoice.ManualInstall;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_installing)
        {
            CancelInstall_Click(sender, e);
            return;
        }

        Choice = PresetSetupChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        try { _installCts?.Cancel(); } catch { /* ignore */ }
        ProgressLabel.Text = Loc.Get("Main.Deps.InstallCancelling");
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_installing) return;
        // Keep window open until cancel finishes; request cancel instead of hard-close.
        e.Cancel = true;
        try { _installCts?.Cancel(); } catch { /* ignore */ }
        ProgressLabel.Text = Loc.Get("Main.Deps.InstallCancelling");
    }

    private void SetInstallingUi(bool on)
    {
        ButtonRow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = on;
        if (on)
            ProgressBar.Value = 0;
        AutoButton.IsEnabled = !on;
        ManualButton.IsEnabled = !on;
        CancelInstallButton.IsEnabled = on;
    }

    private void UpdateProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateProgress(line));
            return;
        }

        ProgressLabel.Text = line;
        if (DownloadProgressUi.TryParsePercent(line, out var pct))
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = 100;
            ProgressBar.Value = pct;
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
        }
    }
}
