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

public partial class SettingsWindow : Window
{
    public const int TabGeneral = 0;
    public const int TabPlayback = 1;
    public const int TabSubtitle = 2;
    public const int TabModels = 3;
    public const int TabAssociation = 4;
    public const int TabAdvanced = 5;
    /// <summary>Obsolete alias — use <see cref="TabModels"/>.</summary>
    public const int TabPresets = TabModels;

    private readonly AppSettings _owner;
    /// <summary>Editable draft — Cancel discards; Save copies back to <see cref="_owner"/>.</summary>
    private readonly AppSettings _settings;
    private bool _busy;
    private bool _syncingSubUi;
    private bool _uiLangBoxSync;
    /// <summary>UI lang used to soft-sync TranslateTarget when user still follows the UI default.</summary>
    private string _uiLangSyncAnchor = UiLanguages.Auto;
    private string _lastContentSubMode = "zh";
    private CancellationTokenSource? _packCts;
    private AsrPipeline? _packAsr;
    private readonly Dictionary<string, CheckBox> _associationBoxes = new(StringComparer.OrdinalIgnoreCase);

    // DisplayMemberPath 关闭态可能回退 ToString；勿显示 record 调试文本。
    private sealed record LabeledOption(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(AppSettings settings, int selectedTab = 0)
    {
        _owner = settings;
        _settings = settings.Clone();
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, WindowChromeUtil.Create(40, canResize: true));
        Closing += SettingsWindow_Closing;
        BindStaticLists();
        LoadFromSettings();
        WireSliderLabels();
        BuildAssociationUi();
        RefreshModelsTab();
        RefreshStatus();
        AsrBackendBox.SelectionChanged += (_, _) => RefreshStatus();
        SettingsTabs.SelectionChanged += SettingsTabs_SelectionChanged;
        if (selectedTab >= 0 && selectedTab < SettingsTabs.Items.Count)
            SettingsTabs.SelectedIndex = selectedTab;
    }

    private void BindStaticLists()
    {
        UiLanguageBox.ItemsSource = BuildUiLanguageOptions();
        UiLanguageBox.DisplayMemberPath = nameof(LabeledOption.Label);
        UiLanguageBox.SelectedValuePath = nameof(LabeledOption.Value);

        RecentMaxBox.ItemsSource = new[] { 0, 5, 8, 12, 16, 20 };
        DefaultSpeedBox.ItemsSource = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
        SeekStepBox.ItemsSource = new[] { 3, 5, 10, 15, 30 };
        SeekFineBox.ItemsSource = new[] { 1, 2, 5 };
        SeekLargeBox.ItemsSource = new[] { 30, 60, 120, 300 };
        HwDecBox.ItemsSource = new[]
        {
            new LabeledOption(Loc.Get("Settings.HwDec.Auto"), "auto"),
            new LabeledOption(Loc.Get("Settings.HwDec.No"), "no"),
            new LabeledOption(Loc.Get("Settings.HwDec.D3d11va"), "d3d11va"),
            new LabeledOption(Loc.Get("Settings.HwDec.Dxva2"), "dxva2"),
            new LabeledOption(Loc.Get("Settings.HwDec.Nvdec"), "nvdec"),
        };
        HwDecBox.DisplayMemberPath = nameof(LabeledOption.Label);
        HwDecBox.SelectedValuePath = nameof(LabeledOption.Value);
        VideoFitBox.ItemsSource = new[]
        {
            new LabeledOption(Loc.Get("Settings.VideoFit.Window"), "window"),
            new LabeledOption(Loc.Get("Settings.VideoFit.Contain"), "contain"),
            new LabeledOption(Loc.Get("Settings.VideoFit.Cover"), "cover"),
            new LabeledOption(Loc.Get("Settings.VideoFit.Stretch"), "stretch"),
        };
        VideoFitBox.DisplayMemberPath = nameof(LabeledOption.Label);
        VideoFitBox.SelectedValuePath = nameof(LabeledOption.Value);
        SubFontBox.ItemsSource = new[]
        {
            "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "DengXian", "Arial", "Segoe UI",
        };
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
        TranslateTargetBox.ItemsSource = BuildTranslateTargetOptions();
        TranslateTargetBox.DisplayMemberPath = nameof(LabeledOption.Label);
        TranslateTargetBox.SelectedValuePath = nameof(LabeledOption.Value);

        TranslateModelBox.ItemsSource = new[]
        {
            new LabeledOption(Loc.Get("TranslateModel.TranslateGemma"), TranslateModels.TranslateGemma4B),
        };
        TranslateModelBox.DisplayMemberPath = nameof(LabeledOption.Label);
        TranslateModelBox.SelectedValuePath = nameof(LabeledOption.Value);

        AsrModelBox.ItemsSource = new[]
        {
            new LabeledOption(Loc.Get("AsrModel.Auto"), ModelPicker.Auto),
            new LabeledOption(Loc.Get("AsrModel.Turbo"), ModelPicker.Turbo),
        };
        AsrModelBox.DisplayMemberPath = nameof(LabeledOption.Label);
        AsrModelBox.SelectedValuePath = nameof(LabeledOption.Value);
    }

    private static LabeledOption[] BuildTranslateTargetOptions() =>
    [
        new(Loc.Get("Settings.TranslateTarget.Zh"), TranslateTargets.Zh),
        new(Loc.Get("Settings.TranslateTarget.ZhHant"), TranslateTargets.ZhHant),
        new(Loc.Get("Settings.TranslateTarget.En"), TranslateTargets.En),
        new(Loc.Get("Settings.TranslateTarget.Ja"), TranslateTargets.Ja),
        new(Loc.Get("Settings.TranslateTarget.Ko"), TranslateTargets.Ko),
    ];

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

