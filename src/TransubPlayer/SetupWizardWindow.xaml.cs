using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class SetupWizardWindow : Window
{
    private sealed record LabeledOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    private readonly AppSettings _settings;
    private SetupDetectResult _detect;
    private int _step;
    private bool _completed;
    private bool _busy;
    private bool _allowClose;
    private bool _assocChoiceInitialized;
    private bool _uiLangBoxSync;
    private string _uiLangSyncAnchor = UiLanguages.Auto;
    private CancellationTokenSource? _workCts;

    public SetupWizardWindow(AppSettings settings)
    {
        _settings = settings;
        if (string.IsNullOrWhiteSpace(_settings.UiLanguage))
            _settings.UiLanguage = UiLanguages.Auto;
        _settings.HfEndpoint = HfEndpoints.NormalizeOrDefault(_settings.HfEndpoint);
        _detect = SetupWizard.Detect(settings);
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, WindowChromeUtil.Create(40, canResize: true));
        Closing += SetupWizardWindow_Closing;
        // First paint of wizard follows OS when preference is auto.
        Loc.Apply(_settings.UiLanguage);
        PopulateCombos();
        LoadPreferencesFromSettings();
        RefreshPreferenceGates();
        ShowStep(0);
    }

    public static bool Show(Window owner, AppSettings settings)
    {
        var win = new SetupWizardWindow(settings) { Owner = owner };
        return win.ShowDialog() == true;
    }

    private void PopulateCombos()
    {
        var uiLang = UiLanguageBox.SelectedValue as string
                     ?? (string.IsNullOrWhiteSpace(_settings.UiLanguage) ? UiLanguages.Auto : _settings.UiLanguage.Trim());
        var translateTarget = TranslateTargetBox.SelectedValue as string
                              ?? TranslateTargets.Normalize(_settings.TranslateTarget);
        var waitZh = WaitZhMinutesBox.SelectedValue as string
                     ?? NormalizeWaitZhMinutes(_settings.WaitForZhMinutes);

        _uiLangBoxSync = true;
        try
        {
            UiLanguageBox.ItemsSource = BuildUiLanguageOptions();
            UiLanguageBox.DisplayMemberPath = nameof(LabeledOption.Label);
            UiLanguageBox.SelectedValuePath = nameof(LabeledOption.Value);
            SelectComboValue(UiLanguageBox, uiLang, UiLanguages.Auto);
        }
        finally
        {
            _uiLangBoxSync = false;
        }

        TranslateTargetBox.ItemsSource = new[]
        {
            new LabeledOption(Loc.Get("Settings.TranslateTarget.Zh"), TranslateTargets.Zh),
            new LabeledOption(Loc.Get("Settings.TranslateTarget.ZhHant"), TranslateTargets.ZhHant),
            new LabeledOption(Loc.Get("Settings.TranslateTarget.En"), TranslateTargets.En),
            new LabeledOption(Loc.Get("Settings.TranslateTarget.Ja"), TranslateTargets.Ja),
            new LabeledOption(Loc.Get("Settings.TranslateTarget.Ko"), TranslateTargets.Ko),
        };
        TranslateTargetBox.DisplayMemberPath = nameof(LabeledOption.Label);
        TranslateTargetBox.SelectedValuePath = nameof(LabeledOption.Value);
        SelectComboValue(
            TranslateTargetBox,
            TranslateTargets.Normalize(translateTarget),
            TranslateTargets.FromUiLanguage(uiLang));

        WaitZhMinutesBox.ItemsSource = new[]
        {
            new LabeledOption(Loc.Get("Settings.WaitZh.FirstBatch"), "0"),
            new LabeledOption(Loc.Format("Settings.WaitZh.Minutes", "0.5"), "0.5"),
            new LabeledOption(Loc.Format("Settings.WaitZh.Minutes", "1"), "1"),
            new LabeledOption(Loc.Format("Settings.WaitZh.Minutes", "2"), "2"),
            new LabeledOption(Loc.Format("Settings.WaitZh.Minutes", "3"), "3"),
            new LabeledOption(Loc.Format("Settings.WaitZh.Minutes", "5"), "5"),
            new LabeledOption(Loc.Format("Settings.WaitZh.Minutes", "10"), "10"),
        };
        WaitZhMinutesBox.DisplayMemberPath = nameof(LabeledOption.Label);
        WaitZhMinutesBox.SelectedValuePath = nameof(LabeledOption.Value);
        SelectComboValue(WaitZhMinutesBox, waitZh, "1");
    }

    private static LabeledOption[] BuildUiLanguageOptions()
    {
        var list = new List<LabeledOption>
        {
            new(Loc.Get("Common.AutoFollowSystem"), UiLanguages.Auto),
        };
        foreach (var lang in UiLanguages.Catalog)
            list.Add(new LabeledOption(lang.NativeName, lang.Tag));
        return list.ToArray();
    }

    private void LoadPreferencesFromSettings()
    {
        var uiLang = string.IsNullOrWhiteSpace(_settings.UiLanguage) ? UiLanguages.Auto : _settings.UiLanguage.Trim();
        if (!UiLanguages.IsKnownTag(uiLang))
            uiLang = UiLanguages.Auto;
        _uiLangBoxSync = true;
        try
        {
            SelectComboValue(UiLanguageBox, uiLang, UiLanguages.Auto);
        }
        finally
        {
            _uiLangBoxSync = false;
        }
        _uiLangSyncAnchor = uiLang;

        AutoPlayBox.IsChecked = _settings.AutoPlayOnOpen;
        AutoPreviewBox.IsChecked = _settings.AutoStartPreview;
        PreferExternalBox.IsChecked = _settings.PreferExternalSubtitle;
        SubtitleCatBox.IsChecked = _settings.FetchSubtitleFromSubtitleCat;
        TranslateBox.IsChecked = _settings.TranslateEnabled;
        SelectComboValue(
            TranslateTargetBox,
            TranslateTargets.Normalize(_settings.TranslateTarget),
            TranslateTargets.FromUiLanguage(_settings.UiLanguage));
        PlayImmediatelyBox.IsChecked = _settings.PlayImmediatelyOnOpen;
        WaitFirstZhBox.IsChecked = _settings.WaitForFirstZhBeforePlay;
        SelectComboValue(WaitZhMinutesBox, NormalizeWaitZhMinutes(_settings.WaitForZhMinutes), "1");
        ModelsPathBox.Text = _settings.ModelsPath;
        AdvancedLlmPathBox.Text = _settings.AdvancedLlmPath;
    }

    private void ApplyPreferencesToSettings()
    {
        _settings.UiLanguage = UiLanguageBox.SelectedValue as string ?? UiLanguages.Auto;
        _settings.AutoPlayOnOpen = AutoPlayBox.IsChecked != false;
        _settings.AutoStartPreview = AutoPreviewBox.IsChecked != false;
        _settings.PreferExternalSubtitle = PreferExternalBox.IsChecked != false;
        _settings.FetchSubtitleFromSubtitleCat = SubtitleCatBox.IsChecked == true;
        _settings.TranslateEnabled = TranslateBox.IsChecked == true;
        _settings.TranslateTarget = TranslateTargetBox.SelectedValue as string
            ?? TranslateTargets.FromUiLanguage(_settings.UiLanguage);
        _settings.PlayImmediatelyOnOpen = PlayImmediatelyBox.IsChecked == true;
        _settings.WaitForFirstZhBeforePlay = WaitFirstZhBox.IsChecked == true;
        _settings.WaitForZhMinutes = ParseWaitZhMinutes(WaitZhMinutesBox.SelectedValue as string);
        _settings.ModelsPath = ModelsPathBox.Text.Trim();
        _settings.AdvancedLlmPath = AdvancedLlmPathBox.Text.Trim();
        _settings.HfEndpoint = HfEndpoints.NormalizeOrDefault(_settings.HfEndpoint);
        PresetReadiness.InvalidateDiskProbe();
        _settings.Save();
    }

    private void BrowseModelsPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.Get("Settings.Paths.BrowseModels") };
        if (dlg.ShowDialog(this) == true)
            ModelsPathBox.Text = dlg.FolderName;
    }

    private void BrowseAdvancedLlmPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.Get("Settings.Paths.BrowseRuntime") };
        if (dlg.ShowDialog(this) == true)
            AdvancedLlmPathBox.Text = dlg.FolderName;
    }

    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiLangBoxSync || !IsLoaded || UiLanguageBox.SelectedValue is not string newUi)
            return;

        var prevDefault = TranslateTargets.FromUiLanguage(_uiLangSyncAnchor);
        var newDefault = TranslateTargets.FromUiLanguage(newUi);
        _uiLangSyncAnchor = newUi;

        _settings.UiLanguage = newUi;
        Loc.Apply(newUi);

        if (TranslateTargetBox.SelectedValue is string cur
            && string.Equals(TranslateTargets.Normalize(cur), prevDefault, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(prevDefault, newDefault, StringComparison.OrdinalIgnoreCase))
        {
            SelectComboValue(TranslateTargetBox, newDefault, newDefault);
        }

        PopulateCombos();
        RefreshNextButton();
        BackButton.Content = Loc.Get("Wizard.Back");
        if (_step == 1)
            RefreshInstallUi();
        else if (_step == 2)
            RefreshDoneSummary();

        _settings.Save();
    }

    private void ShowStep(int step)
    {
        _step = step;
        PrefsPanel.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        DonePanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;

        StyleStep(StepPrefsPill, StepPrefsLabel, 0);
        StyleStep(StepInstallPill, StepInstallLabel, 1);
        StyleStep(StepDonePill, StepDoneLabel, 2);

        BackButton.Visibility = step > 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshNextButton();

        if (step == 1)
            RefreshInstallUi();
        else if (step == 2)
        {
            if (!_assocChoiceInitialized)
            {
                AssociatePlaybackBox.IsChecked = true;
                _assocChoiceInitialized = true;
            }
            AssocError.Visibility = Visibility.Collapsed;
            RefreshDoneSummary();
        }
    }

    private void RefreshNextButton()
    {
        if (_busy)
        {
            NextButton.Content = Loc.Get("Wizard.CancelInstall");
            NextButton.IsEnabled = true;
            return;
        }

        NextButton.Content = Loc.Get(_step switch
        {
            2 => "Wizard.Finish",
            1 when SetupWizard.NeedsInstall(_settings) => "Wizard.Install.DownloadAndNext",
            _ => "Wizard.Next",
        });
        NextButton.IsEnabled = _step switch
        {
            0 => true,
            1 => CanProceedInstallStep(),
            2 => true,
            _ => false,
        };
    }

    private bool CanProceedInstallStep()
    {
        if (!SetupWizard.IsCoreReady(_settings))
        {
            if (MpvLocator.Find() is null && FirstRunHelp.FindFetchMpvScript() is null)
                return false;
            return false;
        }

        return SetupWizard.CanProceedFromInstall(_settings);
    }

    private void StyleStep(Border pill, TextBlock label, int index)
    {
        var active = _step == index;
        pill.Background = active
            ? (Brush)FindResource("NavSelectedBrush")
            : Brushes.Transparent;
        label.Foreground = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush");
        label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => WindowChromeUtil.DragOrToggle(this, e, allowMaximize: false);

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();

    private void RefreshInstallUi()
    {
        _detect = SetupWizard.Detect(_settings);
        MpvStatusLine.Text = Loc.Get(_detect.HasMpv ? "Wizard.Install.MpvReady" : "Wizard.Install.MpvMissing");
        AsrStatusLine.Text = Loc.Get(_detect.HasAsrModel ? "Wizard.Install.AsrReady" : "Wizard.Install.AsrMissing");
        MtStatusLine.Text = _settings.TranslateEnabled
            ? Loc.Get(_detect.HasLlamaRuntime && _detect.HasGguf
                ? "Wizard.Install.MtReady"
                : "Wizard.Install.MtMissing")
            : Loc.Get("Wizard.Install.MtSkipped");

        var need = SetupWizard.NeedsInstall(_settings);
        DownloadAllButton.Visibility = need && !_busy ? Visibility.Visible : Visibility.Collapsed;
        DownloadAllButton.IsEnabled = need && !_busy && CanStartInstall();

        InstallBlockHint.Visibility = Visibility.Collapsed;
        if (!SetupWizard.IsCoreReady(_settings)
            && MpvLocator.Find() is null
            && FirstRunHelp.FindFetchMpvScript() is null)
        {
            InstallBlockHint.Text = Loc.Get("Wizard.Mpv.NoScript");
            InstallBlockHint.Visibility = Visibility.Visible;
        }
        else if (need && !_busy)
        {
            InstallBlockHint.Text = Loc.Get("Wizard.Install.BlockHint");
            InstallBlockHint.Visibility = Visibility.Visible;
        }

        RefreshNextButton();
    }

    private bool CanStartInstall()
    {
        if (_detect.HasMpv)
            return true;
        return FirstRunHelp.FindFetchMpvScript() is not null;
    }

    private void RefreshDoneSummary()
    {
        _detect = SetupWizard.Detect(_settings);
        var lines = new List<string>
        {
            Loc.Get(_detect.HasMpv ? "Wizard.Done.MpvOk" : "Wizard.Done.MpvMissing"),
            Loc.Get(_detect.HasAsrModel ? "Wizard.Done.AsrOk" : "Wizard.Done.AsrMissing"),
            Loc.Format("Wizard.Done.Models", _detect.ModelsRoot),
            Loc.Format("Wizard.Done.Runtime", _detect.AdvancedLlmRoot),
        };

        if (_settings.TranslateEnabled)
        {
            lines.Add(Loc.Get(_detect.HasLlamaRuntime ? "Wizard.Done.MtRuntimeOk" : "Wizard.Done.MtRuntimeMissing"));
            lines.Add(Loc.Get(_detect.HasGguf ? "Wizard.Done.MtGgufOk" : "Wizard.Done.MtGgufMissing"));
        }
        else
        {
            lines.Add(Loc.Get("Wizard.Done.MtDisabled"));
        }

        lines.Add(Loc.Get(AutoPlayBox.IsChecked != false ? "Wizard.Done.AutoPlayOn" : "Wizard.Done.AutoPlayOff"));
        lines.Add(Loc.Get(SubtitleCatBox.IsChecked == true ? "Wizard.Done.OnlineOn" : "Wizard.Done.OnlineOff"));
        lines.Add(Loc.Get(PlayImmediatelyBox.IsChecked == true
            ? "Wizard.Done.PlayImmediateOn"
            : WaitFirstZhBox.IsChecked == true
                ? "Wizard.Done.WaitZhOn"
                : "Wizard.Done.WaitFirstBatch"));
        lines.Add(Loc.Get(AssociatePlaybackBox.IsChecked == true ? "Wizard.Done.AssocOn" : "Wizard.Done.AssocOff"));
        DoneSummary.Text = string.Join(Environment.NewLine, lines);
    }

    private void RefreshPreferenceGates()
    {
        var translateOn = TranslateBox.IsChecked == true;
        var autoPreview = AutoPreviewBox.IsChecked != false;
        var autoPlay = AutoPlayBox.IsChecked != false;

        TranslateTargetPanel.IsEnabled = translateOn;
        MtStatusLine.Visibility = Visibility.Visible;

        var waitDepsOk = translateOn && autoPreview && autoPlay;
        PlayImmediatelyBox.IsEnabled = waitDepsOk;
        WaitFirstZhBox.IsEnabled = waitDepsOk && PlayImmediatelyBox.IsChecked != true;
        WaitZhMinutesPanel.IsEnabled = waitDepsOk
                                       && PlayImmediatelyBox.IsChecked != true
                                       && WaitFirstZhBox.IsChecked == true;
    }

    private void Pref_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshPreferenceGates();
        if (_step == 1)
            RefreshInstallUi();
        else if (_step == 2)
            RefreshDoneSummary();
    }

    private void DoneChoiceBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _step != 2) return;
        AssocError.Visibility = Visibility.Collapsed;
        RefreshDoneSummary();
    }

    private bool TryApplyPlaybackAssociationChoice()
    {
        AssocError.Visibility = Visibility.Collapsed;
        if (AssociatePlaybackBox.IsChecked != true)
            return true;

        var r = SetupWizard.ApplyPlaybackAssociations(_settings);
        if (r.Failed == 0 && string.IsNullOrWhiteSpace(r.LastError))
        {
            PlayerLog.Write(Loc.Format("Settings.Association.ApplyOk", r.Succeeded));
            return true;
        }

        var suffix = string.IsNullOrWhiteSpace(r.LastError) ? "" : " · " + r.LastError;
        var detail = r.Succeeded == 0 && !string.IsNullOrWhiteSpace(r.LastError)
            ? r.LastError
            : Loc.Format("Settings.Association.ApplyPartial", r.Succeeded, r.Failed, suffix);
        PlayerLog.Write(detail);
        if (r.Succeeded > 0)
            return true;

        AssocError.Text = detail;
        AssocError.Visibility = Visibility.Visible;
        return false;
    }

    private async void DownloadAll_Click(object sender, RoutedEventArgs e)
        => await RunInstallAsync(advanceOnSuccess: false);

    private async Task RunInstallAsync(bool advanceOnSuccess)
    {
        if (_busy) return;

        if (!SetupWizard.NeedsInstall(_settings))
        {
            if (advanceOnSuccess)
                ShowStep(2);
            else
                RefreshInstallUi();
            return;
        }

        if (!CanStartInstall())
        {
            InstallBlockHint.Text = Loc.Get("Wizard.Mpv.NoScript");
            InstallBlockHint.Visibility = Visibility.Visible;
            return;
        }

        SetBusy(true);
        InstallProgress.Text = Loc.Get("Wizard.Install.Downloading");
        InstallBlockHint.Visibility = Visibility.Collapsed;
        DownloadAllButton.Visibility = Visibility.Collapsed;
        _workCts = new CancellationTokenSource();
        try
        {
            await SetupWizard.EnsureAllComponentsAsync(
                _settings,
                msg => Dispatcher.BeginInvoke(() => InstallProgress.Text = msg),
                line =>
                {
                    PlayerLog.Write(line);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            InstallProgress.Text = line;
                    });
                },
                _workCts.Token);
            _detect = SetupWizard.Detect(_settings);
            InstallProgress.Text = Loc.Get("Wizard.Install.Ready");
            if (advanceOnSuccess && SetupWizard.CanProceedFromInstall(_settings))
                ShowStep(2);
            else
                RefreshInstallUi();
        }
        catch (OperationCanceledException)
        {
            InstallProgress.Text = Loc.Get("Wizard.Cancelled");
            RefreshInstallUi();
        }
        catch (Exception ex)
        {
            InstallProgress.Text = Loc.Format("Wizard.Install.Failed", ex.Message);
            RefreshInstallUi();
        }
        finally
        {
            SetBusy(false);
            RefreshInstallUi();
            DisposeWorkCts();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BackButton.IsEnabled = !busy;
        UiLanguageBox.IsEnabled = !busy;
        DownloadAllButton.IsEnabled = !busy && CanStartInstall();
        RefreshNextButton();
    }

    private void DisposeWorkCts()
    {
        _workCts?.Dispose();
        _workCts = null;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _step <= 0) return;
        ShowStep(_step - 1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _workCts?.Cancel();
            return;
        }

        if (_step == 0)
        {
            ApplyPreferencesToSettings();
            ShowStep(1);
            return;
        }

        if (_step == 1)
        {
            if (SetupWizard.NeedsInstall(_settings))
            {
                await RunInstallAsync(advanceOnSuccess: true);
                return;
            }

            if (!SetupWizard.CanProceedFromInstall(_settings))
            {
                RefreshInstallUi();
                return;
            }

            ShowStep(2);
            return;
        }

        FinishAndClose(success: true);
    }

    private void FinishAndClose(bool success)
    {
        if (_busy) return;
        if (_step == 2 && !TryApplyPlaybackAssociationChoice())
            return;

        if (success)
            SetupWizard.MarkComplete(_settings);
        _completed = success;
        DialogResult = success;
        _allowClose = true;
        Close();
    }

    private async void SetupWizardWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;

        if (_busy)
        {
            e.Cancel = true;
            _workCts?.Cancel();
            for (var i = 0; i < 80 && _busy; i++)
                await Task.Delay(50).ConfigureAwait(true);
            _allowClose = true;
            _completed = false;
            await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static void SelectComboValue(ComboBox box, string value, string fallback)
    {
        box.SelectedValue = value;
        if (box.SelectedItem is null)
            box.SelectedValue = fallback;
    }

    private static string NormalizeWaitZhMinutes(double minutes)
    {
        var options = new[] { 0, 0.5, 1, 2, 3, 5, 10 };
        var best = options[0];
        var bestDist = double.MaxValue;
        foreach (var o in options)
        {
            var d = Math.Abs(o - minutes);
            if (d < bestDist)
            {
                bestDist = d;
                best = o;
            }
        }

        return best.ToString(CultureInfo.InvariantCulture);
    }

    private static double ParseWaitZhMinutes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 1;
        return double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 30)
            : 1;
    }
}
