using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private void WireMpvHostInput()
    {
        PlayerHost.NativeRightButtonUp += OpenPlayerContextMenu;
        PlayerHost.NativeLeftButtonDown += clickCount =>
        {
            if (!HasMedia) return;
            if (clickCount >= 2)
            {
                _clickTimer.Stop();
                ToggleFullscreen();
                return;
            }

            _clickTimer.Stop();
            _clickTimer.Start();
        };
        PlayerHost.NativeMouseWheel += delta =>
        {
            if (!HasMedia) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                _preview!.SeekRelative(delta > 0 ? 2 : -2);
            else
            {
                _preview!.AdjustVolume(delta > 0 ? 5 : -5);
                SyncVolumeUi();
            }

            BumpChrome();
        };
        PlayerHost.NativeMouseMove += () =>
        {
            if (_isFullscreen)
                BumpChrome();
        };
    }

    private void OpenPlayerContextMenu()
    {
        var menu = ContextMenu;
        if (menu is null) return;
        // Close first so a second right-click repositions correctly.
        menu.IsOpen = false;
        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = VideoArea;
        menu.IsOpen = true;
        BumpChrome();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox)
            return;

        var playbackArrow = IsPlaybackArrowKey(e.Key);
        if (playbackArrow && ControlBarComboStealsFocus(e))
            CloseControlBarDropdowns();

        if (!playbackArrow && e.OriginalSource is ComboBox or ComboBoxItem)
            return;

        if (!HasMedia)
        {
            switch (e.Key)
            {
                case Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        OpenUrl_Click(sender, e);
                    else
                        Open_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.L:
                    TogglePlaylist_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.N:
                    PlaylistNext_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.P:
                    PlaylistPrev_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.F1:
                    ShortcutsHelp_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.T:
                    AlwaysOnTopMenu.IsChecked = !AlwaysOnTopMenu.IsChecked;
                    AlwaysOnTop_Click(sender, e);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    if (_isFullscreen) ToggleFullscreen();
                    else if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (_preview is null && e.Key is not (Key.O or Key.Escape or Key.F11 or Key.F or Key.Enter)) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.Space:
                _preview?.TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                _preview?.SeekRelative(ctrl ? -SeekLargeStep() : shift ? -SeekFineStep() : -SeekStep());
                NoteSeekWhileLagging();
                e.Handled = true;
                break;
            case Key.Right:
                _preview?.SeekRelative(ctrl ? SeekLargeStep() : shift ? SeekFineStep() : SeekStep());
                NoteSeekWhileLagging();
                e.Handled = true;
                break;
            case Key.Up:
                _preview?.AdjustVolume(5);
                SyncVolumeUi();
                e.Handled = true;
                break;
            case Key.Down:
                _preview?.AdjustVolume(-5);
                SyncVolumeUi();
                e.Handled = true;
                break;
            case Key.PageUp:
                _preview?.SeekRelative(60);
                e.Handled = true;
                break;
            case Key.PageDown:
                _preview?.SeekRelative(-60);
                e.Handled = true;
                break;
            case Key.Home:
                _preview?.Seek(0);
                e.Handled = true;
                break;
            case Key.End when _preview is { Duration: > 0 }:
                _preview.Seek(Math.Max(0, _preview.Duration - 1));
                e.Handled = true;
                break;
            case Key.OemPeriod:
            case Key.Decimal:
                _preview?.FrameStep(true);
                e.Handled = true;
                break;
            case Key.OemComma:
                _preview?.FrameStep(false);
                e.Handled = true;
                break;
            case Key.M:
                _preview?.ToggleMute();
                e.Handled = true;
                break;
            case Key.V:
                _preview?.ToggleSubVisible();
                RefreshModeButtons();
                e.Handled = true;
                break;
            case Key.S when !ctrl:
                Screenshot_Click(sender, e);
                e.Handled = true;
                break;
            case Key.OemOpenBrackets:
                _preview?.CycleSpeed();
                RefreshSpeedButton();
                e.Handled = true;
                break;
            case Key.OemCloseBrackets:
                _preview?.ResetSpeed();
                RefreshSpeedButton();
                e.Handled = true;
                break;
            case Key.F1:
                ShortcutsHelp_Click(sender, e);
                e.Handled = true;
                break;
            case Key.T:
                AlwaysOnTopMenu.IsChecked = !AlwaysOnTopMenu.IsChecked;
                AlwaysOnTop_Click(sender, e);
                e.Handled = true;
                break;
            case Key.O when ctrl:
                if (shift)
                    OpenUrl_Click(sender, e);
                else
                    Open_Click(sender, e);
                e.Handled = true;
                break;
            case Key.D1:
                SetMode(SubtitleDisplayMode.Zh);
                e.Handled = true;
                break;
            case Key.D2:
                SetMode(SubtitleDisplayMode.Source);
                e.Handled = true;
                break;
            case Key.D3:
                SetMode(SubtitleDisplayMode.Dual);
                e.Handled = true;
                break;
            case Key.D0:
                _preview?.ToggleSubVisible();
                RefreshModeButtons();
                e.Handled = true;
                break;
            case Key.L:
                TogglePlaylist_Click(sender, e);
                e.Handled = true;
                break;
            case Key.N:
                PlaylistNext_Click(sender, e);
                e.Handled = true;
                break;
            case Key.P:
                PlaylistPrev_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Z:
                _preview?.NudgeSubDelay(0.5);
                e.Handled = true;
                break;
            case Key.X:
                _preview?.NudgeSubDelay(-0.5);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_isFullscreen) ToggleFullscreen();
                else if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
        }
    }

    private void Video_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasMedia) return;
        if (e.ClickCount >= 2)
        {
            _clickTimer.Stop();
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        _clickTimer.Stop();
        _clickTimer.Start();
        e.Handled = true;
    }

    private void Video_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasMedia) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            _preview!.SeekRelative(e.Delta > 0 ? 2 : -2);
        else
        {
            _preview!.AdjustVolume(e.Delta > 0 ? 5 : -5);
            SyncVolumeUi();
        }

        e.Handled = true;
        BumpChrome();
    }

    private static bool IsPlaybackArrowKey(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down;

    /// <summary>底栏 ComboBox 展开或焦点在其内部时，方向键用于播放而非菜单导航。</summary>
    private bool ControlBarComboStealsFocus(KeyEventArgs e)
    {
        if (SubSourceBox is { IsDropDownOpen: true } || PresetBox is { IsDropDownOpen: true })
            return true;

        if (e.OriginalSource is ComboBox or ComboBoxItem)
            return true;

        if (e.OriginalSource is not DependencyObject src)
            return false;

        return IsDescendantOf(SubSourceBox, src) || IsDescendantOf(PresetBox, src);
    }

    private void CloseControlBarDropdowns()
    {
        if (SubSourceBox is { IsDropDownOpen: true })
            SubSourceBox.IsDropDownOpen = false;
        if (PresetBox is { IsDropDownOpen: true })
            PresetBox.IsDropDownOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject? root, DependencyObject node)
    {
        if (root is null) return false;
        for (var p = node; p is not null; p = VisualTreeHelper.GetParent(p))
            if (ReferenceEquals(p, root))
                return true;
        return false;
    }
}
