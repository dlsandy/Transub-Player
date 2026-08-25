using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private string? _openingPath;
    private bool _lagBarDismissed;
    private int _lagSeekStreak;
    private DateTime _lagSeekUtc;
    /// <summary>松手后短暂冻结进度条，避免 mpv 尚未回报新 time-pos 时被 RefreshChrome 拉回旧位置。</summary>
    private double _seekHoldTarget = -1;
    private DateTime _seekHoldUntilUtc;

    private void SeekArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekBar.IsEnabled || _preview is null)
            return;

        _seeking = true;
        _seekHoldTarget = -1;

        // 点在拇指上：交给 Thumb 拖拽（ValueChanged 里实时 Seek）
        if (FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null)
            return;

        var pos = e.GetPosition(SeekBar);
        if (pos.X < 0 || pos.X > SeekBar.ActualWidth || pos.Y < -4 || pos.Y > SeekBar.ActualHeight + 4)
        {
            _seeking = false;
            return;
        }

        ApplySeekRatio(pos.X / Math.Max(1, SeekBar.ActualWidth));
        SeekArea.CaptureMouse();
        e.Handled = true;
    }

    private void SeekArea_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_seeking || !SeekArea.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            return;
        var pos = e.GetPosition(SeekBar);
        ApplySeekRatio(pos.X / Math.Max(1, SeekBar.ActualWidth));
    }

    private void SeekArea_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking)
            return;

        if (SeekArea.IsMouseCaptured)
            SeekArea.ReleaseMouseCapture();
        else
            FinishSeekInteraction(commit: true);

        e.Handled = true;
    }

    private void SeekArea_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_seeking) return;
        FinishSeekInteraction(commit: true);
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_seeking && _seekHoldTarget < 0)
            return;

        PosLabel.Text = MediaTimeFormat.Format(e.NewValue);
        // 拖拇指时 Slider 只改 Value，这里同步推到 mpv
        if (_seeking && !SeekArea.IsMouseCaptured)
        {
            _preview?.Seek(e.NewValue);
            NoteSeekWhileLagging();
        }
    }

    private void FinishSeekInteraction(bool commit)
    {
        if (!_seeking) return;
        var target = SeekBar.Value;
        if (commit && _preview is not null)
        {
            _preview.Seek(target);
            NoteSeekWhileLagging();
        }

        ArmSeekHold(target);
        _seeking = false;
    }

    private void ArmSeekHold(double target)
    {
        _seekHoldTarget = Math.Max(0, target);
        _seekHoldUntilUtc = DateTime.UtcNow.AddSeconds(1.5);
    }

    private bool ShouldSyncSeekBarFromPlayer()
    {
        if (_seeking) return false;
        if (_seekHoldTarget < 0) return true;

        if (_preview is not null && Math.Abs(_preview.Position - _seekHoldTarget) <= 1.0)
        {
            _seekHoldTarget = -1;
            return true;
        }

        if (DateTime.UtcNow >= _seekHoldUntilUtc)
        {
            _seekHoldTarget = -1;
            return true;
        }

        return false;
    }

    private void ApplySeekRatio(double ratio)
    {
        ratio = Math.Clamp(ratio, 0, 1);
        var value = SeekBar.Minimum + (SeekBar.Maximum - SeekBar.Minimum) * ratio;
        value = Math.Clamp(value, SeekBar.Minimum, SeekBar.Maximum);
        SeekBar.Value = value;
        _preview?.Seek(value);
        PosLabel.Text = MediaTimeFormat.Format(value);
        NoteSeekWhileLagging();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void VolumeBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _volumeDrag) return;
        VolumeLabel.Text = ((int)e.NewValue).ToString();
        if (_preview is not null)
            _preview.SetVolume((int)e.NewValue);
        else
        {
            _settings.Volume = (int)e.NewValue;
            _settings.SaveSoon();
        }

        MuteButton.Content = e.NewValue <= 0 || (_preview?.Muted ?? false) ? "🔇" : "🔊";
    }

    private void SyncVolumeUi()
    {
        if (_preview is null) return;
        _volumeDrag = true;
        try
        {
            VolumeBar.Value = _preview.Volume;
            VolumeLabel.Text = _preview.Volume.ToString();
            MuteButton.Content = _preview.Muted || _preview.Volume == 0 ? "🔇" : "🔊";
        }
        finally
        {
            _volumeDrag = false;
        }
    }

    private void RefreshSpeedButton()
    {
        var speed = _preview?.Speed ?? (_settings.Speed <= 0 ? 1.0 : _settings.Speed);
        SpeedButton.Content = $"{speed:0.##}x";
    }

    private void RefreshMaxButton()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxButton.ToolTip = maximized ? Loc.Get("Common.Restore") : Loc.Get("Common.Maximize");
        MaxIcon.Data = Geometry.Parse(maximized ? "M3,1 H9 V7 H8 V8 H2 V2 H3 Z M4,3 H8 V7" : "M1,1 H9 V9 H1 Z");
        if (!_isFullscreen)
            RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    private void RefreshPlaybackEnabled()
    {
        var on = HasMedia;
        SeekBar.IsEnabled = on;
        PlayButton.IsEnabled = on;
        StopButton.IsEnabled = on;
        SeekBackButton.IsEnabled = on;
        SeekFwdButton.IsEnabled = on;
        ModeZhButton.IsEnabled = on;
        ModeSrcButton.IsEnabled = on;
        ModeDualButton.IsEnabled = on;
        if (SubSourceBox is not null)
            SubSourceBox.IsEnabled = on;
        MuteButton.IsEnabled = on;
        VolumeBar.IsEnabled = on;
        SpeedButton.IsEnabled = on;
        ScreenshotButton.IsEnabled = on;
        FullscreenButton.IsEnabled = on;
        PlayMenu.IsEnabled = on;
        StopMenu.IsEnabled = on;
        FullscreenMenu.IsEnabled = on;
        SubMenu.IsEnabled = on;
        FindOnlineSubMenu.IsEnabled = on;
        PresetMenu.IsEnabled = on && _presetReady;
        if (PresetBox is not null)
            PresetBox.IsEnabled = _presetReady;
        SpeedMenu.IsEnabled = on;
        ScreenshotMenu.IsEnabled = on;
        RePreviewMenu.IsEnabled = on && (_preview?.ShowSwitchToPreviewAction == true || _preview?.PreviewRetryAvailable == true);
        StartPreviewMenu.Visibility = on && _preview?.ShowPreviewChrome == true && !_settings.AutoStartPreview
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartPreviewMenu.IsEnabled = on && _preview?.ShowPreviewChrome == true;
        RetryPreviewButton.Visibility = on && _preview?.PreviewRetryAvailable == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartPreviewButton.Visibility = on && _preview?.ShowStartPreviewAction == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        SwitchToPreviewButton.Visibility = on && _preview?.ShowSwitchToPreviewAction == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        InstallPresetButton.Visibility = on && _preview?.PresetInstallAvailable == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RefreshChrome()
    {
        if (_preview is null) return;
        PlayButton.Content = _preview.Paused ? "▶" : "⏸";
        var syncSeek = ShouldSyncSeekBarFromPlayer();
        if (_preview.Duration > 0)
        {
            SeekBar.Maximum = _preview.Duration;
            if (syncSeek)
                SeekBar.Value = Math.Clamp(_preview.Position, 0, _preview.Duration);
            DurLabel.Text = MediaTimeFormat.Format(_preview.Duration);
        }

        PosLabel.Text = MediaTimeFormat.Format(syncSeek ? _preview.Position : SeekBar.Value);
        SyncVolumeUi();
        RefreshSpeedButton();
        RefreshModeButtons();
        RefreshPlaybackEnabled();
        UpdateSubtitleProgress();
        UpdatePresetHint();
        UpdateWaitZhOverlay();
        MaybeShowSubtitleLagOsd();
    }

    private void UpdateWaitZhOverlay()
    {
        if (_preview is null || !_preview.WaitingForZh)
        {
            if (WaitZhPopup.IsOpen)
                WaitZhPopup.IsOpen = false;
            return;
        }

        WaitZhTitleLabel.Text = string.IsNullOrWhiteSpace(_preview.WaitZhOverlayTitle)
            ? Loc.Get("Main.WaitZh.Title")
            : _preview.WaitZhOverlayTitle;
        WaitZhDetailLabel.Text = string.IsNullOrWhiteSpace(_preview.WaitZhOverlayDetail)
            ? Loc.Get("Main.Bootstrap.PleaseWait")
            : _preview.WaitZhOverlayDetail;

        // Popup 是独立 HWND，主窗失活时仍置顶会挡住其它应用
        if (!ShouldShowFloatingPopups())
        {
            if (WaitZhPopup.IsOpen)
                WaitZhPopup.IsOpen = false;
            return;
        }

        SyncWaitZhPopupLayout();
        if (!WaitZhPopup.IsOpen)
            WaitZhPopup.IsOpen = true;
        else
            NudgeWaitZhPopup();
    }

    /// <summary>仅主窗前台且未最小化时显示独立 HWND 的 Popup，避免挡住其它窗口。</summary>
    private bool ShouldShowFloatingPopups()
        => IsActive && WindowState != WindowState.Minimized;

    private void HideFloatingPopups()
    {
        if (OpeningPopup.IsOpen)
            OpeningPopup.IsOpen = false;
        if (WaitZhPopup.IsOpen)
            WaitZhPopup.IsOpen = false;
        if (LagActionPopup.IsOpen)
            LagActionPopup.IsOpen = false;
    }

    private void ShowOpeningOverlay(string path)
    {
        _openingPath = path;
        UpdateOpeningOverlay();
    }

    private void EndOpeningFilePhase()
    {
        _openingPath = null;
        UpdateOpeningOverlay();
    }

    private void HideOpeningOverlay()
    {
        _openingPath = null;
        if (OpeningPopup.IsOpen)
            OpeningPopup.IsOpen = false;
    }

    private void UpdateOpeningOverlay()
    {
        var showBootstrap = _preview?.ShowOpeningBootstrap == true;
        if (string.IsNullOrWhiteSpace(_openingPath) && !showBootstrap)
        {
            if (OpeningPopup.IsOpen)
                OpeningPopup.IsOpen = false;
            return;
        }

        if (showBootstrap && _preview is not null)
        {
            OpeningTitleLabel.Text = _preview.OpeningBootstrapTitle;
            OpeningDetailLabel.Text = string.IsNullOrWhiteSpace(_preview.OpeningBootstrapDetail)
                ? Loc.Get("Main.Bootstrap.PleaseWait")
                : _preview.OpeningBootstrapDetail;
        }
        else
        {
            OpeningTitleLabel.Text = Loc.Get("Main.Opening.Title");
            OpeningDetailLabel.Text = MediaSourceHelper.DisplayName(_openingPath ?? "");
        }

        if (!ShouldShowFloatingPopups())
        {
            if (OpeningPopup.IsOpen)
                OpeningPopup.IsOpen = false;
            return;
        }

        SyncOpeningPopupLayout();
        if (!OpeningPopup.IsOpen)
            OpeningPopup.IsOpen = true;
        else
            NudgeOpeningPopup();
    }

    private void SyncOpeningPopupLayout()
    {
        var w = Math.Max(1, VideoArea.ActualWidth);
        var h = Math.Max(1, VideoArea.ActualHeight);
        OpeningDimmer.Width = w;
        OpeningDimmer.Height = h;
    }

    private void NudgeOpeningPopup()
    {
        if (!OpeningPopup.IsOpen) return;
        var x = OpeningPopup.HorizontalOffset;
        OpeningPopup.HorizontalOffset = x + 0.1;
        OpeningPopup.HorizontalOffset = x;
    }

    private void SyncWaitZhPopupLayout()
    {
        var w = Math.Max(1, VideoArea.ActualWidth);
        var h = Math.Max(1, VideoArea.ActualHeight);
        WaitZhDimmer.Width = w;
        WaitZhDimmer.Height = h;
    }

    /// <summary>Popup 不跟随主窗移动时，微调 Offset 强制重算位置。</summary>
    private void NudgeWaitZhPopup()
    {
        if (!WaitZhPopup.IsOpen) return;
        var x = WaitZhPopup.HorizontalOffset;
        WaitZhPopup.HorizontalOffset = x + 0.1;
        WaitZhPopup.HorizontalOffset = x;
    }

    private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (OpeningPopup.IsOpen)
        {
            SyncOpeningPopupLayout();
            NudgeOpeningPopup();
        }

        if (WaitZhPopup.IsOpen)
        {
            SyncWaitZhPopupLayout();
            NudgeWaitZhPopup();
        }

        if (LagActionPopup.IsOpen)
        {
            SyncLagActionPopupLayout();
            NudgeLagActionPopup();
        }

        // 仅在片源尺寸已到、先前因布局未完成而挂起时补试
        if (_fitWindowPending && _preview is { VideoWidth: > 0, VideoHeight: > 0 })
            FitWindowToVideo(_preview.VideoWidth, _preview.VideoHeight);
    }

    private void SyncLagActionPopupLayout()
    {
        var w = Math.Max(1, VideoArea.ActualWidth);
        var h = Math.Max(1, VideoArea.ActualHeight);
        LagActionBar.Measure(new Size(w, h));
        var barW = LagActionBar.DesiredSize.Width > 1 ? LagActionBar.DesiredSize.Width : 420;
        var barH = LagActionBar.DesiredSize.Height > 1 ? LagActionBar.DesiredSize.Height : 72;
        LagActionPopup.HorizontalOffset = Math.Max(16, (w - barW) / 2);
        LagActionPopup.VerticalOffset = Math.Max(16, h - barH - 16);
    }

    private void NudgeLagActionPopup()
    {
        if (!LagActionPopup.IsOpen) return;
        var x = LagActionPopup.HorizontalOffset;
        LagActionPopup.HorizontalOffset = x + 0.1;
        LagActionPopup.HorizontalOffset = x;
    }

    private void SeekArea_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSubtitleProgress();

    private void UpdateSubtitleProgress()
    {
        if (_preview is null || !_preview.ShowPreviewChrome || _preview.Duration <= 1)
        {
            SourceFill.Visibility = Visibility.Collapsed;
            ZhFill.Visibility = Visibility.Collapsed;
            SeekArea.ToolTip = Loc.Get(TranslateTargetUi.SubProgressLegendKey(_settings, _preview?.IsEnglishSource == true));
            SeekBar.ToolTip = null;
            return;
        }

        var width = FrontierCanvas.ActualWidth;
        if (width <= 1)
        {
            SourceFill.Visibility = Visibility.Collapsed;
            ZhFill.Visibility = Visibility.Collapsed;
            return;
        }

        var src = _preview.SubFrontier;
        var zh = _preview.ZhFrontier;
        SetFill(SourceFill, src, _preview.Duration, width);
        SetFill(ZhFill, zh, _preview.Duration, width);

        var tip = src <= 0
            ? Loc.Get("Main.Seek.NoFrontier")
            : zh > 0
                ? Loc.Format("Main.Seek.Both", MediaTimeFormat.Format(src), MediaTimeFormat.Format(zh))
                : Loc.Format("Main.Seek.SourceOnly", MediaTimeFormat.Format(src));
        SeekBar.ToolTip = tip;
        SeekArea.ToolTip = tip;

        if (src > 0 && _preview is not null)
        {
            var enSrc = _preview.IsEnglishSource;
            var legendTip = TranslateTargetUi.FrontierLegendTipId(_settings, enSrc);
            var legendKey = TranslateTargetUi.FrontierLegendKey(_settings, enSrc);
            if (UserTips.ShouldShow(_settings, legendTip))
            {
                UserTips.Dismiss(_settings, legendTip);
                _preview.ShowOsd(Loc.Get(legendKey), 2800);
            }
        }
    }

    private static void SetFill(System.Windows.Controls.Border fill, double frontier, double duration, double width)
    {
        if (frontier <= 0)
        {
            fill.Visibility = Visibility.Collapsed;
            fill.Width = 0;
            return;
        }

        fill.Visibility = Visibility.Visible;
        fill.Width = Math.Clamp(frontier / duration, 0, 1) * width;
    }

    private void MaybeShowSubtitleLagOsd()
    {
        if (_preview is null || !_preview.ShowPreviewChrome)
        {
            HideLagActionBar();
            return;
        }

        if (_preview.Paused)
        {
            HideLagActionBar();
            return;
        }

        var ready = _preview.EffectiveReadyFrontier();
        if (ready <= 0)
        {
            HideLagActionBar();
            return;
        }

        var gap = _preview.Position - ready;
        if (gap < 3)
        {
            _lagOsdShown = false;
            _lagBarDismissed = false;
            HideLagActionBar();
            return;
        }

        var quiet = _isFullscreen && _settings.FullscreenQuietOsd;
        var interval = quiet ? 90 : 45;
        var now = DateTime.UtcNow;
        if (!_lagOsdShown || (now - _lagOsdUtc).TotalSeconds >= interval)
        {
            _lagOsdShown = true;
            _lagOsdUtc = now;
            var lagOsdKey = _preview.DisplayMode == SubtitleDisplayMode.Source ? "Main.Lag.Osd.Source" : "Main.Lag.Osd.Sub";
            _preview.ShowOsd(Loc.Get(lagOsdKey), 1800);
            MaybeOfferWaitFirstZh();
            MaybeOfferSubDelayHint();
        }

        if (quiet)
        {
            HideLagActionBar();
            return;
        }

        if (!_lagBarDismissed)
            ShowLagActionBar(ready);
    }

    private void ShowLagActionBar(double ready)
    {
        if (!ShouldShowFloatingPopups())
        {
            if (LagActionPopup.IsOpen)
                LagActionPopup.IsOpen = false;
            return;
        }

        LagActionTitle.Text = Loc.Format("Main.Lag.TitleWithReady", MediaTimeFormat.Format(ready));
        SyncLagActionPopupLayout();
        if (!LagActionPopup.IsOpen)
            LagActionPopup.IsOpen = true;
        else
            NudgeLagActionPopup();
    }

    private void HideLagActionBar()
    {
        if (LagActionPopup.IsOpen)
            LagActionPopup.IsOpen = false;
    }

    private void LagJumpReady_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null) return;
        var ready = Math.Max(0, _preview.EffectiveReadyFrontier() - 0.5);
        _preview.Seek(ready);
        _lagBarDismissed = true;
        HideLagActionBar();
        SetStatus(Loc.Format("Main.Status.JumpReady", MediaTimeFormat.Format(ready)));
    }

    private void LagSwitchSource_Click(object sender, RoutedEventArgs e)
    {
        _preview?.SetDisplayMode(SubtitleDisplayMode.Source);
        _lagBarDismissed = true;
        HideLagActionBar();
        RefreshModeButtons();
        SetStatus(Loc.Get("Main.Status.SwitchToSource"));
    }

    private void LagDismiss_Click(object sender, RoutedEventArgs e)
    {
        _lagBarDismissed = true;
        HideLagActionBar();
    }

    private void MaybeOfferWaitFirstZh()
    {
        if (_preview is null) return;
        if (_preview.IsEnglishSource) return;
        if (_settings.WaitForFirstZhBeforePlay) return;
        if (!UserTips.ShouldShow(_settings, UserTips.OfferWaitFirstZh)) return;

        UserTips.Dismiss(_settings, UserTips.OfferWaitFirstZh);
        var result = MessageBox.Show(
            this,
            Loc.Get("Main.Osd.WaitFirstZhOffer"),
            "Transub Player",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        _settings.WaitForFirstZhBeforePlay = true;
        if (_settings.WaitForZhMinutes <= 0)
            _settings.WaitForZhMinutes = 1;
        _settings.Save();
        SetStatus(Loc.Get("Main.Status.WaitFirstZhEnabled"));
    }

    private void MaybeOfferSubDelayHint()
    {
        if (!UserTips.ShouldShow(_settings, UserTips.SubDelayHint)) return;
        if (_lagSeekStreak < 3) return;
        UserTips.Dismiss(_settings, UserTips.SubDelayHint);
        _preview?.ShowOsd(Loc.Get("Main.Osd.SubDelayHint"), 2200);
        SetStatus(Loc.Get("Main.Status.SubDelayHint"));
    }

    private void NoteSeekWhileLagging()
    {
        if (_preview is null || !_preview.ShowPreviewChrome || _preview.Paused) return;
        var ready = _preview.EffectiveReadyFrontier();
        if (ready <= 0 || _preview.Position - ready < 3) return;

        var now = DateTime.UtcNow;
        if ((now - _lagSeekUtc).TotalSeconds > 12)
            _lagSeekStreak = 0;
        _lagSeekUtc = now;
        _lagSeekStreak++;
        MaybeOfferSubDelayHint();
    }

    private void RefreshModeButtons()
    {
        var mode = _preview?.DisplayMode ?? SubtitleDisplayModeUtil.Parse(_settings.SubtitleMode);
        var content = SubtitleDisplayModeUtil.IsContentMode(mode)
            ? mode
            : (_preview?.LastContentMode ?? SubtitleDisplayMode.Zh);

        var pending = _preview?.ShowingZhPending == true
                      && content is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual;

        ModeZhButton.Tag = content == SubtitleDisplayMode.Zh
            ? (pending ? "pending" : "on")
            : null;
        ModeSrcButton.Tag = content == SubtitleDisplayMode.Source ? "on" : null;
        ModeDualButton.Tag = content == SubtitleDisplayMode.Dual
            ? (pending ? "pending" : "on")
            : null;

        ModeZhButton.ToolTip = _preview?.ModeTip(SubtitleDisplayMode.Zh) ?? TranslateTargetUi.ModeTranslationTip(_settings, _preview?.IsEnglishSource == true);
        ModeDualButton.ToolTip = _preview?.ModeTip(SubtitleDisplayMode.Dual) ?? TranslateTargetUi.ModeDualTip(_settings, _preview?.IsEnglishSource == true);
        ModeSrcButton.ToolTip = _preview?.ModeTip(SubtitleDisplayMode.Source) ?? TranslateTargetUi.ModeSourceTip(_settings, _preview?.IsEnglishSource == true);

        ModeZhButton.Content = TranslateTargetUi.ModeTranslationLabel(_settings);
        ModeDualButton.Content = TranslateTargetUi.ModeDualLabel(_settings);
        ModeSrcButton.Content = Loc.Get("Main.Mode.Src");
        ModeZhMenu.Header = TranslateTargetUi.ModeTranslationLabel(_settings);
        ModeDualMenu.Header = TranslateTargetUi.ModeDualLabel(_settings);
        ModeSrcMenu.Header = Loc.Get("Main.Mode.Src");

        var hidePreviewChrome = _preview?.ShowPreviewChrome != true;
        // 转写路径即显示版式芯片；本地字幕/成片外挂与预设一并隐藏
        var onPreviewPath = HasMedia && _preview is not null && !hidePreviewChrome;

        ModeLayoutBorder.Visibility = onPreviewPath ? Visibility.Visible : Visibility.Collapsed;
        PresetBoxBorder.Visibility = hidePreviewChrome ? Visibility.Collapsed : Visibility.Visible;
        PresetMenu.Visibility = hidePreviewChrome ? Visibility.Collapsed : Visibility.Visible;
        RefreshSubSourceBox();

        var layoutMenus = onPreviewPath ? Visibility.Visible : Visibility.Collapsed;
        ModeZhMenu.Visibility = layoutMenus;
        ModeSrcMenu.Visibility = layoutMenus;
        ModeDualMenu.Visibility = layoutMenus;
        SubLayoutMenuSep.Visibility = layoutMenus;
    }
}