    private void LoadFromSettings()
    {
        var uiLang = string.IsNullOrWhiteSpace(_settings.UiLanguage) ? UiLanguages.Auto : _settings.UiLanguage.Trim();
        if (!UiLanguages.IsKnownTag(uiLang))
            uiLang = UiLanguages.Auto;
        SelectComboValue(UiLanguageBox, uiLang, UiLanguages.Auto);
        _uiLangSyncAnchor = uiLang;
        UiLanguageBox.SelectionChanged -= UiLanguageBox_SelectionChanged;
        UiLanguageBox.SelectionChanged += UiLanguageBox_SelectionChanged;

        RememberBoundsBox.IsChecked = _settings.RememberWindowBounds;
        LockAspectBox.IsChecked = _settings.LockWindowAspectRatio;
        AlwaysOnTopBox.IsChecked = _settings.AlwaysOnTop;
        HideChromeBox.IsChecked = _settings.HideChromeInFullscreen;
        HideDelaySlider.Value = Math.Clamp(_settings.FullscreenHideDelaySec, 1, 8);
        ScreenshotDirBox.Text = _settings.ScreenshotDir;
        RememberRecentBox.IsChecked = _settings.RememberRecentFiles;
        RecentMaxBox.SelectedItem = PickClosest(RecentMaxBox.Items.Cast<int>(), _settings.RecentFilesMax);
        RememberRecentBox_Changed(null, null);

        AutoPlayBox.IsChecked = _settings.AutoPlayOnOpen;
        AutoPlayNextBox.IsChecked = _settings.AutoPlayNext;
        AddSameFolderBox.IsChecked = _settings.AddSameFolderToPlaylist;
        PrefetchPlaylistBox.IsChecked = _settings.PrefetchPlaylistSubtitles;
        RememberPosBox.IsChecked = _settings.RememberPlaybackPosition;
        DefaultVolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 130);
        DefaultVolumeLabel.Text = ((int)DefaultVolumeSlider.Value).ToString();
        DefaultSpeedBox.SelectedItem = PickClosest(DefaultSpeedBox.Items.Cast<double>(), _settings.Speed <= 0 ? 1.0 : _settings.Speed);
        SeekStepBox.SelectedItem = PickClosest(SeekStepBox.Items.Cast<int>(), _settings.SeekStepSeconds);
        SeekFineBox.SelectedItem = PickClosest(SeekFineBox.Items.Cast<int>(), _settings.SeekStepFineSeconds);
        SeekLargeBox.SelectedItem = PickClosest(SeekLargeBox.Items.Cast<int>(), _settings.SeekStepLargeSeconds);
        SelectComboValue(HwDecBox, NormalizeHwDec(_settings.HwDec), "auto");
        SelectComboValue(VideoFitBox, NormalizeVideoFit(_settings.VideoFit), "window");

        var subMode = SubtitleDisplayModeUtil.ToSetting(SubtitleDisplayModeUtil.Parse(_settings.SubtitleMode));
        if (!string.Equals(subMode, "off", StringComparison.OrdinalIgnoreCase))
            _lastContentSubMode = subMode;
        PreferExternalBox.IsChecked = _settings.PreferExternalSubtitle;
        SubtitleCatBox.IsChecked = _settings.FetchSubtitleFromSubtitleCat;
        AutoPreviewBox.IsChecked = _settings.AutoStartPreview;
        TranslateBox.IsChecked = _settings.TranslateEnabled;
        SelectComboValue(
            TranslateTargetBox,
            TranslateTargets.Normalize(_settings.TranslateTarget),
            TranslateTargets.FromUiLanguage(_settings.UiLanguage));
        _settings.AsrQuality = AsrQualities.Better;
        SelectComboValue(
            AsrModelBox,
            ModelPicker.Normalize(_settings.AsrModel),
            ModelPicker.Auto);
        SelectComboValue(
            TranslateModelBox,
            TranslateModels.Normalize(_settings.TranslateModelId),
            TranslateModels.TranslateGemma4B);
        UpdateAsrModelHint();
        RebuildSubModeOptions();
        TextSanitizeBox.IsChecked = _settings.TextSanitizeEnabled;
        GlossaryPathBox.Text = _settings.GlossaryPath;
        PlayImmediatelyBox.IsChecked = _settings.PlayImmediatelyOnOpen;
        WaitFirstZhBox.IsChecked = _settings.WaitForFirstZhBeforePlay;
        SelectComboValue(WaitZhMinutesBox, NormalizeWaitZhMinutes(_settings.WaitForZhMinutes), "1");
        FullscreenQuietOsdBox.IsChecked = _settings.FullscreenQuietOsd;
        ApplySubModeToUi(subMode);
        RefreshDependencyGates();
        SubFontBox.Text = string.IsNullOrWhiteSpace(_settings.SubFont) ? "Microsoft YaHei" : _settings.SubFont;
        SubFontSizeSlider.Value = Math.Clamp(_settings.SubFontSize, 20, 72);
        SubBoldBox.IsChecked = _settings.SubBold;
        SubBorderSlider.Value = Math.Clamp(_settings.SubBorderSize, 0, 6);
        SubMarginSlider.Value = Math.Clamp(_settings.SubMarginY, 0, 120);
        SubDelayBox.Text = _settings.SubDelaySec.ToString("0.0", CultureInfo.InvariantCulture);
        UpdateSubSliderLabels();

