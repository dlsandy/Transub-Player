using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private bool _preFsPlaylistOpen;
    private bool _chromeUiVisible = true;
    private bool _bottomBarOverlay;
    private bool _lastFsPausedForChrome;
    private bool _hasFsPointerSample;
    private Point _lastFsPointer;
    private DateTime _chromeRevealSuppressUntilUtc;

    private const double FsPointerMoveThresholdPx = 4;
    private static readonly TimeSpan ChromeRevealSuppressAfterHide = TimeSpan.FromMilliseconds(450);

    private static readonly SolidColorBrush WindowedBottomBrush = CreateFrozenBrush(0xE0, 0x16, 0x16, 0x16);
    private static readonly SolidColorBrush OverlayBottomBrush = CreateFrozenBrush(0xB3, 0x16, 0x16, 0x16);

    private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool FullscreenAutoHideEnabled
        => _isFullscreen && _settings.HideChromeInFullscreen;

    private bool IsPlaybackPaused
        => _preview?.Paused == true;

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!FullscreenAutoHideEnabled) return;
        OnFullscreenPointerActivity();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!FullscreenAutoHideEnabled) return;
        if (_playlistOpen || IsPlaybackPaused) return;
        // Entering the mpv HwndHost fires WPF MouseLeave even though the cursor is still on video.
        if (IsCursorInsideClient()) return;
        HideFullscreenChrome();
    }

    private void FullscreenHitZone_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!FullscreenAutoHideEnabled) return;
        // Hit strip opening under a still cursor must not re-show chrome (causes 3s flash loop).
        OnFullscreenPointerActivity();
    }

    private void BottomBar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!FullscreenAutoHideEnabled) return;
        RevealFullscreenChrome();
    }

    private void BumpChrome()
    {
        if (_isFullscreen)
        {
            RevealFullscreenChrome();
            return;
        }

        SetChromeVisible(true);
        _hideTimer.Stop();
    }

    /// <summary>
    /// Mouse-driven reveal: ignore synthetic WM_MOUSEMOVE from cursor hide / hit-strip open
    /// unless the pointer actually moved.
    /// </summary>
    private void OnFullscreenPointerActivity()
    {
        if (!FullscreenAutoHideEnabled) return;

        if (!TryGetCursorInRoot(out var p, out _, out _))
            return;

        var moved = false;
        if (!_hasFsPointerSample)
        {
            _hasFsPointerSample = true;
            _lastFsPointer = p;
        }
        else
        {
            var dx = p.X - _lastFsPointer.X;
            var dy = p.Y - _lastFsPointer.Y;
            if (dx * dx + dy * dy >= FsPointerMoveThresholdPx * FsPointerMoveThresholdPx)
            {
                _lastFsPointer = p;
                moved = true;
            }
        }

        if (!moved)
            return;
        if (DateTime.UtcNow < _chromeRevealSuppressUntilUtc)
            return;

        RevealFullscreenChrome();
    }

    /// <summary>
    /// Show fullscreen chrome after mouse / key / pause. Starts the idle-hide timer unless paused.
    /// </summary>
    private void RevealFullscreenChrome()
    {
        if (!_isFullscreen) return;

        _chromeRevealSuppressUntilUtc = DateTime.MinValue;
        SetChromeVisible(true);
        _hideTimer.Stop();
        if (!_settings.HideChromeInFullscreen)
            return;

        // 暂停时保持控制区，不启动隐藏计时。
        if (IsPlaybackPaused || _playlistOpen)
            return;

        _hideTimer.Start();
    }

    private void HideFullscreenChrome()
    {
        if (!_isFullscreen) return;
        _hideTimer.Stop();
        // Baseline pointer + short suppress so SetCursor / hit-strip open cannot bounce chrome back.
        if (TryGetCursorInRoot(out var p, out _, out _))
        {
            _hasFsPointerSample = true;
            _lastFsPointer = p;
        }

        _chromeRevealSuppressUntilUtc = DateTime.UtcNow + ChromeRevealSuppressAfterHide;
        SetChromeVisible(false);
    }

    /// <summary>Called from RefreshChrome when pause state changes in fullscreen.</summary>
    private void SyncFullscreenChromeForPause(bool paused)
    {
        if (!_isFullscreen) return;

        if (paused == _lastFsPausedForChrome)
            return;
        _lastFsPausedForChrome = paused;

        if (!_settings.HideChromeInFullscreen)
        {
            SetChromeVisible(true);
            return;
        }

        if (paused)
        {
            // 暂停：显示控制区并停止自动隐藏。
            SetChromeVisible(true);
            _hideTimer.Stop();
        }
        else
        {
            // 继续播放：显示一次，然后按空闲计时隐藏。
            RevealFullscreenChrome();
        }
    }

    /// <summary>
    /// Idle hide: after FullscreenHideDelaySec with no input, hide chrome + cursor.
    /// Paused playback keeps chrome visible.
    /// </summary>
    private void HideTimer_Tick()
    {
        if (!FullscreenAutoHideEnabled)
        {
            _hideTimer.Stop();
            return;
        }

        if (IsPlaybackPaused || _playlistOpen)
        {
            _hideTimer.Stop();
            SetChromeVisible(true);
            return;
        }

        // 鼠标仍在控制条上：视为仍在操作，重置计时，避免点不到按钮。
        if (IsPointerOnFullscreenChrome())
        {
            _hideTimer.Stop();
            _hideTimer.Start();
            return;
        }

        HideFullscreenChrome();
    }

    private bool IsPointerOnFullscreenChrome()
    {
        if (_bottomBarOverlay && BottomBarPopup.IsOpen && BottomBar.IsMouseOver)
            return true;
        // Hit strip alone must not keep chrome forever — it only exists while chrome is hidden.
        return false;
    }

    /// <summary>
    /// Screen cursor → RootBorder DIPs via Win32. Required over mpv HWND where WPF mouse APIs go stale.
    /// </summary>
    private bool TryGetCursorInRoot(out Point rootPoint, out double width, out double height)
    {
        rootPoint = default;
        width = RootBorder.ActualWidth;
        height = RootBorder.ActualHeight;
        if (width <= 1 || height <= 1 || !RootBorder.IsLoaded)
            return false;

        if (!GetCursorPos(out var sp))
            return false;

        try
        {
            var src = PresentationSource.FromVisual(RootBorder);
            if (src?.CompositionTarget is null)
                return false;

            var device = new Point(sp.X, sp.Y);
            var screenDip = src.CompositionTarget.TransformFromDevice.Transform(device);
            rootPoint = RootBorder.PointFromScreen(screenDip);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsCursorInsideClient()
    {
        if (!TryGetCursorInRoot(out var p, out var w, out var h))
            return false;
        return p.X >= -8 && p.Y >= -8 && p.X <= w + 8 && p.Y <= h + 8;
    }

    private void SetChromeVisible(bool visible)
    {
        var wantTitle = visible && !_isFullscreen;
        var wantBottom = !_isFullscreen || visible;
        var wantHit = FullscreenAutoHideEnabled && !wantBottom && !_playlistOpen;
        var wantTitleVis = wantTitle ? Visibility.Visible : Visibility.Collapsed;

        var bottomShown = _bottomBarOverlay ? BottomBarPopup.IsOpen : BottomBar.Visibility == Visibility.Visible;
        if (_chromeUiVisible == visible
            && TitleBar.Visibility == wantTitleVis
            && bottomShown == wantBottom
            && FullscreenHitPopup.IsOpen == wantHit)
        {
            ApplyNativeCursor(visible || _playlistOpen || wantHit || !_isFullscreen);
            return;
        }

        _chromeUiVisible = visible;
        TitleBar.Visibility = wantTitleVis;

        if (_isFullscreen)
            EnsureBottomBarOverlay();
        else
            EnsureBottomBarDocked();

        if (wantBottom)
        {
            FullscreenHitPopup.IsOpen = false;
            if (_bottomBarOverlay)
            {
                BottomBar.Visibility = Visibility.Visible;
                SyncBottomBarPopupLayout();
                BottomBarPopup.IsOpen = true;
                NudgeBottomBarPopup();
            }
            else
            {
                BottomBarPopup.IsOpen = false;
                BottomBar.Visibility = Visibility.Visible;
                BottomBarDockHost.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (_bottomBarOverlay)
            {
                BottomBarPopup.IsOpen = false;
            }
            else
            {
                BottomBar.Visibility = Visibility.Collapsed;
            }

            if (wantHit)
            {
                SyncFullscreenHitPopupLayout();
                FullscreenHitPopup.IsOpen = true;
            }
            else
                FullscreenHitPopup.IsOpen = false;
        }

        PlaylistPanel.Visibility = _playlistOpen ? Visibility.Visible : Visibility.Collapsed;
        ApplyNativeCursor(visible || _playlistOpen || wantHit || !_isFullscreen);
    }

    private void EnsureBottomBarDocked()
    {
        if (!_bottomBarOverlay && BottomBarDockHost.Content == BottomBar)
            return;

        BottomBarPopup.IsOpen = false;
        DetachBottomBar();
        BottomBar.ClearValue(FrameworkElement.WidthProperty);
        BottomBar.Background = WindowedBottomBrush;
        BottomBarDockHost.Content = BottomBar;
        BottomBarDockHost.Visibility = Visibility.Visible;
        _bottomBarOverlay = false;
    }

    private void EnsureBottomBarOverlay()
    {
        if (_bottomBarOverlay && BottomBarPopupHost.Content == BottomBar)
            return;

        DetachBottomBar();
        BottomBar.Background = OverlayBottomBrush;
        BottomBar.Visibility = Visibility.Visible;
        BottomBarPopupHost.Content = BottomBar;
        BottomBarDockHost.Visibility = Visibility.Collapsed;
        _bottomBarOverlay = true;
    }

    private void DetachBottomBar()
    {
        if (BottomBarDockHost.Content == BottomBar)
            BottomBarDockHost.Content = null;
        if (BottomBarPopupHost.Content == BottomBar)
            BottomBarPopupHost.Content = null;
        if (BottomBar.Parent is Panel panel)
            panel.Children.Remove(BottomBar);
        else if (BottomBar.Parent is Decorator decorator && decorator.Child == BottomBar)
            decorator.Child = null;
        else if (BottomBar.Parent is ContentControl cc && cc.Content == BottomBar)
            cc.Content = null;
    }

    private void SyncBottomBarPopupLayout()
    {
        if (!_bottomBarOverlay) return;
        var w = Math.Max(1, VideoArea.ActualWidth);
        var h = Math.Max(1, VideoArea.ActualHeight);
        BottomBar.Width = w;
        BottomBar.Measure(new Size(w, double.PositiveInfinity));
        var barH = BottomBar.DesiredSize.Height > 1 ? BottomBar.DesiredSize.Height : 72;
        BottomBarPopup.HorizontalOffset = 0;
        BottomBarPopup.VerticalOffset = Math.Max(0, h - barH);
    }

    private void SyncFullscreenHitPopupLayout()
    {
        var w = Math.Max(1, VideoArea.ActualWidth);
        var h = Math.Max(1, VideoArea.ActualHeight);
        FullscreenHitZone.Width = w;
        FullscreenHitPopup.HorizontalOffset = 0;
        FullscreenHitPopup.VerticalOffset = Math.Max(0, h - FullscreenHitZone.Height);
    }

    private void NudgeBottomBarPopup()
    {
        if (!BottomBarPopup.IsOpen) return;
        var x = BottomBarPopup.HorizontalOffset;
        BottomBarPopup.HorizontalOffset = x + 0.1;
        BottomBarPopup.HorizontalOffset = x;
    }

    private void ApplyNativeCursor(bool visible)
    {
        Cursor = visible ? Cursors.Arrow : Cursors.None;
        // WPF Cursor does not affect the mpv HwndHost airspace — drive Win32 there too.
        PlayerHost.SuppressNativeCursor = !visible && _isFullscreen;
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        if (!HasMedia) return;
        EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        _isFullscreen = true;
        _preFsState = WindowState;
        _preFsResize = ResizeMode;
        _preFsLeft = Left;
        _preFsTop = Top;
        _preFsWidth = ActualWidth > 0 ? ActualWidth : Width;
        _preFsHeight = ActualHeight > 0 ? ActualHeight : Height;
        _preFsMargin = RootBorder.Margin;
        _preFsBorder = RootBorder.BorderThickness;
        _preFsPlaylistOpen = _playlistOpen;
        _lastFsPausedForChrome = IsPlaybackPaused;

        if (_playlistOpen)
            ShowPlaylist(false);
        HideFloatingPopups();
        HideSeekPreview();

        EnsureBottomBarOverlay();

        ResizeMode = ResizeMode.NoResize;
        WindowChromeUtil.ApplyChromeMetrics(this, captionHeight: 0, canResize: false);
        RootBorder.BorderThickness = new Thickness(0);
        RootBorder.Margin = new Thickness(0);
        Topmost = true;

        WindowChromeUtil.VideoFullscreen = true;
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;

        UpdateFullscreenButton();

        _chromeUiVisible = true; // force SetChromeVisible to apply a real transition
        _hideTimer.Stop();
        if (_settings.HideChromeInFullscreen && !IsPlaybackPaused)
        {
            // 进入全屏即隐藏；鼠标 / 键盘 / 暂停后再显示。
            HideFullscreenChrome();
        }
        else
        {
            SetChromeVisible(true);
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isFullscreen) return;
            SyncBottomBarPopupLayout();
            if (BottomBarPopup.IsOpen)
                NudgeBottomBarPopup();
            _preview?.ShowOsd(Loc.Get("Main.Menu.Fullscreen"));
        }, DispatcherPriority.Loaded);
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        _hideTimer.Stop();
        WindowChromeUtil.VideoFullscreen = false;
        FullscreenHitPopup.IsOpen = false;
        BottomBarPopup.IsOpen = false;

        EnsureBottomBarDocked();
        SetChromeVisible(true);

        Topmost = _settings.AlwaysOnTop;
        ResizeMode = _preFsResize;
        WindowChromeUtil.ApplyChromeMetrics(this, captionHeight: 52, canResize: true);
        RootBorder.Margin = _preFsMargin;
        RootBorder.BorderThickness = _preFsBorder;
        WindowState = WindowState.Normal;
        Left = _preFsLeft;
        Top = _preFsTop;
        Width = _preFsWidth;
        Height = _preFsHeight;
        if (_preFsState == WindowState.Maximized)
            WindowState = WindowState.Maximized;
        ShowPlaylist(_preFsPlaylistOpen);
        UpdateFullscreenButton();
        ApplyNativeCursor(true);
    }

    private void UpdateFullscreenButton()
    {
        FullscreenButton.Content = _isFullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = _isFullscreen
            ? Loc.Get("Main.Fullscreen.ExitTip")
            : Loc.Get("Main.Fullscreen.EnterTip");
    }

    /// <summary>Owner deactivated: close overlay popups without leaving chrome state inconsistent.</summary>
    private void OnShellDeactivated()
    {
        HideFloatingPopups();
        HideSeekPreview();
        if (!_isFullscreen) return;

        if (_settings.HideChromeInFullscreen && !IsPlaybackPaused)
            HideFullscreenChrome();
    }

    private void OnShellActivated()
    {
        if (_isFullscreen)
        {
            if (_settings.HideChromeInFullscreen)
            {
                if (IsPlaybackPaused)
                {
                    SetChromeVisible(true);
                    _hideTimer.Stop();
                }
                // 播放中：保持隐藏，等鼠标 / 键盘再显示。
            }
            else
            {
                SetChromeVisible(true);
            }
        }
        else
        {
            EnsureBottomBarDocked();
            SetChromeVisible(true);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
