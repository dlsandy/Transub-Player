using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _hideTimer;
    private PreviewController? _preview;
    private EngineLogWindow? _engineLogWindow;
    private bool _seeking;
    private bool _volumeDrag;
    private bool _presetReady;
    private bool _lagOsdShown;
    private DateTime _lagOsdUtc;
    private bool _isFullscreen;
    private WindowState _preFsState = WindowState.Normal;
    private ResizeMode _preFsResize = ResizeMode.CanResize;
    private double _preFsLeft;
    private double _preFsTop;
    private double _preFsWidth;
    private double _preFsHeight;
    private Thickness _preFsMargin;
    private Thickness _preFsBorder;
    private readonly DispatcherTimer _clickTimer;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _closeForUpdate;

    public MainWindow()
    {
        InitializeComponent();
        WindowChrome.SetWindowChrome(this, WindowChromeUtil.Create(52, canResize: true));
        SourceInitialized += (_, _) =>
        {
            WindowChromeUtil.ApplyHostClipChildren(this);
            WindowChromeUtil.AspectLockProvider = TryGetWindowAspectLock;
        };
        Closed += (_, _) => WindowChromeUtil.AspectLockProvider = null;
        Topmost = _settings.AlwaysOnTop;
        AlwaysOnTopMenu.IsChecked = _settings.AlwaysOnTop;
        VolumeBar.Value = Math.Clamp(_settings.Volume, 0, 130);
        VolumeLabel.Text = ((int)VolumeBar.Value).ToString();
        SpeedButton.Content = $"{(_settings.Speed <= 0 ? 1.0 : _settings.Speed):0.##}x";

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => RefreshChrome();
        _uiTimer.Start();

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) =>
        {
            if (!_isFullscreen || !_settings.HideChromeInFullscreen) return;
            // Keep chrome while the pointer is still on the bar / hit strip / bottom hot zone.
            UpdateFullscreenChromeVisibility();
        };
        ApplyHideTimerInterval();
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            _preview?.TogglePause();
        };

        RestoreWindowBounds();
        LocationChanged += (_, _) =>
        {
            NudgeOpeningPopup();
            NudgeWaitZhPopup();
            NudgeLagActionPopup();
            NudgeResumeOfferPopup();
            NudgeFinishedSubPopup();
        };
        // WaitZh / LagAction 用 Popup（独立 HWND），失活时必须关掉，否则会挡住其它窗口
        Deactivated += (_, _) => HideFloatingPopups();
        Activated += (_, _) =>
        {
            UpdateOpeningOverlay();
            UpdateWaitZhOverlay();
            MaybeShowSubtitleLagOsd();
            UpdateResumeOffer();
            _preview?.ProbeFinishedSubtitleOffer();
            UpdateFinishedSubOffer();
        };

        Loaded += (_, _) =>
        {
            WireMpvHostInput();
            _preview = new PreviewController(_settings, PlayerHost, SetStatus, PlayerLog.Write);
            _preview.IsFullscreenProvider = () => _isFullscreen;
            _preview.OfferPresetSetupAsync = OfferPresetSetupAsync;
            _preview.OfferSubtitleCatPickAsync = OfferSubtitleCatPickAsync;
            _preview.OfferEnglishSourceChoiceAsync = OfferEnglishSourceChoiceAsync;
            // Use lambdas — BeginInvoke(methodGroup) reflects the raw CLR signature.
            // Optional parameters (e.g. RefreshPresetUi(bool = true)) then throw
            // TargetParameterCountException: "Parameter count mismatch."
            _preview.StateChanged += () => Dispatcher.BeginInvoke(() => RefreshChrome());
            _preview.PacksChanged += () => Dispatcher.BeginInvoke(() => RefreshPresetUi());
            _preview.MediaEnded += () => Dispatcher.BeginInvoke(() => OnMediaEnded());
            _preview.VideoSizeChanged += (w, h) => Dispatcher.BeginInvoke(() => FitWindowToVideo(w, h));
            _preview.PrefetchChanged += _ => Dispatcher.BeginInvoke(() => RefreshPlaylistUi());
            _presetReady = true;
            RefreshModeButtons();
            RefreshMaxButton();
            RefreshPlaybackEnabled();
            RefreshRecentMenu();
            RefreshFavoritesMenu();
            RefreshRecentHint();
            InitSubSourceBox();
            SeekBackButton.ToolTip = Loc.Format("Main.Transport.SeekBack", _settings.SeekStepSeconds);
            SeekFwdButton.ToolTip = Loc.Format("Main.Transport.SeekFwd", _settings.SeekStepSeconds);
            PlayButton.ToolTip = Loc.Get("Main.Transport.PlayPause");
            MuteButton.ToolTip = Loc.Get("Main.Transport.Mute");
            SpeedButton.ToolTip = Loc.Get("Main.Transport.Speed");

            // Disk probes (mpv/engine/preset readiness) after first paint — keeps cold start snappy.
            Dispatcher.BeginInvoke(new Action(() => _ = AfterFirstPaintAsync()), DispatcherPriority.ApplicationIdle);

            if (_pendingExternalOpen is { Length: > 0 } pending)
            {
                _pendingExternalOpen = null;
                HandleExternalOpen(pending);
            }
        };
    }

    private void RestoreWindowBounds()
    {
        if (!_settings.RememberWindowBounds) return;
        if (_settings.WindowWidth >= MinWidth) Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight) Height = _settings.WindowHeight;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top
            && double.IsFinite(left) && double.IsFinite(top))
        {
            var area = SystemParameters.WorkArea;
            var w = Width > 0 ? Width : MinWidth;
            var h = Height > 0 ? Height : MinHeight;
            // Keep at least 80px of the title bar on-screen (multi-monitor / resolution changes).
            Left = Math.Clamp(left, area.Left - w + 80, area.Right - 80);
            Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - 80));
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    private void SaveWindowBounds()
    {
        if (!_settings.RememberWindowBounds) return;
        if (_isFullscreen) return;
        _settings.WindowWidth = ActualWidth > 0 ? ActualWidth : Width;
        _settings.WindowHeight = ActualHeight > 0 ? ActualHeight : Height;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    /// <summary>供 WM_SIZING：有片源时锁视频区比例；否则锁当前视频区/窗口比例。全屏与最大化时不锁。</summary>
    private WindowChromeUtil.WindowAspectLock? TryGetWindowAspectLock()
    {
        if (!_settings.LockWindowAspectRatio)
            return null;
        if (_isFullscreen || WindowState != WindowState.Normal)
            return null;
        if (ResizeMode == ResizeMode.NoResize)
            return null;

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.PixelsPerInchX / 96.0;
        var scaleY = dpi.PixelsPerInchY / 96.0;

        double contentAspect;
        double chromeWDip;
        double chromeHDip;

        if (_preview is { VideoWidth: > 0, VideoHeight: > 0 })
        {
            contentAspect = (double)_preview.VideoWidth / _preview.VideoHeight;
            if (VideoArea.ActualWidth > 1 && VideoArea.ActualHeight > 1 && ActualWidth > 1 && ActualHeight > 1)
            {
                chromeWDip = Math.Max(0, ActualWidth - VideoArea.ActualWidth);
                chromeHDip = Math.Max(0, ActualHeight - VideoArea.ActualHeight);
            }
            else
            {
                // 布局未就绪时先按整窗比例，避免拖出畸形窗
                chromeWDip = 0;
                chromeHDip = 0;
                if (ActualWidth > 1 && ActualHeight > 1)
                    contentAspect = ActualWidth / ActualHeight;
            }
        }
        else if (VideoArea.ActualWidth > 1 && VideoArea.ActualHeight > 1 && ActualWidth > 1 && ActualHeight > 1)
        {
            contentAspect = VideoArea.ActualWidth / VideoArea.ActualHeight;
            chromeWDip = Math.Max(0, ActualWidth - VideoArea.ActualWidth);
            chromeHDip = Math.Max(0, ActualHeight - VideoArea.ActualHeight);
        }
        else if (ActualWidth > 1 && ActualHeight > 1)
        {
            contentAspect = ActualWidth / ActualHeight;
            chromeWDip = 0;
            chromeHDip = 0;
        }
        else
        {
            return null;
        }

        return new WindowChromeUtil.WindowAspectLock(
            contentAspect,
            (int)Math.Round(chromeWDip * scaleX),
            (int)Math.Round(chromeHDip * scaleY),
            (int)Math.Round(MinWidth * scaleX),
            (int)Math.Round(MinHeight * scaleY));
    }

    private void ApplyHideTimerInterval()
        => _hideTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.FullscreenHideDelaySec, 1, 8));

    private void ApplySettingsFromDisk()
    {
        MpvLocator.Invalidate();
        EngineLocator.Invalidate();
        Loc.Apply(_settings.UiLanguage);
        Topmost = _settings.AlwaysOnTop;
        AlwaysOnTopMenu.IsChecked = _settings.AlwaysOnTop;
        VolumeBar.Value = Math.Clamp(_settings.Volume, 0, 130);
        VolumeLabel.Text = ((int)VolumeBar.Value).ToString();
        SpeedButton.Content = $"{(_settings.Speed <= 0 ? 1.0 : _settings.Speed):0.##}x";
        ApplyHideTimerInterval();
        SeekBackButton.ToolTip = Loc.Format("Main.Transport.SeekBack", _settings.SeekStepSeconds);
        SeekFwdButton.ToolTip = Loc.Format("Main.Transport.SeekFwd", _settings.SeekStepSeconds);
        PlayButton.ToolTip = Loc.Get("Main.Transport.PlayPause");
        MuteButton.ToolTip = Loc.Get("Main.Transport.Mute");
        SpeedButton.ToolTip = Loc.Get("Main.Transport.Speed");
        RefreshHealthIndicator();
        _preview?.ApplyPlayerSettings();
        _preview?.SetDisplayMode(SubtitleDisplayModeUtil.Parse(_settings.SubtitleMode));
        RefreshModeButtons();
        RefreshMaxButton();
        RefreshRecentMenu();
        RefreshFavoritesMenu();
        RefreshRecentHint();
        RefreshPresetUi();
        if (!HasMedia)
        {
            TitleLabel.Text = Loc.Get("Main.Tagline");
            SetStatus(Loc.Get("Main.Status.Ready"));
        }
        if (_preview is { VideoWidth: > 0, VideoHeight: > 0 })
            FitWindowToVideo(_preview.VideoWidth, _preview.VideoHeight);
    }

    private bool _fitWindowPending;
    private int _fitWindowGuard;

    /// <summary>「窗口适应视频」：按片源像素尺寸（DPI 感知）调整窗口，使视频区接近 1:1 显示并落入工作区。</summary>
    private void FitWindowToVideo(int videoW, int videoH)
    {
        if (!string.Equals(_settings.VideoFit, "window", StringComparison.OrdinalIgnoreCase))
        {
            _fitWindowPending = false;
            return;
        }

        if (_isFullscreen || WindowState == WindowState.Maximized)
            return;
        if (videoW <= 0 || videoH <= 0)
            return;

        // 布局尚未完成时延后
        if (VideoArea.ActualWidth < 1 || VideoArea.ActualHeight < 1 || ActualWidth < 1 || ActualHeight < 1)
        {
            _fitWindowPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                if (_preview is { VideoWidth: > 0, VideoHeight: > 0 })
                    FitWindowToVideo(_preview.VideoWidth, _preview.VideoHeight);
            }, DispatcherPriority.Loaded);
            return;
        }

        _fitWindowPending = false;
        if (_fitWindowGuard > 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        // 1 视频像素 ≈ 1 屏幕像素
        var targetVideoW = videoW * 96.0 / dpi.PixelsPerInchX;
        var targetVideoH = videoH * 96.0 / dpi.PixelsPerInchY;

        var chromeW = Math.Max(0, ActualWidth - VideoArea.ActualWidth);
        var chromeH = Math.Max(0, ActualHeight - VideoArea.ActualHeight);
        // 显式加上侧栏，避免 Collapsed→Visible 后首帧量测偏小
        if (PlaylistPanel.Visibility == Visibility.Visible && PlaylistPanel.ActualWidth > 1)
            chromeW = Math.Max(chromeW, PlaylistPanel.ActualWidth + RootBorder.BorderThickness.Left + RootBorder.BorderThickness.Right);

        var work = SystemParameters.WorkArea;
        var maxVideoW = Math.Max(320, work.Width - chromeW - 16);
        var maxVideoH = Math.Max(180, work.Height - chromeH - 16);
        var scale = Math.Min(1.0, Math.Min(maxVideoW / targetVideoW, maxVideoH / targetVideoH));
        targetVideoW *= scale;
        targetVideoH *= scale;

        // 不低于窗口最小客户区（在 MinWidth/MinHeight 约束下尽量贴近片源比例）
        var minVideoW = Math.Max(160, MinWidth - chromeW);
        var minVideoH = Math.Max(90, MinHeight - chromeH);
        if (targetVideoW < minVideoW || targetVideoH < minVideoH)
        {
            var up = Math.Max(minVideoW / targetVideoW, minVideoH / targetVideoH);
            targetVideoW *= up;
            targetVideoH *= up;
            if (targetVideoW > maxVideoW || targetVideoH > maxVideoH)
            {
                var down = Math.Min(maxVideoW / targetVideoW, maxVideoH / targetVideoH);
                targetVideoW *= down;
                targetVideoH *= down;
            }
        }

        var deltaW = targetVideoW - VideoArea.ActualWidth;
        var deltaH = targetVideoH - VideoArea.ActualHeight;
        if (Math.Abs(deltaW) < 2 && Math.Abs(deltaH) < 2)
            return;

        var newW = Math.Clamp(ActualWidth + deltaW, MinWidth, work.Width);
        var newH = Math.Clamp(ActualHeight + deltaH, MinHeight, work.Height);
        if (Math.Abs(ActualWidth - newW) < 1.5 && Math.Abs(ActualHeight - newH) < 1.5)
            return;

        _fitWindowGuard++;
        try
        {
            Width = newW;
            Height = newH;
            if (Left + newW > work.Right)
                Left = Math.Max(work.Left, work.Right - newW);
            if (Top + newH > work.Bottom)
                Top = Math.Max(work.Top, work.Bottom - newH);
            if (Left < work.Left) Left = work.Left;
            if (Top < work.Top) Top = work.Top;
        }
        finally
        {
            // 布局稳定后再校正一次（WindowChrome / 侧栏首帧量测常偏差）
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_preview is { VideoWidth: > 0, VideoHeight: > 0 }
                        && string.Equals(_settings.VideoFit, "window", StringComparison.OrdinalIgnoreCase)
                        && !_isFullscreen
                        && WindowState != WindowState.Maximized)
                    {
                        CorrectFitWindowToVideo(_preview.VideoWidth, _preview.VideoHeight);
                    }
                }
                finally
                {
                    _fitWindowGuard = Math.Max(0, _fitWindowGuard - 1);
                }
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>单次校正：用当前 VideoArea 与目标尺寸的差值微调，避免递归风暴。</summary>
    private void CorrectFitWindowToVideo(int videoW, int videoH)
    {
        if (VideoArea.ActualWidth < 1 || VideoArea.ActualHeight < 1)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var targetVideoW = videoW * 96.0 / dpi.PixelsPerInchX;
        var targetVideoH = videoH * 96.0 / dpi.PixelsPerInchY;
        var chromeW = Math.Max(0, ActualWidth - VideoArea.ActualWidth);
        var chromeH = Math.Max(0, ActualHeight - VideoArea.ActualHeight);
        var work = SystemParameters.WorkArea;
        var maxVideoW = Math.Max(320, work.Width - chromeW - 16);
        var maxVideoH = Math.Max(180, work.Height - chromeH - 16);
        var scale = Math.Min(1.0, Math.Min(maxVideoW / targetVideoW, maxVideoH / targetVideoH));
        targetVideoW *= scale;
        targetVideoH *= scale;

        var deltaW = targetVideoW - VideoArea.ActualWidth;
        var deltaH = targetVideoH - VideoArea.ActualHeight;
        if (Math.Abs(deltaW) < 2 && Math.Abs(deltaH) < 2)
            return;

        var newW = Math.Clamp(ActualWidth + deltaW, MinWidth, work.Width);
        var newH = Math.Clamp(ActualHeight + deltaH, MinHeight, work.Height);
        Width = newW;
        Height = newH;
        if (Left + newW > work.Right)
            Left = Math.Max(work.Left, work.Right - newW);
        if (Top + newH > work.Bottom)
            Top = Math.Max(work.Top, work.Bottom - newH);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("Main.OpenDialog.Title"),
            Filter = MediaFileTypes.BuildOpenFileFilter(Loc.Get("Common.AllFiles") ?? "所有文件"),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true)
            await OpenFilesAsync(dlg.FileNames, append: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private async void OpenUrl_Click(object sender, RoutedEventArgs e)
    {
        string? initial = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                var clip = Clipboard.GetText()?.Trim();
                if (!string.IsNullOrWhiteSpace(clip) && MediaSourceHelper.TryNormalizeMedia(clip, out _))
                    initial = clip;
            }
        }
        catch
        {
            // ignore clipboard access failures
        }

        var dlg = new OpenUrlDialog(initial) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Url)) return;
        if (dlg.AddToFavorites)
            TryAddFavoriteFromOpenUrl(dlg.Url);
        await OpenFilesAsync([dlg.Url], append: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private async void ScreenCapture_Click(object sender, RoutedEventArgs e)
        => await OpenFilesAsync([MediaSourceHelper.DesktopCaptureUrl], append: false);

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await OpenFilesAsync(files, append: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            return;
        }

        if (e.Data.GetData(DataFormats.Text) is string text
            && MediaSourceHelper.TryNormalizeMedia(text, out var url))
            await OpenFilesAsync([url], append: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else if (e.Data.GetDataPresent(DataFormats.Text)
                 && MediaSourceHelper.TryNormalizeMedia(e.Data.GetData(DataFormats.Text)?.ToString() ?? "", out _))
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Play_Click(object sender, RoutedEventArgs e) => _preview?.TogglePause();
    private async void Stop_Click(object sender, RoutedEventArgs e)
        => await StopMediaAsync();

    private void SeekBack_Click(object sender, RoutedEventArgs e)
    {
        _preview?.SeekRelative(-SeekStep());
        NotifySeekPastReady(_preview?.Position ?? 0);
    }

    private void SeekFwd_Click(object sender, RoutedEventArgs e)
    {
        _preview?.SeekRelative(SeekStep());
        NotifySeekPastReady(_preview?.Position ?? 0);
    }
    private void ModeZh_Click(object sender, RoutedEventArgs e) => SetMode(SubtitleDisplayMode.Zh);
    private void ModeSrc_Click(object sender, RoutedEventArgs e) => SetMode(SubtitleDisplayMode.Source);
    private void ModeDual_Click(object sender, RoutedEventArgs e) => SetMode(SubtitleDisplayMode.Dual);
    private void SubDelayLater_Click(object sender, RoutedEventArgs e) => _preview?.NudgeSubDelay(0.5);
    private void SubDelayEarlier_Click(object sender, RoutedEventArgs e) => _preview?.NudgeSubDelay(-0.5);
    private void ToggleSub_Click(object sender, RoutedEventArgs e)
    {
        _preview?.ToggleSubVisible();
        RefreshModeButtons();
    }
    private void Mute_Click(object sender, RoutedEventArgs e) => _preview?.ToggleMute();
    private void CycleSpeed_Click(object sender, RoutedEventArgs e)
    {
        _preview?.CycleSpeed();
        RefreshSpeedButton();
    }

    private void ResetSpeed_Click(object sender, RoutedEventArgs e)
    {
        _preview?.ResetSpeed();
        RefreshSpeedButton();
    }
    private void Speed05_Click(object sender, RoutedEventArgs e) => SetSpeed(0.5);
    private void Speed075_Click(object sender, RoutedEventArgs e) => SetSpeed(0.75);
    private void Speed10_Click(object sender, RoutedEventArgs e) => SetSpeed(1.0);
    private void Speed125_Click(object sender, RoutedEventArgs e) => SetSpeed(1.25);
    private void Speed15_Click(object sender, RoutedEventArgs e) => SetSpeed(1.5);
    private void Speed20_Click(object sender, RoutedEventArgs e) => SetSpeed(2.0);
    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var path = _preview?.Screenshot();
        if (string.IsNullOrWhiteSpace(path)) return;
        var name = Path.GetFileName(path);
        _preview?.ShowOsd(Loc.Format("Main.Osd.Screenshot", name), 2200);
        SetStatus(Loc.Format("Main.Status.ScreenshotSaved", path));
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void AlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = AlwaysOnTopMenu.IsChecked;
        Topmost = _settings.AlwaysOnTop;
        _settings.Save();
        _preview?.ShowOsd(_settings.AlwaysOnTop ? Loc.Get("Main.Osd.TopMostOn") : Loc.Get("Main.Osd.TopMostOff"));
    }

    private void SetMode(SubtitleDisplayMode mode)
    {
        _preview?.SetDisplayMode(mode, announce: true);
        RefreshModeButtons();
        // Mode wait overlay is driven by StateChanged → RefreshChrome; nudge immediately.
        UpdateWaitZhOverlay();
    }

    private void SetSpeed(double speed)
    {
        _preview?.SetSpeed(speed);
        RefreshSpeedButton();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
        => OpenSettings();

    private void FileAssociation_Click(object sender, RoutedEventArgs e)
        => OpenSettings(SettingsWindow.TabAssociation);

    private void OpenSettings(int selectedTab = 0)
    {
        var prevAsrBackend = _settings.AsrBackend;
        var prevModelsPath = _settings.ModelsPath;
        var prevAdvancedLlmPath = _settings.AdvancedLlmPath;
        var prevTranslateUrl = _settings.TranslateUrl;
        var prevTranslateEnabled = _settings.TranslateEnabled;
        var prevTranslateTarget = _settings.TranslateTarget;
        var prevAsrModel = _settings.AsrModel;
        var prevTranslateModelId = _settings.TranslateModelId;

        var win = new SettingsWindow(_settings, selectedTab) { Owner = this };
        if (win.ShowDialog() != true)
            return;

        ApplySettingsFromDisk();
        _ = ApplySettingsSideEffectsSafeAsync(
            prevAsrBackend,
            prevModelsPath,
            prevAdvancedLlmPath,
            prevTranslateUrl,
            prevTranslateEnabled,
            prevTranslateTarget,
            prevAsrModel,
            prevTranslateModelId);
    }

    private async Task ApplySettingsSideEffectsSafeAsync(
        string prevAsrBackend,
        string prevModelsPath,
        string prevAdvancedLlmPath,
        string prevTranslateUrl,
        bool prevTranslateEnabled,
        string prevTranslateTarget,
        string? prevAsrModel = null,
        string? prevTranslateModelId = null)
    {
        if (_preview is null) return;
        try
        {
            var osdParts = await _preview.ApplySettingsSideEffectsAsync(
                prevAsrBackend,
                prevModelsPath,
                prevAdvancedLlmPath,
                prevTranslateUrl,
                prevTranslateEnabled,
                prevTranslateTarget,
                prevAsrModel,
                prevTranslateModelId).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (_preview is null) return;
                RefreshChrome();
                if (osdParts.Count > 0)
                {
                    var msg = osdParts.Count == 1
                        ? osdParts[0]
                        : string.Join(" · ", osdParts.Distinct());
                    _preview.ShowOsd(msg, 3200);
                }
            });
        }
        catch (Exception ex)
        {
            PlayerLog.Write("应用设置：" + ex.Message);
        }
    }

    private void RefreshRecentMenu()
    {
        RecentMenu.Items.Clear();
        var items = RecentFiles.Valid(_settings).Take(Math.Max(1, _settings.RecentFilesMax)).ToList();
        if (items.Count == 0 || _settings.RecentFilesMax == 0)
        {
            RecentMenu.IsEnabled = false;
            RecentMenu.Items.Add(new MenuItem { Header = Loc.Get("Main.RecentEmpty"), IsEnabled = false });
            RefreshRecentHint();
            return;
        }

        RecentMenu.IsEnabled = true;
        foreach (var path in items)
        {
            var name = MediaSourceHelper.DisplayName(path);
            var item = new MenuItem { Header = name, ToolTip = path };
            item.Click += async (_, _) => await OpenPathAsync(path);
            RecentMenu.Items.Add(item);
        }

        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = Loc.Get("Main.Menu.ClearRecent") };
        clear.Click += ClearRecent_Click;
        RecentMenu.Items.Add(clear);

        RefreshRecentHint();
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        RecentFiles.Clear(_settings);
        _settings.Save();
        RefreshRecentMenu();
        SetStatus(Loc.Get("Main.Status.RecentCleared"));
    }

    private void RefreshPresetMenu(bool probeDeps = true)
    {
        _ = probeDeps;
        PresetMenu.Items.Clear();
        if (!_presetReady)
        {
            PresetMenu.IsEnabled = false;
            PresetMenu.Header = Loc.Get("SourceLang.Auto");
            PresetMenu.Items.Add(new MenuItem { Header = Loc.Get("Main.Menu.PresetsLoading"), IsEnabled = false });
            return;
        }

        PresetMenu.IsEnabled = true;
        var current = SourceLanguages.Normalize(_settings.SourceLanguage);
        PresetMenu.Header = Loc.Format("Main.Menu.SourceLangCurrent", SourceLanguages.DisplayName(current));
        foreach (var lang in SourceLanguages.All)
        {
            var item = new MenuItem
            {
                Header = SourceLanguages.DisplayName(lang),
                IsCheckable = true,
                IsChecked = string.Equals(lang, current, StringComparison.OrdinalIgnoreCase),
                Tag = lang,
                ToolTip = Loc.Get("Main.Preset.Tip"),
            };
            var captured = lang;
            item.Click += (_, _) => _ = SelectSourceLanguageAsync(captured);
            PresetMenu.Items.Add(item);
        }
    }

    private enum SourceLangSwitchChoice
    {
        Abort,
        FullRestart,
        FromPlayhead,
    }

    private async Task SelectSourceLanguageAsync(string lang)
    {
        if (!_presetReady || _preview is null) return;
        var normalized = SourceLanguages.Normalize(lang);
        if (string.Equals(SourceLanguages.Normalize(_settings.SourceLanguage), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        if (TryBlockSubtitlePipelineChange())
        {
            RevertSourceLangBoxSelection();
            RefreshPresetMenu();
            return;
        }

        var choice = ConfirmSourceLanguageSwitch(normalized);
        if (choice == SourceLangSwitchChoice.Abort)
        {
            RevertSourceLangBoxSelection();
            RefreshPresetMenu();
            return;
        }

        var fromPlayhead = choice == SourceLangSwitchChoice.FromPlayhead;
        if (fromPlayhead)
        {
            _preview.ShowOsd(Loc.Format("Main.Osd.SourceLangFromPlayhead", MediaTimeFormat.Format(_preview.Position)), 2200);
            SetStatus(Loc.Format("Main.Status.SourceLangFromPlayhead", MediaTimeFormat.Format(_preview.Position)));
        }
        else if (HasMedia && _preview.ShowPreviewChrome && _settings.AutoStartPreview)
        {
            _preview.ShowOsd(Loc.Get("Main.Osd.PresetRestart"));
            SetStatus(Loc.Get("Main.Status.PresetRestart"));
        }

        try
        {
            await _preview.SetSourceLanguageAsync(normalized, CancellationToken.None, fromPlayhead);
        }
        catch (Exception ex)
        {
            RevertSourceLangBoxSelection();
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }

        RefreshPresetUi();
    }

    private SourceLangSwitchChoice ConfirmSourceLanguageSwitch(string normalized)
    {
        if (_preview is null || !ShouldConfirmSourceLanguageSwitch(normalized))
            return SourceLangSwitchChoice.FullRestart;

        var name = SourceLanguages.DisplayName(normalized);
        if (_preview.CanRestartFromPlayhead)
        {
            var result = MessageBox.Show(
                this,
                Loc.Format(
                    "Main.SourceLang.Confirm.Message.WithPlayhead",
                    name,
                    MediaTimeFormat.Format(_preview.Position)),
                Loc.Get("Main.SourceLang.Confirm.Title"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            return result switch
            {
                MessageBoxResult.Yes => SourceLangSwitchChoice.FullRestart,
                MessageBoxResult.Cancel => SourceLangSwitchChoice.FromPlayhead,
                _ => SourceLangSwitchChoice.Abort,
            };
        }

        var yesNo = MessageBox.Show(
            this,
            Loc.Format("Main.SourceLang.Confirm.Message", name),
            Loc.Get("Main.SourceLang.Confirm.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return yesNo == MessageBoxResult.Yes
            ? SourceLangSwitchChoice.FullRestart
            : SourceLangSwitchChoice.Abort;
    }

    private bool ShouldConfirmSourceLanguageSwitch(string normalized)
    {
        if (_preview is null || !HasMedia || !_preview.ShowPreviewChrome) return false;
        if (_preview.CueCount <= 0) return false;
        return !string.Equals(
            SourceLanguages.Normalize(_settings.SourceLanguage),
            normalized,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool TryBlockSubtitlePipelineChange()
    {
        if (_preview?.IsRecording != true) return false;
        MessageBox.Show(
            this,
            Loc.Get("Main.Recording.BlockSubtitleChange"),
            Loc.Get("Main.Recording.BlockSubtitleChange.Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return true;
    }

    private bool ShouldConfirmTranslateTargetSwitch()
    {
        if (_preview is null || !HasMedia || !_preview.ShowPreviewChrome) return false;
        if (_preview.Paused || _preview.CueCount <= 0) return false;
        if (_preview.Position < 300) return false;
        var ready = Math.Max(_preview.SubFrontier, _preview.ZhFrontier);
        return _preview.Position - ready > 60;
    }

    private async Task SelectTranslateTargetAsync(string target)
    {
        if (!_presetReady || _preview is null) return;
        var normalized = TranslateTargets.Normalize(target);
        var prev = _settings.TranslateTarget;
        if (string.Equals(TranslateTargets.Normalize(prev), normalized, StringComparison.OrdinalIgnoreCase))
            return;

        if (TryBlockSubtitlePipelineChange())
        {
            RevertTranslateTargetBoxSelection(prev);
            return;
        }

        if (ShouldConfirmTranslateTargetSwitch())
        {
            var name = TranslateTargetFull(normalized);
            var result = MessageBox.Show(
                this,
                Loc.Format("Main.TranslateTarget.Confirm.Message", name),
                Loc.Get("Main.TranslateTarget.Confirm.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                RevertTranslateTargetBoxSelection(prev);
                return;
            }
        }

        _settings.TranslateTarget = normalized;
        _settings.Save();
        RefreshPresetUi();
        try
        {
            await _preview.ApplySettingsSideEffectsAsync(
                _settings.AsrBackend,
                _settings.ModelsPath,
                _settings.AdvancedLlmPath,
                _settings.TranslateUrl,
                _settings.TranslateEnabled,
                prev).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(RefreshChrome);
        }
        catch (Exception ex)
        {
            RevertTranslateTargetBoxSelection(prev);
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
    }

    private async Task<SubtitleCatResult?> OfferSubtitleCatPickAsync(SubtitleCatPickRequest request)
        => await Dispatcher.InvokeAsync(() => SubtitleCatPickerWindow.Show(this, request));

    private async Task<PresetSetupChoice> OfferPresetSetupAsync(PresetGapReport report)
    {
        if (_preview is null)
            return PresetSetupChoice.Cancel;

        var choice = await Dispatcher.InvokeAsync(() => PresetSetupDialog.Show(
            this,
            report,
            async (status, ct) =>
            {
                await _preview.InstallGapsAsync(report, status, ct).ConfigureAwait(true);
            }));

        if (choice == PresetSetupChoice.AutoInstall)
        {
            await _preview.RefreshGapsAfterInstallAsync(CancellationToken.None);
            return choice;
        }

        if (choice != PresetSetupChoice.ManualInstall)
            return choice;

        MessageBox.Show(
            this,
            ModelManualInstall.BuildInstructions(report, _settings),
            Loc.Get("Settings.ManualInstall.Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        var installer = new PresetDependencyInstaller(_settings, SetStatus, PlayerLog.Write);
        installer.OpenManualGuidance(report);
        var go = MessageBox.Show(
            this,
            Loc.Get("Main.ManualInstall.Prompt"),
            Loc.Get("Main.ManualInstall.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        return go switch
        {
            MessageBoxResult.Yes => PresetSetupChoice.ManualInstall,
            _ => PresetSetupChoice.Cancel,
        };
    }

    private async void RePreview_Click(object sender, RoutedEventArgs e)
        => await RetryPreviewCoreAsync();

    private async void FindOnlineSubtitles_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is null || !HasMedia) return;
        try
        {
            await _preview.FindOnlineSubtitlesAsync(CancellationToken.None);
            RefreshPlaybackEnabled();
            RefreshChrome();
        }
        catch (OperationCanceledException)
        {
            // media switched / closed
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
    }

    private async void RetryPreview_Click(object sender, RoutedEventArgs e)
        => await RetryPreviewCoreAsync();

    private async void RestartFromPlayhead_Click(object sender, RoutedEventArgs e)
        => await RestartFromPlayheadCoreAsync();

    private async Task RestartFromPlayheadCoreAsync()
    {
        if (_preview is null || !HasMedia || !_preview.CanRestartFromPlayhead)
            return;

        if (TryBlockSubtitlePipelineChange())
            return;

        var result = MessageBox.Show(
            this,
            Loc.Format("Main.RestartFromPlayhead.Confirm.Message", MediaTimeFormat.Format(_preview.Position)),
            Loc.Get("Main.RestartFromPlayhead.Confirm.Title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            await _preview.RestartPreviewFromPlayheadAsync(CancellationToken.None);
            RefreshPlaybackEnabled();
            RefreshChrome();
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
    }

    private async Task RetryPreviewCoreAsync()
    {
        if (_preview is null || !HasMedia) return;
        try
        {
            await _preview.RetryPreviewAsync(CancellationToken.None);
            RefreshPlaybackEnabled();
            RefreshChrome();
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
    }

    private async Task AfterFirstPaintAsync()
    {
        InitPresetBox();
        await RunSetupWizardIfNeededAsync().ConfigureAwait(true);
        RefreshHealthIndicator();
        RefreshPresetUi(probeDeps: true);
        await AppUpdateUi.StartupCheckAsync(this, _settings).ConfigureAwait(true);
    }

    private Task RunSetupWizardIfNeededAsync()
    {
        if (!SetupWizard.ShouldShow(_settings))
        {
            if (SetupWizard.IsReady(_settings))
                SetStatus(Loc.Get("Main.Status.Ready"));
            else if (MpvLocator.Find() is null)
                SetStatus(Loc.Get("Main.Status.MpvMissing"));
            else if (EngineLocator.Find(_settings) is null)
                SetStatus(Loc.Get("Main.Status.EngineMissing"));
            else if (_settings.TranslateEnabled && !SetupWizard.IsTranslationReady(_settings))
                SetStatus(Loc.Get("Main.Status.MtMissing"));
            return Task.CompletedTask;
        }

        SetupWizardWindow.Show(this, _settings);
        ApplySettingsFromDisk();
        // Wizard may change engine paths — rebind. Do not fake a translate toggle:
        // llama starts only when opening media that needs MT (ApplyTranslateEnabledAsync).
        _ = ApplySettingsSideEffectsSafeAsync(
            "\0", "\0", "\0",
            _settings.TranslateUrl ?? "",
            _settings.TranslateEnabled,
            _settings.TranslateTarget);
        if (SetupWizard.IsReady(_settings))
            SetStatus(Loc.Get("Main.Status.Ready"));
        else if (MpvLocator.Find() is null)
            SetStatus(Loc.Get("Main.Status.MpvMissing"));
        else if (EngineLocator.Find(_settings) is null)
            SetStatus(Loc.Get("Main.Status.EngineMissing"));
        else if (_settings.TranslateEnabled && !SetupWizard.IsTranslationReady(_settings))
            SetStatus(Loc.Get("Main.Status.MtMissing"));

        return Task.CompletedTask;
    }

    private void RefreshRecentHint()
    {
        RecentHintPanel.Children.Clear();
        var recent = RecentFiles.Valid(_settings).Take(3).ToList();
        if (recent.Count == 0) return;

        RecentHintPanel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Main.Recent"),
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
            IsHitTestVisible = false,
        });

        foreach (var path in recent)
        {
            var name = MediaSourceHelper.DisplayName(path);
            var captured = path;

            var row = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 400,
            };

            var remove = new Button
            {
                Content = "×",
                ToolTip = Loc.Get("Main.Recent.RemoveTip"),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 14,
                Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 28,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(remove, Dock.Right);
            remove.Click += (_, _) => RemoveRecentEntry(captured);

            var open = new Button
            {
                Content = name,
                ToolTip = path,
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            open.Click += async (_, _) => await OpenPathAsync(captured);

            row.Children.Add(remove);
            row.Children.Add(open);
            RecentHintPanel.Children.Add(row);
        }
    }

    private void RemoveRecentEntry(string path)
    {
        RecentFiles.Remove(_settings, path);
        PlaybackPositionStore.Remove(path);
        _settings.Save();
        RefreshRecentMenu();
        SetStatus(Loc.Get("Main.Status.RecentRemoved"));
    }

    private void OpenScreenshotFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = _settings.ResolveScreenshotDir();
        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.Format("Errors.OpenFolder", ex.Message), "Transub Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private int SeekStep() => Math.Max(1, _settings.SeekStepSeconds);
    private int SeekFineStep() => Math.Max(1, _settings.SeekStepFineSeconds);
    private int SeekLargeStep() => Math.Max(1, _settings.SeekStepLargeSeconds);

    private void TransubSite_Click(object sender, RoutedEventArgs e)
        => FirstRunHelp.OpenTransubSite();

    private async Task OfferFetchMpvAsync()
    {
        if (FirstRunHelp.FindFetchMpvScript() is null)
        {
            MessageBox.Show(this, Loc.Get("Wizard.Mpv.NoScript"), "Transub Player",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var go = MessageBox.Show(
            this,
            Loc.Get("Wizard.Mpv.Offer"),
            "Transub Player",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (go != MessageBoxResult.Yes) return;

        SetStatus(Loc.Get("Wizard.Mpv.Downloading"));
        try
        {
            await FirstRunHelp.RunFetchMpvAsync(PlayerLog.Write, CancellationToken.None);
            MpvLocator.Invalidate();
            RefreshHealthIndicator();
            SetStatus(Loc.Get("Wizard.Mpv.ReopenHint"));
            _preview?.ShowOsd(Loc.Get("Wizard.Mpv.Done"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.Format("Wizard.Mpv.Failed", ex.Message));
            MessageBox.Show(this, Loc.Format("Wizard.Mpv.Failed", ex.Message), "Transub Player",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => WindowChromeUtil.DragOrToggle(this, e);

    private void AppMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = AppMenu;
        if (menu is null) return;

        if (PlayerContextMenu is { IsOpen: true })
            PlayerContextMenu.IsOpen = false;

        // Toggle when already open under the title button.
        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        menu.Placement = PlacementMode.Bottom;
        menu.PlacementTarget = AppMenuButton;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => AboutWindow.Show(this);

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await AppUpdateUi.CheckInteractiveAsync(this, _settings, quietIfCurrent: false);

    /// <summary>Live settings instance for update checks (avoid reloading a stale disk copy).</summary>
    internal AppSettings SettingsForUpdate => _settings;

    /// <summary>Status line used while an interactive update check is in flight.</summary>
    internal void SetUpdateStatus(string text) => SetStatus(text);

    /// <summary>Current status label text (for restoring after a transient update-check status).</summary>
    internal string StatusTextSnapshot =>
        Dispatcher.CheckAccess()
            ? StatusLabel.Text ?? ""
            : Dispatcher.Invoke(() => StatusLabel.Text ?? "");

    /// <summary>Exit after staging a portable update (apply script waits on this PID).</summary>
    internal void RequestCloseForUpdate()
    {
        _closeForUpdate = true;
        Close();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        // Title-bar maximize = ordinary window maximize. Never enter video fullscreen here.
        if (_isFullscreen)
            ToggleFullscreen();
        else
            WindowChromeUtil.ToggleMax(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        RefreshMaxButton();
        if (WindowState == WindowState.Minimized)
            HideFloatingPopups();
        else if (IsActive)
        {
            UpdateOpeningOverlay();
            UpdateWaitZhOverlay();
            MaybeShowSubtitleLagOsd();
        }
    }

    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;

        if (!_shutdownStarted && ModelDownloadActivity.IsActive && !_closeForUpdate)
        {
            var confirm = MessageBox.Show(
                this,
                Loc.Get("Main.Close.WhileDownloading.Message"),
                Loc.Get("Main.Close.WhileDownloading.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;

        if (_isFullscreen)
            ExitFullscreen();

        _uiTimer.Stop();
        _hideTimer.Stop();
        _clickTimer.Stop();
        Interlocked.Increment(ref _openGen);
        try { _openCts?.Cancel(); } catch { /* ignore */ }
        _openCts?.Dispose();
        _openCts = null;
        WaitZhPopup.IsOpen = false;
        OpeningPopup.IsOpen = false;
        LagActionPopup.IsOpen = false;
        ResumeOfferPopup.IsOpen = false;
        FinishedSubPopup.IsOpen = false;
        StopResumeOfferTimer();
        SaveWindowBounds();
        IsEnabled = false;
        // Hide before await: ShutdownAsync may take up to HttpBudget (+ process waits).
        // Leaving the window visible looks like a multi-second freeze on the last frame.
        try { _preview?.StopPlaybackImmediate(); } catch { /* ignore */ }
        ShowInTaskbar = false;
        Hide();
        try
        {
            if (_preview is not null)
                await _preview.ShutdownAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PlayerLog.Write("退出清理：" + ex.Message);
        }
        finally
        {
            _preview?.Dispose();
            _preview = null;
            ChildProcessLifetime.KillRemaining();
            _shutdownComplete = true;
            // Close() must not run while still inside Closing (common when ShutdownAsync
            // completes synchronously with no media/engine). Yield so the event can unwind.
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
        }
    }

    private bool HasMedia => !string.IsNullOrWhiteSpace(_preview?.MediaPath);

    private void SetStatus(string text)
    {
        Dispatcher.BeginInvoke(() => StatusLabel.Text = text);
    }

    private void StatusArea_Click(object sender, MouseButtonEventArgs e)
    {
        if (_engineLogWindow is { IsLoaded: true })
        {
            _engineLogWindow.Refresh();
            _engineLogWindow.Activate();
            return;
        }

        _engineLogWindow = new EngineLogWindow { Owner = this };
        _engineLogWindow.Closed += (_, _) => _engineLogWindow = null;
        _engineLogWindow.Show();
    }
}
