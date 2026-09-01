using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private void RefreshHealthIndicator()
    {
        if (HealthDot is null || HealthButton is null) return;
        var status = RuntimeHealth.Probe(_settings);
        // Only show when something needs fixing — green "ok" dot is noise in the caption bar.
        HealthButton.Visibility = status.Level == RuntimeHealthLevel.Ok
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (status.Level == RuntimeHealthLevel.Ok) return;

        HealthDot.Fill = status.Level == RuntimeHealthLevel.Warning
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x32))
            : new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        HealthDot.ToolTip = RuntimeHealth.Tooltip(status);
        HealthButton.ToolTip = RuntimeHealth.Tooltip(status);
    }

    private void HealthDot_Click(object sender, RoutedEventArgs e)
    {
        var status = RuntimeHealth.Probe(_settings);
        if (status.Level == RuntimeHealthLevel.Ok)
        {
            OpenSettings();
            return;
        }

        if (MpvLocator.Find() is null)
        {
            _ = OfferFetchMpvAsync();
            return;
        }

        if (SetupWizard.ShouldShow(_settings))
            SetupWizardWindow.Show(this, _settings);
        else
            OpenSettings(SettingsWindow.TabAdvanced);
    }

    private async Task<bool?> OfferEnglishSourceChoiceAsync()
    {
        var result = await Dispatcher.InvokeAsync(() => MessageBox.Show(
            this,
            Loc.Get("Main.EnSource.Offer"),
            "Transub Player",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question));
        return result switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
    }

    private void ShortcutsHelp_Click(object sender, RoutedEventArgs e)
        => HelpWindow.Show(this, HelpWindow.HelpKind.Shortcuts, _settings);

    private void SubSourceHelp_Click(object sender, RoutedEventArgs e)
        => HelpWindow.Show(this, HelpWindow.HelpKind.SubSource);

    private async void StartPreviewAction_Click(object sender, RoutedEventArgs e)
        => await RetryPreviewCoreAsync();

    private async void SwitchToPreviewAction_Click(object sender, RoutedEventArgs e)
        => await RetryPreviewCoreAsync();

    private async void InstallPresetAction_Click(object sender, RoutedEventArgs e)
    {
        if (_preview?.PendingGapReport is not { HasGaps: true } report) return;
        var choice = await OfferPresetSetupAsync(report);
        if (choice == PresetSetupChoice.Cancel) return;
        if (_preview is null) return;
        RefreshPresetUi();
        RefreshPlaybackEnabled();
        if (_preview.PresetInstallAvailable) return;
        if (HasMedia && _preview.ShowPreviewChrome)
            await RetryPreviewCoreAsync();
    }

    private void WaitZhSkip_Click(object sender, RoutedEventArgs e)
        => _preview?.SkipWaitForFirstZh();

    private void WaitSwitchSource_Click(object sender, RoutedEventArgs e)
    {
        _preview?.WaitSwitchToSourceAndResume();
        RefreshModeButtons();
    }

    private void WaitJumpReady_Click(object sender, RoutedEventArgs e)
        => _preview?.WaitJumpToReadyAndResume();
}
