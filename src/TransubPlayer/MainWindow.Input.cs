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
        if (PlayerContextMenu is not null)
            PlayerContextMenu.Opened += (_, _) =>
            {
                if (AppMenu is { IsOpen: true })
                    AppMenu.IsOpen = false;
                RefreshModelMenus();
            };
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

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                NotifySeekPastReady(_preview.Position);

            BumpChrome();
        };
        PlayerHost.NativeMouseMove += () =>
        {
            if (_isFullscreen)
                BumpChrome();
        };
        // Keys land on mpv HWND after clicking the video — forward so Space etc. still work.
        PlayerHost.NativeKeyDown += (key, isRepeat) =>
        {
            if (isRepeat && key == Key.Space)
                return;
            HandlePlaybackKey(key);
        };
    }

    private void OpenPlayerContextMenu()
    {
        var menu = PlayerContextMenu;
        if (menu is null) return;
        if (AppMenu is { IsOpen: true })
            AppMenu.IsOpen = false;
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

        // Space must always toggle play/pause on the main shell. ComboBox keeps
        // focus after a pick and would otherwise swallow Space (open/select);
        // focused Buttons can also activate on Space and cancel a TogglePause.
        if (e.Key == Key.Space)
        {
            if (ControlBarComboStealsFocus(e))
                CloseControlBarDropdowns();
            if (HandlePlaybackKey(Key.Space))
                e.Handled = true;
            return;
        }

        var playbackArrow = IsPlaybackArrowKey(e.Key);
        if (playbackArrow && ControlBarComboStealsFocus(e))
            CloseControlBarDropdowns();

        if (!playbackArrow && e.OriginalSource is ComboBox or ComboBoxItem)
            return;

        if (HandlePlaybackKey(e.Key))
            e.Handled = true;
    }

    /// <returns>True when the key was consumed as a player shortcut.</returns>
    private bool HandlePlaybackKey(Key key)
    {
        if (!HasMedia)
        {
            switch (key)
            {
                case Key.O when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        OpenUrl_Click(this, new RoutedEventArgs());
                    else
                        Open_Click(this, new RoutedEventArgs());
                    return true;
                case Key.L:
                    TogglePlaylist_Click(this, new RoutedEventArgs());
                    return true;
                case Key.N:
                    PlaylistNext_Click(this, new RoutedEventArgs());
                    return true;
                case Key.P:
                    PlaylistPrev_Click(this, new RoutedEventArgs());
                    return true;
                case Key.F1:
                    ShortcutsHelp_Click(this, new RoutedEventArgs());
                    return true;
                case Key.T:
                    AlwaysOnTopMenu.IsChecked = !AlwaysOnTopMenu.IsChecked;
                    AlwaysOnTop_Click(this, new RoutedEventArgs());
                    return true;
                case Key.Escape:
                    if (_isFullscreen) ToggleFullscreen();
                    else if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                    return true;
                default:
                    return false;
            }
        }

        if (_preview is null && key is not (Key.O or Key.Escape or Key.F11 or Key.F or Key.Enter))
            return false;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var stream = _preview?.IsStreamPlayback == true;

        switch (key)
        {
            case Key.Space:
                _preview?.TogglePause();
                return true;
            case Key.Left:
                if (stream) return true;
                _preview?.SeekRelative(ctrl ? -SeekLargeStep() : shift ? -SeekFineStep() : -SeekStep());
                NotifySeekPastReady(_preview?.Position ?? 0);
                return true;
            case Key.Right:
                if (stream) return true;
                _preview?.SeekRelative(ctrl ? SeekLargeStep() : shift ? SeekFineStep() : SeekStep());
                NotifySeekPastReady(_preview?.Position ?? 0);
                return true;
            case Key.Up:
                _preview?.AdjustVolume(5);
                SyncVolumeUi();
                return true;
            case Key.Down:
                _preview?.AdjustVolume(-5);
                SyncVolumeUi();
                return true;
            case Key.PageUp:
                if (stream) return true;
                _preview?.SeekRelative(60);
                NotifySeekPastReady(_preview?.Position ?? 0);
                return true;
            case Key.PageDown:
                if (stream) return true;
                _preview?.SeekRelative(-60);
                NotifySeekPastReady(_preview?.Position ?? 0);
                return true;
            case Key.Home:
                if (stream) return true;
                _preview?.Seek(0);
                return true;
            case Key.End when _preview is { Duration: > 0 }:
                if (stream) return true;
                _preview.Seek(Math.Max(0, _preview.Duration - 1));
                NotifySeekPastReady(_preview.Position);
                return true;
            case Key.OemPeriod:
            case Key.Decimal:
                _preview?.FrameStep(true);
                return true;
            case Key.OemComma:
                _preview?.FrameStep(false);
                return true;
            case Key.M:
                _preview?.ToggleMute();
                return true;
            case Key.V:
                _preview?.ToggleSubVisible();
                RefreshModeButtons();
                return true;
            case Key.S when !ctrl:
                Screenshot_Click(this, new RoutedEventArgs());
                return true;
            case Key.OemOpenBrackets:
                _preview?.CycleSpeed();
                RefreshSpeedButton();
                return true;
            case Key.OemCloseBrackets:
                _preview?.ResetSpeed();
                RefreshSpeedButton();
                return true;
            case Key.F1:
                ShortcutsHelp_Click(this, new RoutedEventArgs());
                return true;
            case Key.T:
                AlwaysOnTopMenu.IsChecked = !AlwaysOnTopMenu.IsChecked;
                AlwaysOnTop_Click(this, new RoutedEventArgs());
                return true;
            case Key.O when ctrl:
                if (shift)
                    OpenUrl_Click(this, new RoutedEventArgs());
                else
                    Open_Click(this, new RoutedEventArgs());
                return true;
            case Key.R when ctrl && shift:
                _ = ToggleStreamRecordAsync();
                return true;
            case Key.D1:
                if (!stream) SetMode(SubtitleDisplayMode.Zh);
                return true;
            case Key.D2:
                if (!stream) SetMode(SubtitleDisplayMode.Source);
                return true;
            case Key.D3:
                if (!stream) SetMode(SubtitleDisplayMode.Dual);
                return true;
            case Key.D0:
                if (!stream)
                {
                    _preview?.ToggleSubVisible();
                    RefreshModeButtons();
                }
                return true;
            case Key.L:
                if (!stream) TogglePlaylist_Click(this, new RoutedEventArgs());
                return true;
            case Key.N:
                if (!stream) PlaylistNext_Click(this, new RoutedEventArgs());
                return true;
            case Key.P:
                if (!stream) PlaylistPrev_Click(this, new RoutedEventArgs());
                return true;
            case Key.Z:
                if (!stream) _preview?.NudgeSubDelay(0.5);
                return true;
            case Key.X:
                if (!stream) _preview?.NudgeSubDelay(-0.5);
                return true;
            case Key.Escape:
                if (_isFullscreen) ToggleFullscreen();
                else if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                return true;
            case Key.Enter:
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                return true;
            default:
                return false;
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
        {
            if (_preview?.IsStreamPlayback != true)
            {
                _preview!.SeekRelative(e.Delta > 0 ? 2 : -2);
                NotifySeekPastReady(_preview.Position);
            }
        }
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

    /// <summary>底栏 ComboBox 展开或焦点在其内部时，方向键/空格用于播放而非菜单导航。</summary>
    private bool ControlBarComboStealsFocus(KeyEventArgs e)
    {
        if (SubSourceBox is { IsDropDownOpen: true }
            || SourceLangBox is { IsDropDownOpen: true }
            || TranslateTargetBarBox is { IsDropDownOpen: true }
            || StreamQualityBox is { IsDropDownOpen: true })
            return true;

        if (e.OriginalSource is ComboBox or ComboBoxItem)
            return true;

        if (e.OriginalSource is not DependencyObject src)
            return false;

        return IsDescendantOf(SubSourceBox, src)
               || IsDescendantOf(SourceLangBox, src)
               || IsDescendantOf(TranslateTargetBarBox, src)
               || IsDescendantOf(StreamQualityBox, src);
    }

    private void CloseControlBarDropdowns()
    {
        if (SubSourceBox is { IsDropDownOpen: true })
            SubSourceBox.IsDropDownOpen = false;
        if (SourceLangBox is { IsDropDownOpen: true })
            SourceLangBox.IsDropDownOpen = false;
        if (TranslateTargetBarBox is { IsDropDownOpen: true })
            TranslateTargetBarBox.IsDropDownOpen = false;
        if (StreamQualityBox is { IsDropDownOpen: true })
            StreamQualityBox.IsDropDownOpen = false;
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
