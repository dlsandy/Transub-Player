using System.Windows;
using System.Windows.Controls;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private bool _langBoxSync;

    private void InitPresetBox()
    {
        // ItemTemplate 已绑定 Name/FullName，不能再设 DisplayMemberPath
        SourceLangBox.SelectedValuePath = nameof(LabeledLang.Id);
        TranslateTargetBarBox.SelectedValuePath = nameof(LabeledLang.Id);
        RefreshPresetUi(probeDeps: false);
    }

    private sealed record LabeledLang(string Id, string Name, string FullName)
    {
        public override string ToString() => Name;
    }

    private void SourceLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_presetReady || _langBoxSync) return;
        if (SourceLangBox.SelectedValue is not string id) return;
        if (string.Equals(id, SourceLanguages.Normalize(_settings.SourceLanguage), StringComparison.OrdinalIgnoreCase))
            return;
        _ = SelectSourceLanguageAsync(id);
    }
    private void TranslateTargetBarBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_presetReady || _langBoxSync) return;
        if (TranslateTargetBarBox.SelectedValue is not string id) return;
        if (string.Equals(id, TranslateTargets.Normalize(_settings.TranslateTarget), StringComparison.OrdinalIgnoreCase))
            return;
        _ = SelectTranslateTargetAsync(id);
    }

    private void RefreshPresetUi(bool probeDeps = true)
    {
        RefreshPresetMenu(probeDeps);
        RefreshModelMenus();
        RefreshLangBoxes();
        UpdatePresetHint();
    }

    private void RefreshLangBoxes()
    {
        if (!_presetReady) return;

        _langBoxSync = true;
        try
        {
            SourceLangBox.ItemsSource = SourceLanguages.All
                .Select(id => new LabeledLang(id, ResolveSourceLangShort(id), SourceLanguages.DisplayName(id)))
                .ToList();
            SourceLangBox.SelectedValue = SourceLanguages.Normalize(_settings.SourceLanguage);

            TranslateTargetBarBox.ItemsSource = TranslateTargets.All
                .Select(id => new LabeledLang(id, TranslateTargetShort(id), TranslateTargetFull(id)))
                .ToList();
            TranslateTargetBarBox.SelectedValue = TranslateTargets.Normalize(_settings.TranslateTarget);
        }
        finally
        {
            _langBoxSync = false;
        }

        var enabled = _presetReady;
        var onPreview = _preview?.ShowPreviewChrome == true;
        SourceLangBox.IsEnabled = enabled && onPreview;
        TranslateTargetBarBox.IsEnabled = enabled && onPreview;
        if (PresetBoxBorder is not null)
        {
            if (!onPreview && _preview is not null && HasMedia)
            {
                PresetBoxBorder.ToolTip = _preview.IsLocalSubtitleSource
                    ? Loc.Get("Main.Preset.Disabled.Local")
                    : _preview.UsingExistingSub
                        ? Loc.Get("Main.Preset.Disabled.External")
                        : _preview.IsStreamPlayback
                            ? Loc.Get("Main.Preset.Disabled.Stream")
                            : Loc.Get("Main.Preset.Tip");
            }
        }
        UpdatePresetHint();
    }

    private string ResolveSourceLangShort(string id)
    {
        var norm = SourceLanguages.Normalize(id);
        if (!SourceLanguages.IsAuto(norm))
            return SourceLangShort(norm);

        if (_preview is not null && HasMedia)
        {
            if (_preview.MatchedScene is { } matched && !SourceLanguages.IsAuto(matched.Language))
                return Loc.Format("SourceLang.Short.AutoResolved", SourceLangShort(matched.Language));
            if (!string.IsNullOrWhiteSpace(_preview.SensedSourceLanguage))
                return Loc.Format("SourceLang.Short.AutoResolved", SourceLangShort(_preview.SensedSourceLanguage));
        }

        return Loc.Get("SourceLang.Short.Auto");
    }

    private static string SourceLangShort(string id) => SourceLanguages.Normalize(id) switch
    {
        SourceLanguages.Ja => Loc.Get("SourceLang.Short.Ja"),
        SourceLanguages.Ko => Loc.Get("SourceLang.Short.Ko"),
        SourceLanguages.En => Loc.Get("SourceLang.Short.En"),
        SourceLanguages.Zh => Loc.Get("SourceLang.Short.Zh"),
        _ => Loc.Get("SourceLang.Short.Auto"),
    };

    private static string TranslateTargetShort(string id) => TranslateTargets.Normalize(id) switch
    {
        TranslateTargets.En => Loc.Get("Settings.TranslateTarget.Short.En"),
        TranslateTargets.Ja => Loc.Get("Settings.TranslateTarget.Short.Ja"),
        TranslateTargets.Ko => Loc.Get("Settings.TranslateTarget.Short.Ko"),
        TranslateTargets.ZhHant => Loc.Get("Settings.TranslateTarget.Short.ZhHant"),
        _ => Loc.Get("Settings.TranslateTarget.Short.Zh"),
    };

    private static string TranslateTargetFull(string id) => TranslateTargets.Normalize(id) switch
    {
        TranslateTargets.En => Loc.Get("Settings.TranslateTarget.En"),
        TranslateTargets.Ja => Loc.Get("Settings.TranslateTarget.Ja"),
        TranslateTargets.Ko => Loc.Get("Settings.TranslateTarget.Ko"),
        TranslateTargets.ZhHant => Loc.Get("Settings.TranslateTarget.ZhHant"),
        _ => Loc.Get("Settings.TranslateTarget.Zh"),
    };

    private void UpdatePresetHint()
    {
        if (SourceLangBox is null) return;

        var srcFull = SourceLanguages.DisplayName(_settings.SourceLanguage);
        var tgtFull = TranslateTargetFull(_settings.TranslateTarget);
        if (TranslateTargetBarBox is not null)
            TranslateTargetBarBox.ToolTip = tgtFull;

        var baseTip = Loc.Get("Main.Preset.Tip");
        if (_preview is null || !HasMedia)
        {
            SourceLangBox.ToolTip = srcFull;
            if (PresetBoxBorder is not null)
                PresetBoxBorder.ToolTip = baseTip;
            return;
        }

        if (SourceLanguages.IsAuto(_settings.SourceLanguage) && _preview.MatchedScene is { } matched
            && !SourceLanguages.IsAuto(matched.Language))
        {
            SourceLangBox.ToolTip = Loc.Format(
                "Main.Preset.Tip.Matched",
                SourceLanguages.DisplayName(matched.Language));
            if (PresetBoxBorder is not null)
                PresetBoxBorder.ToolTip = baseTip;
            return;
        }

        if (SourceLanguages.IsAuto(_settings.SourceLanguage)
            && !string.IsNullOrWhiteSpace(_preview.SensedSourceLanguage))
        {
            SourceLangBox.ToolTip = Loc.Format(
                "Main.Preset.Tip.Sensed",
                SourceLanguages.DisplayName(_preview.SensedSourceLanguage));
            if (PresetBoxBorder is not null)
                PresetBoxBorder.ToolTip = baseTip;
            return;
        }

        if (!SourceLanguages.IsAuto(_settings.SourceLanguage))
        {
            SourceLangBox.ToolTip = Loc.Get("Main.Preset.Tip.Manual");
            if (PresetBoxBorder is not null)
                PresetBoxBorder.ToolTip = baseTip;
            return;
        }

        SourceLangBox.ToolTip = srcFull;
        if (PresetBoxBorder is not null)
            PresetBoxBorder.ToolTip = baseTip;
    }

    private void RevertSourceLangBoxSelection()
    {
        if (!_presetReady) return;
        _langBoxSync = true;
        try
        {
            SourceLangBox.SelectedValue = SourceLanguages.Normalize(_settings.SourceLanguage);
        }
        finally
        {
            _langBoxSync = false;
        }
    }

    private void RevertTranslateTargetBoxSelection(string prev)
    {
        if (!_presetReady || TranslateTargetBarBox is null) return;
        _langBoxSync = true;
        try
        {
            TranslateTargetBarBox.SelectedValue = TranslateTargets.Normalize(prev);
        }
        finally
        {
            _langBoxSync = false;
        }
    }
}
