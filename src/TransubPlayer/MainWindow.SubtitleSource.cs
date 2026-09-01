using System.Windows;
using System.Windows.Controls;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private bool _subSourceBoxSync;

    private void InitSubSourceBox()
    {
        SubSourceBox.SelectedValuePath = nameof(SubtitleSourceEntry.Id);
        RefreshSubSourceBox();
    }

    private async void SubSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_subSourceBoxSync || _preview is null || !HasMedia) return;
        if (SubSourceBox.SelectedValue is not string id) return;
        if (!Enum.TryParse<SubtitleSourceKind>(id, ignoreCase: true, out var kind)) return;
        if (kind == _preview.ActiveSource) return;

        try
        {
            await _preview.SelectSubtitleSourceAsync(kind, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            PlayerLog.Write("字幕来源：" + ex.Message);
        }
        finally
        {
            RefreshSubSourceBox();
            RefreshModeButtons();
        }
    }

    private void RefreshSubSourceBox()
    {
        if (SubSourceBox is null) return;

        var selected = _preview?.ActiveSource ?? SubtitleSourceKind.Off;
        var hasLocal = _preview?.HasLocalSubtitle == true;
        var entries = new List<SubtitleSourceEntry>
        {
            new(SubtitleSourceKind.Off, Loc.Get("Main.SubSource.Off")),
            new(SubtitleSourceKind.Online, Loc.Get("Main.SubSource.Online")),
            new(SubtitleSourceKind.Local,
                hasLocal ? Loc.Get("Main.SubSource.Local") : Loc.Get("Main.SubSource.LocalNone")),
            new(SubtitleSourceKind.Live, Loc.Get("Main.SubSource.Live")),
        };

        _subSourceBoxSync = true;
        try
        {
            SubSourceBox.ItemsSource = entries;
            SubSourceBox.SelectedValue = selected.ToString();
        }
        finally
        {
            _subSourceBoxSync = false;
        }

        var show = HasMedia && _preview?.IsStreamPlayback != true;
        SubSourceBox.IsEnabled = show;
        SubSourceBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SubSourceBox.Tag = selected == SubtitleSourceKind.Off ? null : "on";
        SubSourceBox.ToolTip = selected switch
        {
            SubtitleSourceKind.Off => Loc.Get("Main.SubSource.Off") + " · " + Loc.Get("Main.SubSource.Tip"),
            SubtitleSourceKind.Online => Loc.Get("Main.SubSource.Online"),
            SubtitleSourceKind.Local => hasLocal
                ? Loc.Get("Main.SubSource.Local")
                : Loc.Get("Main.SubSource.LocalOnlyNone"),
            SubtitleSourceKind.Live => Loc.Get("Main.Status.LiveActive"),
            _ => Loc.Get("Main.SubSource.Tip"),
        };
    }
}