        HfBox.Text = _settings.HfEndpoint;
        TranslateUrlBox.Text = _settings.TranslateUrl;
        PopulateAsrBackendBox();
        SelectComboValue(AsrBackendBox, AsrBackends.Normalize(_settings.AsrBackend), AsrBackends.Auto);
        PopulateUpdateSourceBox();
        SelectComboValue(UpdateSourceBox, AppUpdateEndpoints.Normalize(_settings.UpdateSource), AppUpdateEndpoints.Auto);
        CheckUpdatesOnStartupBox.IsChecked = _settings.CheckUpdatesOnStartup == true;
        HideDelayLabel.Text = Loc.Format("Settings.HideDelay.Current", HideDelaySlider.Value.ToString("0.#", CultureInfo.CurrentUICulture));
        ModelsPathBox.Text = _settings.ModelsPath;
        AdvancedLlmPathBox.Text = _settings.AdvancedLlmPath;
        UpdateSubPreview();
    }

    private void WireSliderLabels()
    {
        HideDelaySlider.ValueChanged += (_, _) =>
            HideDelayLabel.Text = Loc.Format("Settings.HideDelay.Current", HideDelaySlider.Value.ToString("0.#", CultureInfo.CurrentUICulture));
        DefaultVolumeSlider.ValueChanged += (_, e) => DefaultVolumeLabel.Text = ((int)e.NewValue).ToString();
        SubFontSizeSlider.ValueChanged += (_, _) => UpdateSubSliderLabels();
        SubBorderSlider.ValueChanged += (_, _) => UpdateSubSliderLabels();
        SubMarginSlider.ValueChanged += (_, _) => UpdateSubSliderLabels();
    }

    private void UpdateSubSliderLabels()
    {
        SubFontSizeLabel.Text = ((int)SubFontSizeSlider.Value).ToString();
        SubBorderLabel.Text = ((int)SubBorderSlider.Value).ToString();
        SubMarginLabel.Text = ((int)SubMarginSlider.Value).ToString();
        UpdateSubPreview();
    }

    private void UpdateSubPreview()
    {
        if (SubPreviewSample is null) return;
        var size = Math.Max(12, (int)SubFontSizeSlider.Value * 0.55);
        SubPreviewSample.FontSize = size;
        SubPreviewSample.FontWeight = SubBoldBox.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        SubPreviewSample.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(SubFontBox.Text) ? "Segoe UI" : SubFontBox.Text);
        SubPreviewSample.Margin = new Thickness(0, 0, 0, (int)SubMarginSlider.Value * 0.25);
        var border = (int)SubBorderSlider.Value;
        if (border > 0)
        {
            SubPreviewSample.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = border * 2,
                ShadowDepth = 0,
                Opacity = 0.9,
            };
        }
        else
        {
            SubPreviewSample.Effect = null;
        }
    }

    private void SubFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSubPreview();
    private void SubBoldBox_Changed(object sender, RoutedEventArgs e) => UpdateSubPreview();

    private void PopulateAsrBackendBox()
    {
        AsrBackendBox.ItemsSource = AsrBackends.Selectable
            .Select(id => new LabeledOption(AsrBackends.DisplayName(id), id))
            .ToList();
        AsrBackendBox.DisplayMemberPath = nameof(LabeledOption.Label);
        AsrBackendBox.SelectedValuePath = nameof(LabeledOption.Value);
    }

    private void PopulateUpdateSourceBox()
    {
        UpdateSourceBox.ItemsSource = new List<LabeledOption>
        {
            new(Loc.Get("Settings.Advanced.UpdateSource.Auto"), AppUpdateEndpoints.Auto),
            new(Loc.Get("Settings.Advanced.UpdateSource.GitHub"), AppUpdateEndpoints.GitHub),
            new(Loc.Get("Settings.Advanced.UpdateSource.GitCode"), AppUpdateEndpoints.GitCode),
        };
        UpdateSourceBox.DisplayMemberPath = nameof(LabeledOption.Label);
        UpdateSourceBox.SelectedValuePath = nameof(LabeledOption.Value);
    }

    private void RefreshStatus()
    {
        SnapshotEngineFields();
        var models = AsrModelStore.ResolveModelsRoot(_settings);
        var turbo = DescribeAsrPackStatus(models, ModelPicker.InstallTarget(_settings.AsrModel));
        var llama = ManagedLlmInstaller.HasLlamaRuntime(_settings);
        var gguf = ManagedLlmInstaller.HasPreferredGguf(_settings);
        EngineStatusLabel.Text = Loc.Format(
            "Settings.Advanced.EmbeddedStatus",
            AsrBackends.DisplayName(_settings.AsrBackend),
            models);
        PackStatusLabel.Text =
            $"{turbo} · "
            + $"{(llama ? Loc.Get("Settings.PackStatus.LlamaReady") : Loc.Get("Settings.PackStatus.LlamaMissing"))} · "
            + $"{(gguf ? Loc.Get("Settings.PackStatus.GgufReady") : Loc.Get("Settings.PackStatus.GgufMissing"))}";

        var caps = AsrRuntime.EnrichCapabilities(EngineCapabilities.ForEmbedded(models), _settings);
        if (EngineCapsLabel is not null)
            EngineCapsLabel.Text = Loc.Format("Settings.EngineCaps.Line", caps.FormatStatusLine());
    }

    private static string DescribeAsrPackStatus(string modelsRoot, string modelId)
    {
        var id = ModelPicker.Normalize(modelId);
        if (AsrModelStore.IsInstalled(modelsRoot, id))
            return Loc.Format("Settings.PackStatus.AsrReady", AsrModelCatalog.DisplayName(id));

        var dir = Path.Combine(modelsRoot, "asr", id);
        if (string.Equals(id, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)
            && AsrModelStore.IsTurboPartial(modelsRoot))
            return Loc.Format("Settings.PackStatus.AsrPartial", AsrModelCatalog.DisplayName(id));
        if (AsrModelIntegrity.IsPartiallyPresent(dir, id))
            return Loc.Format("Settings.PackStatus.AsrPartial", AsrModelCatalog.DisplayName(id));

        return Loc.Format("Settings.PackStatus.AsrMissing", AsrModelCatalog.DisplayName(id));
    }

    private void SnapshotEngineFields()
    {
        _settings.AsrBackend = AsrBackendBox.SelectedValue as string ?? AsrBackends.Auto;
        _settings.HfEndpoint = HfEndpoints.NormalizeOrDefault(HfBox.Text);
        _settings.UpdateSource = AppUpdateEndpoints.Normalize(UpdateSourceBox.SelectedValue as string);
        _settings.CheckUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
    }

    private void SnapshotAll()
    {
        _settings.UiLanguage = UiLanguageBox.SelectedValue as string ?? UiLanguages.Auto;

        _settings.RememberWindowBounds = RememberBoundsBox.IsChecked == true;
        _settings.LockWindowAspectRatio = LockAspectBox.IsChecked == true;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _settings.HideChromeInFullscreen = HideChromeBox.IsChecked == true;
        _settings.FullscreenHideDelaySec = HideDelaySlider.Value;
        _settings.ScreenshotDir = ScreenshotDirBox.Text.Trim();
        _settings.RememberRecentFiles = RememberRecentBox.IsChecked != false;
        _settings.RecentFilesMax = RecentMaxBox.SelectedItem is int max ? max : 12;
        RecentFiles.Trim(_settings);

        _settings.AutoPlayOnOpen = AutoPlayBox.IsChecked != false;
        _settings.AutoPlayNext = AutoPlayNextBox.IsChecked != false;
        _settings.AddSameFolderToPlaylist = AddSameFolderBox.IsChecked == true;
        _settings.PrefetchPlaylistSubtitles = PrefetchPlaylistBox.IsChecked != false;
        _settings.RememberPlaybackPosition = RememberPosBox.IsChecked == true;
        _settings.Volume = (int)DefaultVolumeSlider.Value;
        _settings.Speed = DefaultSpeedBox.SelectedItem is double sp ? sp : 1.0;
        _settings.SeekStepSeconds = SeekStepBox.SelectedItem is int seek ? seek : 5;
        _settings.SeekStepFineSeconds = SeekFineBox.SelectedItem is int fine ? fine : 1;
        _settings.SeekStepLargeSeconds = SeekLargeBox.SelectedItem is int large ? large : 30;
        _settings.HwDec = HwDecBox.SelectedValue as string ?? "auto";
        _settings.VideoFit = VideoFitBox.SelectedValue as string ?? "window";

        var subMode = SubModeBox.SelectedValue as string ?? "zh";
        if (SubVisibleBox.IsChecked == false)
            subMode = "off";
        else if (string.Equals(subMode, "off", StringComparison.OrdinalIgnoreCase))
            subMode = _lastContentSubMode;
        _settings.SubtitleMode = subMode;
        _settings.SubVisibleOnStart = !string.Equals(subMode, "off", StringComparison.OrdinalIgnoreCase);
        _settings.PreferExternalSubtitle = PreferExternalBox.IsChecked != false;
        _settings.FetchSubtitleFromSubtitleCat = SubtitleCatBox.IsChecked == true;
        _settings.AutoStartPreview = AutoPreviewBox.IsChecked != false;
        _settings.TranslateEnabled = TranslateBox.IsChecked == true;
        _settings.TranslateTarget = TranslateTargetBox.SelectedValue as string
            ?? TranslateTargets.FromUiLanguage(_settings.UiLanguage);
        _settings.AsrQuality = AsrQualities.Better;
        _settings.AsrModel = ModelPicker.Normalize(AsrModelBox.SelectedValue as string);
        _settings.TranslateModelId = TranslateModels.Normalize(TranslateModelBox.SelectedValue as string);
        _settings.TextSanitizeEnabled = TextSanitizeBox.IsChecked != false;
        _settings.GlossaryPath = GlossaryPathBox.Text.Trim();
        _settings.PlayImmediatelyOnOpen = PlayImmediatelyBox.IsChecked == true;
        _settings.WaitForFirstZhBeforePlay = WaitFirstZhBox.IsChecked == true;
        _settings.WaitForZhMinutes = ParseWaitZhMinutes(WaitZhMinutesBox.SelectedValue as string);
        _settings.FullscreenQuietOsd = FullscreenQuietOsdBox.IsChecked != false;
        _settings.SubFont = string.IsNullOrWhiteSpace(SubFontBox.Text) ? "Microsoft YaHei" : SubFontBox.Text.Trim();
        _settings.SubFontSize = (int)SubFontSizeSlider.Value;
        _settings.SubBold = SubBoldBox.IsChecked == true;
        _settings.SubBorderSize = (int)SubBorderSlider.Value;
        _settings.SubMarginY = (int)SubMarginSlider.Value;
        _settings.SubDelaySec = ParseDelay(SubDelayBox.Text);

        SnapshotEngineFields();
        _settings.TranslateUrl = string.IsNullOrWhiteSpace(TranslateUrlBox.Text) ? "http://127.0.0.1:39281" : TranslateUrlBox.Text.Trim();
        _settings.ModelsPath = ModelsPathBox.Text.Trim();
        _settings.AdvancedLlmPath = AdvancedLlmPathBox.Text.Trim();
        SnapshotAssociationSelections();
    }

    private void BuildAssociationUi()
    {
        _associationBoxes.Clear();
        AssociationGroups.Items.Clear();

        foreach (var category in MediaFileTypes.Categories)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("FieldLabel"),
                Text = Loc.Get(category.LabelKey),
            });

            var wrap = new WrapPanel();
            foreach (var ext in category.Extensions)
            {
                var box = new CheckBox
                {
                    Content = ext,
                    Margin = new Thickness(0, 0, 16, 6),
                    Tag = ext,
                };
                box.Checked += (_, _) => RefreshAssociationStatus();
                box.Unchecked += (_, _) => RefreshAssociationStatus();
                _associationBoxes[ext] = box;
                wrap.Children.Add(box);
            }

            panel.Children.Add(wrap);
            AssociationGroups.Items.Add(panel);
        }

        LoadAssociationState();
    }

    private void LoadAssociationState()
    {
        var saved = new HashSet<string>(
            _settings.AssociatedExtensions.Select(MediaFileTypes.NormalizeExtension),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (ext, box) in _associationBoxes)
        {
            var norm = MediaFileTypes.NormalizeExtension(ext);
            box.IsChecked = FileAssociationService.IsAssociated(ext) || saved.Contains(norm);
        }

        RefreshAssociationStatus();
    }

    private void RefreshAssociationStatus()
    {
        var total = _associationBoxes.Count;
        var active = FileAssociationService.CountAssociated(_associationBoxes.Keys);
        var selected = _associationBoxes.Values.Count(b => b.IsChecked == true);
        AssociationStatusLabel.Text = Loc.Format("Settings.Association.Status", active, total, selected);
    }

    private void SnapshotAssociationSelections()
    {
        _settings.AssociatedExtensions = _associationBoxes
            .Where(kv => kv.Value.IsChecked == true)
            .Select(kv => MediaFileTypes.NormalizeExtension(kv.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<string> SelectedAssociationExtensions()
        => _associationBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key);

    private IEnumerable<string> DeselectedAssociationExtensions()
        => _associationBoxes.Where(kv => kv.Value.IsChecked != true).Select(kv => kv.Key);

    private void AssociationSelectVideo_Click(object sender, RoutedEventArgs e)
    {
        var video = MediaFileTypes.Categories[0].Extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (ext, box) in _associationBoxes)
            box.IsChecked = video.Contains(ext);
        RefreshAssociationStatus();
    }

    private void AssociationSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in _associationBoxes.Values)
            box.IsChecked = true;
        RefreshAssociationStatus();
    }

    private void AssociationSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var box in _associationBoxes.Values)
            box.IsChecked = false;
        RefreshAssociationStatus();
    }

    private void AssociationApply_Click(object sender, RoutedEventArgs e)
    {
        var associate = SelectedAssociationExtensions().ToList();
        var remove = DeselectedAssociationExtensions().ToList();

        var ok = 0;
        var failed = 0;
        string? lastError = null;

        if (associate.Count > 0)
        {
            var r = FileAssociationService.Apply(associate, associate: true);
            ok += r.Succeeded;
            failed += r.Failed;
            lastError = r.LastError;
        }

        if (remove.Count > 0)
        {
            var r = FileAssociationService.Apply(remove, associate: false);
            ok += r.Succeeded;
            failed += r.Failed;
            lastError ??= r.LastError;
        }

        SnapshotAssociationSelections();
        // Associations apply immediately to the live settings (not only on Save).
        _owner.AssociatedExtensions = [.._settings.AssociatedExtensions];
        _owner.Save();
        LoadAssociationState();

        if (failed == 0)
            AssociationStatusLabel.Text = Loc.Format("Settings.Association.ApplyOk", ok);
        else
        {
            var suffix = string.IsNullOrWhiteSpace(lastError) ? "" : " · " + lastError;
            AssociationStatusLabel.Text = Loc.Format("Settings.Association.ApplyPartial", ok, failed, suffix);
        }
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsTabs.SelectedIndex == TabModels)
            RefreshModelsTab();
    }

    private void RefreshModelsTab()
    {
        SnapshotEngineFields();
        SnapshotModelsFields();
        var summary = PresetAssetManager.SummarizeModels(_settings);
        var engine = EngineLocator.Find(_settings);
        var modelsRoot = EngineLocator.ResolveModelsRoot(_settings, engine);
        var preferred = summary.PreferredAsrId;
        var asrDir = Path.Combine(modelsRoot, "asr", preferred);
        var asrPartial = !summary.HasPreferredAsr
            && (string.Equals(preferred, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)
                ? AsrModelStore.IsTurboPartial(modelsRoot)
                : AsrModelIntegrity.IsPartiallyPresent(asrDir, preferred));
        var llamaOk = ManagedLlmInstaller.HasLlamaRuntime(_settings);
        var report = PresetReadiness.AnalyzeDisk(_settings, _settings.TranslateEnabled);
        UpdateAsrModelHint();

        if (ModelsStatusLabel is not null)
            ModelsStatusLabel.Text = $"{summary.AsrStatus} · {summary.GgufStatus}";

        if (TranslateModelHintLabel is not null)
            TranslateModelHintLabel.Text = Loc.Get("TranslateModel.TranslateGemma.Hint");

        if (AsrCardStatusLabel is not null)
        {
            var size = AsrModelCatalog.SizeHint(preferred);
            AsrCardStatusLabel.Text = summary.HasPreferredAsr
                ? Loc.Get("Settings.Models.Status.QualityReady")
                : asrPartial
                    ? Loc.Get("Settings.Models.Status.QualityPartial")
                    : Loc.Format("Settings.Models.Status.QualityMissingSized", size);
        }

        if (MtCardStatusLabel is not null)
        {
            var sizeHint = TranslateModels.ResolveSpec(_settings.TranslateModelId).SizeHint;
            MtCardStatusLabel.Text = (llamaOk, summary.HasGguf) switch
            {
                (true, true) => TransubSharedAssets.DescribeReuse(_settings).TranslateModel
                    ? Loc.Get("Settings.Models.Status.TranslateReadyShared")
                    : Loc.Get("Settings.Models.Status.TranslateReady"),
                (true, false) => Loc.Format("Settings.Models.Status.TranslatePartialSized", sizeHint),
                (false, true) => Loc.Get("Settings.Models.Status.TranslateRuntimeMissing"),
                _ => Loc.Format("Settings.Models.Status.TranslateMissingSized", sizeHint),
            };
        }

        var canInstall = !_busy && report.HasGaps;
        if (ModelsOneClickButton is not null)
        {
            ModelsOneClickButton.IsEnabled = canInstall;
            ModelsOneClickButton.Content = report.HasGaps
                ? Loc.Get("Settings.Models.OneClickInstall")
                : Loc.Get("Settings.Models.OneClickReady");
        }

        if (ModelsOneClickHintLabel is not null)
        {
            ModelsOneClickHintLabel.Text = report.HasGaps
                ? Loc.Get("Settings.Models.OneClickHint.Need")
                : Loc.Get("Settings.Models.OneClickHint.Ready");
        }

        var asrName = AsrModelCatalog.DisplayName(preferred);
        if (InstallQualityButton is not null)
        {
            InstallQualityButton.Content = Loc.Format("Settings.Models.InstallNamed", asrName);
            InstallQualityButton.IsEnabled = !_busy && (!summary.HasPreferredAsr || asrPartial);
        }
        if (DeleteQualityButton is not null)
        {
            DeleteQualityButton.Content = Loc.Format("Settings.Models.DeleteNamed", asrName);
            DeleteQualityButton.IsEnabled = !_busy && summary.HasPreferredAsr;
        }
        if (InstallTranslateButton is not null)
            InstallTranslateButton.IsEnabled = !_busy && (!summary.HasGguf || !llamaOk);
        if (DeleteTranslateButton is not null)
            DeleteTranslateButton.IsEnabled = !_busy && summary.HasGguf;

        if (PresetStatusLabel is null || _busy)
            return;

        var qualityTip = Loc.Format(
            "Settings.Models.QualityNeedInstallNamed",
            asrName,
            AsrModelCatalog.SizeHint(preferred));
        var tipPrefix = Loc.Get("Settings.Models.QualityNeedInstallPrefix");
        if (!summary.HasPreferredAsr)
        {
            if (string.IsNullOrWhiteSpace(PresetStatusLabel.Text)
                || PresetStatusLabel.Text.StartsWith(tipPrefix, StringComparison.Ordinal))
                PresetStatusLabel.Text = qualityTip;
        }
        else if (PresetStatusLabel.Text.StartsWith(tipPrefix, StringComparison.Ordinal))
        {
            PresetStatusLabel.Text = "";
        }
    }

    private void AsrModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SnapshotModelsFields();
        PresetReadiness.InvalidateDiskProbe();
        RefreshModelsTab();
    }

    private void TranslateModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SnapshotModelsFields();
        PresetReadiness.InvalidateDiskProbe();
        RefreshModelsTab();
    }

    private void SnapshotModelsFields()
    {
        _settings.AsrQuality = AsrQualities.Better;
        if (AsrModelBox?.SelectedValue is string asrModel)
            _settings.AsrModel = ModelPicker.Normalize(asrModel);
        if (TranslateModelBox?.SelectedValue is string mtModel)
            _settings.TranslateModelId = TranslateModels.Normalize(mtModel);
        if (TranslateTargetBox?.SelectedValue is string tgt)
            _settings.TranslateTarget = TranslateTargets.Normalize(tgt);
        if (ModelsPathBox is not null)
            _settings.ModelsPath = ModelsPathBox.Text.Trim();
        if (AdvancedLlmPathBox is not null)
            _settings.AdvancedLlmPath = AdvancedLlmPathBox.Text.Trim();
    }

    private void UpdateAsrModelHint()
    {
        var id = ModelPicker.Normalize(
            AsrModelBox?.SelectedValue as string ?? _settings.AsrModel);
        var hint = ModelPicker.Normalize(id) switch
        {
            ModelPicker.Turbo => Loc.Get("AsrModel.Turbo.Hint"),
            _ => Loc.Get("AsrModel.Auto.Hint"),
        };
        if (AsrModelHintLabel is not null)
            AsrModelHintLabel.Text = hint;
        if (AsrQualityHintLabel is not null)
            AsrQualityHintLabel.Text = hint;
    }

    private void ManualInstallAsr_Click(object sender, RoutedEventArgs e)
        => RunManualInstallForKinds(SetPackStatus, PresetGapKind.AsrModel);

    private void ManualInstallTranslate_Click(object sender, RoutedEventArgs e)
        => RunManualInstallForKinds(
            SetPackStatus,
            PresetGapKind.GgufModel,
            PresetGapKind.LlamaRuntime);

    private void ManualInstallLlama_Click(object sender, RoutedEventArgs e)
        => RunManualInstallForKinds(SetPackStatus, PresetGapKind.LlamaRuntime);

    /// <summary>
    /// Pack / download progress lives on Advanced (<see cref="PackStatusLabel"/>) and Models
    /// (<see cref="PresetStatusLabel"/>). Models-tab actions must update both or the UI looks dead.
    /// </summary>
    private void SetPackStatus(string text)
    {
        PackStatusLabel.Text = text;
        if (PresetStatusLabel is not null)
            PresetStatusLabel.Text = text;
    }

    private void ImportAsrModel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SnapshotEngineFields();
        SnapshotModelsFields();
        var report = PresetReadiness.AnalyzeDisk(_settings, _settings.TranslateEnabled);
        var dlg = new OpenFolderDialog { Title = Loc.Get("Settings.ManualInstall.PickFolder") };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var id = ModelManualInstall.ImportAsrFromFolder(
                dlg.FolderName,
                _settings,
                ModelManualInstall.AsrCandidates(report));
            FinishManualInstallCheck(
                text => SetPackStatus(Loc.Format("Settings.ManualInstall.ImportDone", AsrModelCatalog.DisplayName(id))));
        }
        catch (Exception ex)
        {
            SetPackStatus(ex.Message);
        }
    }

    private void RunManualInstallForKinds(Action<string> setStatus, params PresetGapKind[] kinds)
    {
        SnapshotEngineFields();
        SnapshotModelsFields();
        var full = PresetReadiness.AnalyzeDisk(_settings, _settings.TranslateEnabled);
        var kindSet = kinds.ToHashSet();
        var gaps = full.Gaps.Where(g => kindSet.Contains(g.Kind)).ToList();
        var report = new PresetGapReport
        {
            PresetId = full.PresetId,
            PresetName = full.PresetName,
            PreferredAsr = full.PreferredAsr,
            FallbackAsr = full.FallbackAsr,
            Gaps = gaps,
        };

        if (!report.HasGaps)
        {
            // Still open the relevant folders so users can inspect / replace files.
            OpenManualFoldersForKinds(kindSet);
            setStatus(Loc.Format("Settings.Presets.AlreadyReady", report.PresetName));
            RefreshModelsTab();
            return;
        }

        MessageBox.Show(
            this,
            ModelManualInstall.BuildInstructions(report, _settings),
            Loc.Get("Settings.ManualInstall.Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        ModelManualInstall.OpenGuidance(report, _settings);
        FinishManualInstallCheck(setStatus);
    }

    private void OpenManualFoldersForKinds(HashSet<PresetGapKind> kinds)
    {
        if (kinds.Contains(PresetGapKind.AsrModel))
            ModelManualInstall.OpenAsrModelFolder(_settings, ModelPicker.InstallTarget(_settings.AsrModel));

        if (kinds.Contains(PresetGapKind.GgufModel))
            ManagedLlmInstaller.OpenModelsFolder(_settings);

        if (kinds.Contains(PresetGapKind.LlamaRuntime))
            ModelManualInstall.OpenLlamaRuntimeFolder(_settings);
    }

    private void FinishManualInstallCheck(Action<string> setStatus)
    {
        PresetReadiness.ClearLivePacks();
        RefreshStatus();
        RefreshModelsTab();
        var post = PresetReadiness.AnalyzeDisk(_settings, _settings.TranslateEnabled);
        setStatus(post.HasGaps
            ? Loc.Format("Settings.ManualInstall.StillMissing", post.SummaryLine())
            : Loc.Format("Settings.Presets.AlreadyReady", post.PresetName));
    }

    private void DeleteAsr_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SnapshotEngineFields();
        SnapshotModelsFields();
        var summary = PresetAssetManager.SummarizeModels(_settings);
        if (!summary.HasPreferredAsr)
        {
            PresetStatusLabel.Text = Loc.Get("Settings.Models.NothingToDelete");
            return;
        }

        var name = AsrModelCatalog.DisplayName(summary.PreferredAsrId);
        var size = AsrModelCatalog.SizeHint(summary.PreferredAsrId);
        if (MessageBox.Show(
                this,
                Loc.Format("Settings.Models.DeleteAsrConfirm", name, size),
                Loc.Get("Settings.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            PresetAssetManager.DeleteAsr(_settings, summary.PreferredAsrId, _ => { });
            NotifyModelsChanged();
            RefreshStatus();
            RefreshModelsTab();
            PresetStatusLabel.Text = Loc.Format("Settings.Models.DeleteNamed", name) + " · OK";
        }
        catch (Exception ex)
        {
            PresetStatusLabel.Text = ex.Message;
        }
    }

    private void DeleteGguf_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SnapshotEngineFields();
        SnapshotModelsFields();
        var summary = PresetAssetManager.SummarizeModels(_settings);
        if (!summary.HasGguf)
        {
            PresetStatusLabel.Text = Loc.Get("Settings.Models.NothingToDelete");
            return;
        }

        if (MessageBox.Show(
                this,
                Loc.Get("Settings.Models.DeleteGgufConfirm"),
                Loc.Get("Settings.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            PresetAssetManager.DeleteGguf(_settings, _ => { });
            NotifyModelsChanged();
            RefreshStatus();
            RefreshModelsTab();
            PresetStatusLabel.Text = Loc.Get("Settings.Models.DeleteTranslate") + " · OK";
        }
        catch (Exception ex)
        {
            PresetStatusLabel.Text = ex.Message;
        }
    }

    private void InstallRuntimeDepsAsync(Action<string> setStatus)
    {
        if (_busy) return;
        SnapshotEngineFields();
        SnapshotModelsFields();
        var report = PresetReadiness.AnalyzeDisk(_settings, _settings.TranslateEnabled);
        if (!report.HasGaps)
        {
            setStatus(Loc.Format("Settings.Presets.AlreadyReady", report.PresetName));
            RefreshModelsTab();
            return;
        }

        if (!report.CanAutoInstallAny)
        {
            setStatus(Loc.Format("Settings.Presets.CannotAuto", report.PresetName, report.SummaryLine()));
            return;
        }

        RunBusyDownload(
            Loc.Format("DownloadProgress.PresetHeading", report.PresetName),
            async (status, ct) =>
            {
                status(Loc.Format("Settings.ModelDownload.InstallingPreset", report.PresetName));
                _packAsr = new AsrPipeline(_settings, status, _ => { });
                await _packAsr.EnsureReadyAsync(ct).ConfigureAwait(true);
                var installer = new PresetDependencyInstaller(_settings, status, _ => { });
                await installer.InstallAsync(report, _packAsr, ct).ConfigureAwait(true);
            },
            onSuccess: () =>
            {
                NotifyModelsChanged();
                RefreshStatus();
                var post = PresetReadiness.AnalyzeDisk(_settings, _settings.TranslateEnabled);
                setStatus(post.HasGaps
                    ? Loc.Format("Settings.Presets.InstallIncomplete", post.PresetName, post.SummaryLine())
                    : Loc.Format("Settings.Presets.InstallDone", post.PresetName));
            });
    }

    /// <summary>Modal progress dialog for Settings downloads; mirrors lines to pack status labels.</summary>
    private void RunBusyDownload(
        string heading,
        Func<Action<string>, CancellationToken, Task> work,
        Action? onSuccess = null)
    {
        if (_busy) return;
        _busy = true;
        // Modal dialog owns Cancel; Settings CancelPack links to the same token for Closing.
        _packCts?.Dispose();
        _packCts = new CancellationTokenSource();
        var packToken = _packCts.Token;
        CancelPackButton.IsEnabled = true;
        if (ModelsCancelPackButton is not null)
            ModelsCancelPackButton.IsEnabled = true;
        RefreshModelsTab();
        try
        {
            var result = DownloadProgressWindow.ShowAndRun(this, heading, async (status, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, packToken);
                await work(line =>
                {
                    status(line);
                    Dispatcher.BeginInvoke(() => SetPackStatus(line));
                }, linked.Token).ConfigureAwait(true);
            });

            switch (result)
            {
                case DownloadProgressResult.Ok:
                    onSuccess?.Invoke();
                    break;
                case DownloadProgressResult.Cancelled:
                    SetPackStatus(Loc.Get("Settings.ModelDownload.Cancelled"));
                    break;
            }
        }
        finally
        {
            DisposePackAsr();
            _busy = false;
            CancelPackButton.IsEnabled = false;
            if (ModelsCancelPackButton is not null)
                ModelsCancelPackButton.IsEnabled = false;
            _packCts?.Dispose();
            _packCts = null;
            RefreshModelsTab();
        }
    }

    private void RememberRecentBox_Changed(object? sender, RoutedEventArgs? e)
    {
        RecentMaxPanel.IsEnabled = RememberRecentBox.IsChecked != false;
    }

    private void DependencyGate_Changed(object sender, RoutedEventArgs e)
        => RefreshDependencyGates();

    private void WaitFirstZhBox_Changed(object? sender, RoutedEventArgs? e)
        => RefreshDependencyGates();

    /// <summary>
    /// Disable controls that have no effect given other checkboxes / subtitle mode.
    /// Does not clear saved values — re-enabling restores the previous choice.
    /// </summary>
    private void RefreshDependencyGates()
    {
        if (!IsLoaded) return;

        var translateOn = TranslateBox.IsChecked == true;
        var autoPreview = AutoPreviewBox.IsChecked != false;
        var autoPlay = AutoPlayBox.IsChecked != false;
        var mode = SubModeBox.SelectedValue as string ?? "zh";
        var modeWantsMt = mode is "zh" or "dual";
        var waitDepsOk = translateOn && autoPreview && autoPlay && modeWantsMt;

        if (TranslateTargetPanel is not null)
            TranslateTargetPanel.IsEnabled = translateOn;

        if (PlayImmediatelyBox is not null)
            PlayImmediatelyBox.IsEnabled = waitDepsOk;
        if (WaitFirstZhBox is not null)
            WaitFirstZhBox.IsEnabled = waitDepsOk && PlayImmediatelyBox?.IsChecked != true;
        if (WaitZhMinutesPanel is not null)
            WaitZhMinutesPanel.IsEnabled = waitDepsOk
                                           && PlayImmediatelyBox?.IsChecked != true
                                           && WaitFirstZhBox?.IsChecked == true;

        var sanitizeOn = TextSanitizeBox.IsChecked != false;
        if (GlossaryPanel is not null)
            GlossaryPanel.IsEnabled = sanitizeOn;

        if (HideDelayPanel is not null)
            HideDelayPanel.IsEnabled = HideChromeBox.IsChecked == true;

        if (PrefetchPlaylistBox is not null)
            PrefetchPlaylistBox.IsEnabled = autoPreview;
    }

    private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiLangBoxSync || UiLanguageBox?.SelectedValue is not string newUi)
            return;

        var prevDefault = TranslateTargets.FromUiLanguage(_uiLangSyncAnchor);
        var newDefault = TranslateTargets.FromUiLanguage(newUi);
        _uiLangSyncAnchor = newUi;

        if (TranslateTargetBox?.SelectedValue is string cur
            && string.Equals(TranslateTargets.Normalize(cur), prevDefault, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(prevDefault, newDefault, StringComparison.OrdinalIgnoreCase))
        {
            _uiLangBoxSync = true;
            try { SelectComboValue(TranslateTargetBox, newDefault, newDefault); }
            finally { _uiLangBoxSync = false; }
        }
    }

    private void TranslateTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        RebuildSubModeOptions();
        RefreshDependencyGates();
    }

    private void RebuildSubModeOptions()
    {
        var toEn = string.Equals(
            TranslateTargetBox.SelectedValue as string ?? TranslateTargets.Normalize(_settings.TranslateTarget),
            TranslateTargets.En,
            StringComparison.OrdinalIgnoreCase);
        var current = SubModeBox.SelectedValue as string;
        SubModeBox.ItemsSource = new[]
        {
            new LabeledOption(toEn ? Loc.Get("Settings.SubMode.En") : Loc.Get("Settings.SubMode.Zh"), "zh"),
            new LabeledOption(Loc.Get("Settings.SubMode.Src"), "src"),
            new LabeledOption(toEn ? Loc.Get("Settings.SubMode.DualEn") : Loc.Get("Settings.SubMode.Dual"), "dual"),
            new LabeledOption(Loc.Get("Settings.SubMode.Off"), "off"),
        };
        if (!string.IsNullOrWhiteSpace(current))
            SelectComboValue(SubModeBox, current, "zh");
    }

    private void SubModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSubUi || SubModeBox.SelectedValue is not string mode)
            return;
        ApplySubModeToUi(mode);
        RefreshDependencyGates();
    }

    private void SubVisibleBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSubUi)
            return;
        if (SubVisibleBox.IsChecked == false)
        {
            ApplySubModeToUi("off");
            RefreshDependencyGates();
            return;
        }

        var current = SubModeBox.SelectedValue as string;
        ApplySubModeToUi(
            string.Equals(current, "off", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(current)
                ? _lastContentSubMode
                : current);
        RefreshDependencyGates();
    }

    private void ApplySubModeToUi(string mode)
    {
        _syncingSubUi = true;
        try
        {
            if (!string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
                _lastContentSubMode = mode;
            if (!Equals(SubModeBox.SelectedValue as string, mode))
                SubModeBox.SelectedValue = mode;
            if (SubModeBox.SelectedValue is null)
                SubModeBox.SelectedValue = string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) ? "off" : "zh";
            SubVisibleBox.IsChecked = !string.Equals(SubModeBox.SelectedValue as string, "off", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _syncingSubUi = false;
        }
    }

    private static void SelectComboValue(ComboBox box, string value, string fallback)
    {
        box.SelectedValue = value;
        if (box.SelectedValue is null)
            box.SelectedValue = fallback;
    }

    private static double ParseDelay(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, -30, 30)
            : 0;
    }

    private static T PickClosest<T>(IEnumerable<T> items, T target) where T : struct, IComparable<T>
    {
        T? best = null;
        foreach (var item in items)
        {
            if (item.Equals(target)) return item;
            if (best is null || Math.Abs(CompareDelta(item, target)) < Math.Abs(CompareDelta(best.Value, target)))
                best = item;
        }

        return best ?? target;

        static double CompareDelta(T a, T b)
            => Convert.ToDouble(a, CultureInfo.InvariantCulture) - Convert.ToDouble(b, CultureInfo.InvariantCulture);
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

    private static string NormalizeHwDec(string? raw)
    {
        var key = (raw ?? "auto").Trim().ToLowerInvariant();
        return key is "no" or "off" ? "no" : key;
    }

    private static string NormalizeVideoFit(string? raw)
    {
        var key = (raw ?? "window").Trim().ToLowerInvariant();
        return key is "window" or "contain" or "cover" or "stretch" ? key : "window";
    }

    private void BrowseScreenshot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.Get("Settings.Browse.ScreenshotDir") };
        if (dlg.ShowDialog(this) == true)
            ScreenshotDirBox.Text = dlg.FolderName;
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

    private void RunWizard_Click(object sender, RoutedEventArgs e)
    {
        SetupWizardWindow.Show(this, _settings);
        LoadFromSettings();
        RefreshStatus();
    }

    private void BrowseGlossary_Click(object sender, RoutedEventArgs e)
        => PickFile(GlossaryPathBox, Loc.Get("Settings.Browse.GlossaryFilter"));

    private void PickFile(System.Windows.Controls.TextBox box, string filter)
    {
        var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog(this) == true)
            box.Text = dlg.FileName;
    }

    private void DownloadAsr_Click(object sender, RoutedEventArgs e)
    {
        SnapshotModelsFields();
        RunAsrDownload([ModelPicker.InstallTarget(_settings.AsrModel)]);
    }

    private void EnsureGpu_Click(object sender, RoutedEventArgs e)
    {
        SetPackStatus(Loc.Get("Settings.Engine.EmbeddedGpuHint"));
    }

    private void RunAsrDownload(IReadOnlyList<string> modelIds)
    {
        SnapshotEngineFields();
        SnapshotModelsFields();
        RunBusyDownload(
            Loc.Get("DownloadProgress.AsrHeading"),
            async (status, ct) =>
            {
                status(Loc.Get("Settings.ModelDownload.PreparingAsr"));
                _packAsr = new AsrPipeline(_settings, status, _ => { });
                await _packAsr.EnsureReadyAsync(ct).ConfigureAwait(true);
                await _packAsr.DownloadModelsAsync(modelIds, ct).ConfigureAwait(true);
            },
            onSuccess: () =>
            {
                NotifyModelsChanged();
                RefreshStatus();
            });
    }

    private void InstallLlama_Click(object sender, RoutedEventArgs e)
    {
        RunBusyDownload(
            Loc.Get("DownloadProgress.LlamaHeading"),
            async (status, ct) =>
            {
                await ManagedLlmInstaller.EnsureLlamaRuntimeAsync(_settings, status, _ => { }, ct)
                    .ConfigureAwait(true);
            },
            onSuccess: () =>
            {
                RefreshStatus();
                SetPackStatus(Loc.Get("Settings.PackStatus.LlamaReady"));
            });
    }

    private void DownloadGguf_Click(object sender, RoutedEventArgs e)
    {
        SnapshotEngineFields();
        SnapshotModelsFields();
        RunBusyDownload(
            Loc.Get("DownloadProgress.GgufHeading"),
            async (status, ct) =>
            {
                if (!ManagedLlmInstaller.HasLlamaRuntime(_settings))
                {
                    await ManagedLlmInstaller.EnsureLlamaRuntimeAsync(_settings, status, _ => { }, ct)
                        .ConfigureAwait(true);
                }

                await ManagedLlmInstaller.EnsureGgufAsync(
                        _settings.HfEndpoint,
                        status,
                        _ => { },
                        ct,
                        _settings.TranslateModelId,
                        _settings)
                    .ConfigureAwait(true);
            },
            onSuccess: () =>
            {
                NotifyModelsChanged();
                RefreshStatus();
                SetPackStatus(Loc.Get("Settings.Models.TranslateDone"));
            });
    }

    private void OpenAdvancedLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ManagedLlmInstaller.OpenModelsFolder(_settings);
            SetPackStatus(Loc.Format("Settings.ManualInstall.OpenedAsrRoot", AppPaths.ResolveAdvancedLlmModelsDir(_settings)));
        }
        catch (Exception ex)
        {
            SetPackStatus(ex.Message);
        }
    }

    private void InstallCurrentPreset_Click(object sender, RoutedEventArgs e)
        => InstallRuntimeDepsAsync(SetPackStatus);

    private void CancelPack_Click(object sender, RoutedEventArgs e) => _packCts?.Cancel();

    /// <summary>Drop cached model roots / live pack probes so Main re-reads disk after Settings installs.</summary>
    private static void NotifyModelsChanged()
    {
        EngineLocator.Invalidate();
        PresetReadiness.ClearLivePacks();
    }

    private void DisposePackAsr()
    {
        try { _packAsr?.Detach(); } catch { /* ignore */ }
        _packAsr = null;
    }

    private void SettingsWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy || ModelDownloadActivity.IsActive)
        {
            var confirm = MessageBox.Show(
                this,
                Loc.Get("Main.Close.WhileDownloading.Message"),
                Loc.Get("Main.Close.WhileDownloading.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _packCts?.Cancel();
        DisposePackAsr();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SnapshotAll();
        PresetReadiness.InvalidateDiskProbe();
        EngineLocator.Invalidate();
        _owner.CopyFrom(_settings);
        Loc.Apply(_owner.UiLanguage);
        _owner.Save();
        DialogResult = true;
        Close();
    }

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => WindowChromeUtil.DragOrToggle(this, e, allowMaximize: false);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
