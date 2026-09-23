using System.IO;
using System.Windows;
using System.Windows.Controls;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private bool _subSourceBoxSync;
    private bool _localSubBoxSync;
    private string? _subSourceUiSig;
    private string? _localSubUiSig;

    private void InitSubSourceBox()
    {
        SubSourceBox.SelectedValuePath = nameof(SubtitleSourceEntry.Id);
        LocalSubBox.SelectedValuePath = nameof(LocalSidecarEntry.Id);
        _subSourceUiSig = null;
        _localSubUiSig = null;
        RefreshSubSourceBox();
        RefreshLocalSubSwitchUi();
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
            RefreshLocalSubSwitchUi();
            RefreshModeButtons();
        }
    }

    private async void LocalSubBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_localSubBoxSync || _preview is null || !HasMedia) return;
        if (LocalSubBox.SelectedValue is not string path || string.IsNullOrWhiteSpace(path)) return;
        if (IsActiveLocalSidecar(path)) return;

        try
        {
            await _preview.SelectLocalSidecarAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            PlayerLog.Write("本地字幕切换：" + ex.Message);
        }
        finally
        {
            RefreshSubSourceBox();
            RefreshLocalSubSwitchUi();
            RefreshModeButtons();
        }
    }

    private async void LocalSidecarMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null || !HasMedia) return;
        if (sender is not MenuItem { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
        if (IsActiveLocalSidecar(path)) return;

        try
        {
            await _preview.SelectLocalSidecarAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            PlayerLog.Write("本地字幕切换：" + ex.Message);
        }
        finally
        {
            RefreshSubSourceBox();
            RefreshLocalSubSwitchUi();
            RefreshModeButtons();
        }
    }

    private void RefreshSubSourceBox()
    {
        if (SubSourceBox is null) return;

        var selected = _preview?.ActiveSource ?? SubtitleSourceKind.Off;
        var hasLocal = _preview?.HasLocalSubtitle == true;
        var show = HasMedia && _preview?.IsStreamPlayback != true;
        var sig = $"{show}|{selected}|{hasLocal}";
        if (string.Equals(sig, _subSourceUiSig, StringComparison.Ordinal))
            return;
        _subSourceUiSig = sig;

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

    private void RefreshLocalSubSwitchUi()
    {
        if (_preview is null || !HasMedia || _preview.IsStreamPlayback)
        {
            if (_localSubUiSig is not null)
            {
                _localSubUiSig = null;
                RefreshLocalSubBox();
                RefreshLocalSubMenu();
            }
            return;
        }

        var entries = BuildLocalSidecarEntries();
        var active = _preview.UsingExistingSub == true ? _preview.ActiveSubPath : null;
        var sig = entries.Count <= 1
            ? $"hide|{_preview.MediaPath}"
            : $"show|{_preview.MediaPath}|{active}|{string.Join('\u001f', entries.Select(e => e.Id))}";
        if (string.Equals(sig, _localSubUiSig, StringComparison.Ordinal))
            return;
        _localSubUiSig = sig;

        RefreshLocalSubBox(entries);
        RefreshLocalSubMenu(entries);
    }

    private void RefreshLocalSubBox(List<LocalSidecarEntry>? prebuilt = null)
    {
        if (LocalSubBox is null) return;

        var entries = prebuilt ?? BuildLocalSidecarEntries();
        var show = HasMedia
                   && _preview?.IsStreamPlayback != true
                   && entries.Count > 1;

        _localSubBoxSync = true;
        try
        {
            if (!show)
            {
                LocalSubBox.ItemsSource = null;
                LocalSubBox.SelectedIndex = -1;
            }
            else
            {
                LocalSubBox.ItemsSource = entries;
                var selected = ResolveSelectedLocalSidecarId(entries);
                LocalSubBox.SelectedValue = selected;
            }
        }
        finally
        {
            _localSubBoxSync = false;
        }

        LocalSubBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LocalSubBox.IsEnabled = show;
        LocalSubBox.ToolTip = show ? Loc.Get("Main.LocalSub.Tip") : null;
    }

    private void RefreshLocalSubMenu(List<LocalSidecarEntry>? prebuilt = null)
    {
        if (LocalSubMenu is null) return;

        var entries = prebuilt ?? BuildLocalSidecarEntries();
        var show = HasMedia
                   && _preview?.IsStreamPlayback != true
                   && entries.Count > 1;

        LocalSubMenu.Items.Clear();
        if (!show)
        {
            LocalSubMenu.Visibility = Visibility.Collapsed;
            LocalSubMenu.IsEnabled = false;
            return;
        }

        var active = _preview?.UsingExistingSub == true ? _preview.ActiveSubPath : null;
        foreach (var entry in entries)
        {
            var item = new MenuItem
            {
                Header = entry.Name,
                Tag = entry.Path,
                IsCheckable = true,
                IsChecked = IsSamePath(active, entry.Path),
                ToolTip = entry.Path,
            };
            item.Click += LocalSidecarMenu_Click;
            LocalSubMenu.Items.Add(item);
        }

        LocalSubMenu.Visibility = Visibility.Visible;
        LocalSubMenu.IsEnabled = true;
    }

    private List<LocalSidecarEntry> BuildLocalSidecarEntries()
    {
        if (_preview is null || string.IsNullOrWhiteSpace(_preview.MediaPath))
            return [];

        return _preview.LocalSidecars
            .Select(s => new LocalSidecarEntry(s.Path, FormatLocalSidecarLabel(s), s.Tag))
            .ToList();
    }

    private static string FormatLocalSidecarLabel(SidecarInfo info)
    {
        var ext = string.IsNullOrWhiteSpace(info.Extension)
            ? ""
            : info.Extension.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(info.Tag))
            return string.IsNullOrWhiteSpace(ext)
                ? Loc.Get("Main.LocalSub.Default")
                : Loc.Get("Main.LocalSub.Default") + " · " + ext;
        return string.IsNullOrWhiteSpace(ext) ? info.Tag : info.Tag + " · " + ext;
    }

    private string? ResolveSelectedLocalSidecarId(IReadOnlyList<LocalSidecarEntry> entries)
    {
        if (entries.Count == 0) return null;
        // Only highlight when that sidecar is actually loaded — otherwise leave blank so
        // choosing the preferred file still fires SelectionChanged from Live/Online/Off.
        if (_preview?.UsingExistingSub != true)
            return null;

        var active = _preview.ActiveSubPath;
        foreach (var e in entries)
        {
            if (IsSamePath(active, e.Path))
                return e.Id;
        }

        return null;
    }

    private bool IsActiveLocalSidecar(string path)
        => IsSamePath(_preview?.ActiveSubPath, path) && _preview?.UsingExistingSub == true;

    private static bool IsSamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
