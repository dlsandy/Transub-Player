using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private bool _preFsPlaylistOpen;
    private bool _chromeUiVisible = true;

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        UpdateFullscreenChromeVisibility();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen || !_settings.HideChromeInFullscreen) return;
        if (_playlistOpen) return;
        // Entering the mpv HwndHost fires WPF MouseLeave even though the cursor is still on video.
        if (IsCursorInsideClient()) return;
        _hideTimer.Stop();
        SetChromeVisible(false);
    }

    private void FullscreenHitZone_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        RevealFullscreenChrome();
    }

    private void BottomBar_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        RevealFullscreenChrome();
    }

    private void BumpChrome()
    {
        if (_isFullscreen)
        {
            UpdateFullscreenChromeVisibility();
            return;
        }

        SetChromeVisible(true);
        _hideTimer.Stop();
    }

    private void RevealFullscreenChrome()
    {
        SetChromeVisible(true);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void UpdateFullscreenChromeVisibility()
    {
        if (!_isFullscreen) return;

        if (!_settings.HideChromeInFullscreen)
        {
            SetChromeVisible(true);
            return;
        }

        // WPF elements already under the cursor (bottom bar / hit strip) — keep chrome up.
        if (_playlistOpen || BottomBar.IsMouseOver || FullscreenHitZone.IsMouseOver)
        {
            RevealFullscreenChrome();
            return;
        }

        if (!TryGetCursorInRoot(out var p, out var w, out var h))
            return;

        var inside = p.X >= -8 && p.Y >= -8 && p.X <= w + 8 && p.Y <= h + 8;
        if (!inside)
        {
            _hideTimer.Stop();
            SetChromeVisible(false);
            return;
        }

        // Large bottom hot zone over the video (Win32 cursor — Mouse.GetPosition is wrong on HwndHost).
        var hotH = Math.Max(120, h * 0.22);
        if (p.Y >= h - hotH)
        {
            RevealFullscreenChrome();
            return;
        }

        _hideTimer.Stop();
        SetChromeVisible(false);
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
        var wantBottom = visible;
        // Fullscreen + auto-hide: keep a WPF hit strip at the bottom so the cursor can leave mpv airspace.
        var wantHit = _isFullscreen && _settings.HideChromeInFullscreen && !visible && !_playlistOpen;

        var wantTitleVis = wantTitle ? Visibility.Visible : Visibility.Collapsed;
        var wantBottomVis = wantBottom ? Visibility.Visible : Visibility.Collapsed;
        var wantHitVis = wantHit ? Visibility.Visible : Visibility.Collapsed;

        if (_chromeUiVisible == visible
            && TitleBar.Visibility == wantTitleVis
            && BottomBar.Visibility == wantBottomVis
            && FullscreenHitZone.Visibility == wantHitVis)
        {
            Cursor = visible || _playlistOpen || wantHit ? Cursors.Arrow : Cursors.None;
            return;
        }

        _chromeUiVisible = visible;
        TitleBar.Visibility = wantTitleVis;
        BottomBar.Visibility = wantBottomVis;
        FullscreenHitZone.Visibility = wantHitVis;
        PlaylistPanel.Visibility = _playlistOpen ? Visibility.Visible : Visibility.Collapsed;
        Cursor = visible || _playlistOpen || wantHit ? Cursors.Arrow : Cursors.None;
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

        if (_playlistOpen)
            ShowPlaylist(false);
        HideFloatingPopups();
        if (_settings.HideChromeInFullscreen)
            SetChromeVisible(false);
        else
            SetChromeVisible(true);

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
        Dispatcher.BeginInvoke(() =>
        {
            UpdateFullscreenChromeVisibility();
            _preview?.ShowOsd(Loc.Get("Main.Menu.Fullscreen"));
        }, DispatcherPriority.Background);
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false;
        _hideTimer.Stop();
        WindowChromeUtil.VideoFullscreen = false;
        FullscreenHitZone.Visibility = Visibility.Collapsed;
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
    }

    private void UpdateFullscreenButton()
    {
        FullscreenButton.Content = _isFullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = _isFullscreen
            ? Loc.Get("Main.Fullscreen.ExitTip")
            : Loc.Get("Main.Fullscreen.EnterTip");
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
