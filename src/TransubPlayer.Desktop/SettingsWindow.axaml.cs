using Avalonia.Controls;
using Avalonia.Interactivity;
using TransubPlayer.Localization;
using TransubPlayer.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop;

internal partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        foreach (var id in AsrBackends.Selectable)
            AsrBackendBox.Items.Add(new ComboBoxItem { Content = AsrBackends.DisplayName(id), Tag = id });
        SelectByTag(AsrBackendBox, AsrBackends.Normalize(_settings.AsrBackend));

        foreach (var t in TranslateTargets.All)
            TranslateTargetBox.Items.Add(new ComboBoxItem { Content = TranslateLabel(t), Tag = t });
        SelectByTag(TranslateTargetBox, TranslateTargets.Normalize(_settings.TranslateTarget));

        TranslateEnabledBox.IsChecked = _settings.TranslateEnabled;

        UiLanguageBox.Items.Add(new ComboBoxItem { Content = Loc.Get("Common.AutoFollowSystem"), Tag = UiLanguages.Auto });
        foreach (var lang in UiLanguages.Catalog)
            UiLanguageBox.Items.Add(new ComboBoxItem { Content = lang.NativeName, Tag = lang.Tag });
        SelectByTag(UiLanguageBox, string.IsNullOrWhiteSpace(_settings.UiLanguage) ? UiLanguages.Auto : _settings.UiLanguage);

        SelectHwDec(_settings.HwDec);
        RefreshModelStatus();
    }

    private async void OnDownloadAsrClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            PackStatus.Text = "正在下载 Whisper turbo…";
            var root = AsrModelStore.ResolveModelsRoot(_settings);
            await AsrModelStore.EnsureTurboAsync(
                root,
                _settings.HfEndpoint,
                status => PackStatus.Text = status,
                log => PlayerLog.Write(log),
                CancellationToken.None);
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            PackStatus.Text = UserFacingErrors.Message(ex);
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _settings.AsrBackend = TagOr(_settings.AsrBackend, AsrBackendBox);
        _settings.TranslateTarget = TagOr(_settings.TranslateTarget, TranslateTargetBox);
        _settings.TranslateEnabled = TranslateEnabledBox.IsChecked == true;
        _settings.UiLanguage = TagOr(_settings.UiLanguage, UiLanguageBox);
        _settings.HwDec = (HwDecBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "auto";
        _settings.Save();
        AsrRuntime.Apply(_settings);
        Loc.Apply(_settings.UiLanguage);
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnAssocClick(object? sender, RoutedEventArgs e)
    {
        var r = PlatformServices.Os?.ApplyPlaybackAssociations(_settings)
                ?? new FileAssociationApplyResult(0, 0, "unavailable");
        PackStatus.Text = string.IsNullOrWhiteSpace(r.LastError)
            ? $"关联完成：{r.Succeeded}"
            : r.LastError;
    }

    private async void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            PackStatus.Text = "正在检查更新…";
            var result = await AppUpdateService.CheckAsync(_settings, CancellationToken.None);
            PackStatus.Text = result.Kind switch
            {
                AppUpdateCheckKind.UpToDate => "已是最新",
                AppUpdateCheckKind.Available => $"发现更新：{result.Release?.TagName}",
                _ => result.ErrorMessage ?? result.TriedSources ?? result.Kind.ToString(),
            };
        }
        catch (Exception ex)
        {
            PackStatus.Text = UserFacingErrors.Message(ex);
        }
    }

    private void RefreshModelStatus()
    {
        var root = AsrModelStore.ResolveModelsRoot(_settings);
        var path = AsrModelStore.TurboPath(root);
        AsrModelStatus.Text = AsrModelStore.IsTurboInstalled(root)
            ? $"模型已就绪：{path}"
            : "尚未下载 whisper turbo 模型（可点上方按钮下载）";
    }

    private static string TranslateLabel(string id)
        => TranslateTargets.Normalize(id) switch
        {
            TranslateTargets.En => "English",
            TranslateTargets.Ja => "日本語",
            TranslateTargets.Ko => "한국어",
            TranslateTargets.ZhHant => "繁體中文",
            _ => "简体中文",
        };

    private void SelectHwDec(string? hw)
    {
        hw = string.IsNullOrWhiteSpace(hw) ? "auto" : hw;
        for (var i = 0; i < HwDecBox.Items.Count; i++)
        {
            if (HwDecBox.Items[i] is ComboBoxItem item
                && string.Equals(item.Content?.ToString(), hw, StringComparison.OrdinalIgnoreCase))
            {
                HwDecBox.SelectedIndex = i;
                return;
            }
        }

        HwDecBox.SelectedIndex = 0;
    }

    private static void SelectByTag(ComboBox box, string? tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private static string TagOr(string fallback, ComboBox box)
        => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
}
