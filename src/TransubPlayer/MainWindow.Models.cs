using System.Windows;
using System.Windows.Controls;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private void RefreshModelMenus()
    {
        RefreshAsrModelMenu();
        RefreshTranslateModelMenu();
    }

    private void RefreshAsrModelMenu()
    {
        if (AsrModelMenu is null) return;

        AsrModelMenu.Items.Clear();
        if (!_presetReady)
        {
            AsrModelMenu.IsEnabled = false;
            AsrModelMenu.Header = Loc.Get("Main.Menu.AsrModel");
            AsrModelMenu.Items.Add(new MenuItem { Header = Loc.Get("Main.Menu.PresetsLoading"), IsEnabled = false });
            return;
        }

        var current = ModelPicker.Normalize(_settings.AsrModel);
        AsrModelMenu.IsEnabled = true;
        AsrModelMenu.Header = Loc.Format("Main.Menu.AsrModelCurrent", AsrModelLabel(current));
        foreach (var id in ModelPicker.Selectable)
        {
            var item = new MenuItem
            {
                Header = AsrModelLabel(id),
                IsCheckable = true,
                IsChecked = string.Equals(id, current, StringComparison.OrdinalIgnoreCase),
                Tag = id,
                ToolTip = AsrModelHint(id),
            };
            var captured = id;
            item.Click += (_, _) => _ = SelectAsrModelAsync(captured);
            AsrModelMenu.Items.Add(item);
        }
    }

    private void RefreshTranslateModelMenu()
    {
        if (TranslateModelMenu is null) return;

        TranslateModelMenu.Items.Clear();
        if (!_presetReady)
        {
            TranslateModelMenu.IsEnabled = false;
            TranslateModelMenu.Header = Loc.Get("Main.Menu.TranslateModel");
            TranslateModelMenu.Items.Add(new MenuItem { Header = Loc.Get("Main.Menu.PresetsLoading"), IsEnabled = false });
            return;
        }

        var current = TranslateModels.Normalize(_settings.TranslateModelId);
        TranslateModelMenu.IsEnabled = true;
        TranslateModelMenu.Header = Loc.Format("Main.Menu.TranslateModelCurrent", TranslateModelLabel(current));
        foreach (var id in TranslateModels.Selectable)
        {
            var item = new MenuItem
            {
                Header = TranslateModelLabel(id),
                IsCheckable = true,
                IsChecked = string.Equals(id, current, StringComparison.OrdinalIgnoreCase),
                Tag = id,
                ToolTip = TranslateModelHint(id),
            };
            var captured = id;
            item.Click += (_, _) => _ = SelectTranslateModelAsync(captured);
            TranslateModelMenu.Items.Add(item);
        }
    }

    private async Task SelectAsrModelAsync(string modelId)
    {
        if (!_presetReady || _preview is null) return;
        var normalized = ModelPicker.Normalize(modelId);
        if (string.Equals(ModelPicker.Normalize(_settings.AsrModel), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _settings.AsrModel = normalized;
        _settings.Save();
        RefreshModelMenus();

        try
        {
            await _preview.ApplyAsrModelChangeAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(RefreshChrome);
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
    }

    private async Task SelectTranslateModelAsync(string modelId)
    {
        if (!_presetReady || _preview is null) return;
        var normalized = TranslateModels.Normalize(modelId);
        if (string.Equals(TranslateModels.Normalize(_settings.TranslateModelId), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        _settings.TranslateModelId = normalized;
        _settings.Save();
        RefreshModelMenus();

        try
        {
            await _preview.ApplyTranslateModelChangeAsync(CancellationToken.None).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(RefreshChrome);
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
    }

    private static string AsrModelLabel(string id) => ModelPicker.Normalize(id) switch
    {
        ModelPicker.Auto => Loc.Get("AsrModel.Auto"),
        _ => Loc.Get("AsrModel.Turbo"),
    };

    private static string AsrModelHint(string id) => ModelPicker.Normalize(id) switch
    {
        ModelPicker.Auto => Loc.Get("AsrModel.Auto.Hint"),
        _ => Loc.Get("AsrModel.Turbo.Hint"),
    };

    private static string TranslateModelLabel(string id) => Loc.Get("TranslateModel.TranslateGemma");

    private static string TranslateModelHint(string id) => Loc.Get("TranslateModel.TranslateGemma.Hint");
}
