using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop;

internal partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly PreviewController _preview;
    private readonly LanShareHost _lanShare;
    private readonly PlaybackQueue _playlist = new();
    private double? _clipMarkStart;
    private bool _shuttingDown;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _preview = new PreviewController(
            _settings,
            VideoHost,
            status => Dispatcher.UIThread.Post(() => StatusText.Text = status),
            log => PlayerLog.Write(log));

        _lanShare = new LanShareHost(
            _settings,
            status => Dispatcher.UIThread.Post(() => StatusText.Text = status),
            log => PlayerLog.Write(log),
            getDesktopMediaPath: () => _preview.MediaPath);
        _lanShare.Changed += () => Dispatcher.UIThread.Post(RefreshLanChrome);

        _preview.StateChanged += () => Dispatcher.UIThread.Post(RefreshChrome);
        _preview.MediaEnded += () => Dispatcher.UIThread.Post(OnMediaEnded);

        KeyDown += OnKeyDown;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        DragDrop.SetAllowDrop(this, true);

        TaglineText.Text = Loc.Get("Main.Tagline");
        OpenButton.Content = Loc.Get("Main.Open");
        SettingsButton.Content = Loc.Get("Main.Settings");
        StatusText.Text = Loc.Get("Main.Status.Ready");
        PlayPauseButton.Content = "播放";

        Closed += async (_, _) => await ShutdownAsync();
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("Main.Open"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Media")
                {
                    Patterns = MediaFileTypes.PlaybackExtensions.Select(ext => "*" + ext).ToArray(),
                },
            ],
        });
        if (files.Count == 0) return;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().ToList();
        if (paths.Count == 0) return;
        _playlist.Replace(paths);
        await OpenPathAsync(paths[0]);
    }

    private async void OnOpenUrlClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenUrlWindow();
            var url = await dlg.ShowDialog<string?>(this);
            if (string.IsNullOrWhiteSpace(url)) return;
            await OpenPathAsync(url.Trim());
        }
        catch (Exception ex)
        {
            StatusText.Text = UserFacingErrors.Message(ex);
            PlayerLog.Write("OpenUrl: " + ex);
        }
    }

    private async void OnPlaylistClick(object? sender, RoutedEventArgs e)
    {
        var win = new PlaylistWindow(_playlist, async path =>
        {
            _playlist.TryActivate(_playlist.Items.ToList().FindIndex(p =>
                string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
            await OpenPathAsync(path);
        });
        await win.ShowDialog(this);
    }

    private async void OnFavoritesClick(object? sender, RoutedEventArgs e)
    {
        var win = new FavoritesWindow(async path => await OpenPathAsync(path));
        await win.ShowDialog(this);
    }

    private async void OnLanShareClick(object? sender, RoutedEventArgs e)
    {
        var win = new LanShareWindow(_lanShare, _settings);
        await win.ShowDialog(this);
        RefreshLanChrome();
    }

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        // Live stream recording uses mpv stream-record; surface status for Mac shell.
        if (!_preview.IsLiveStreamPlayback)
        {
            StatusText.Text = "录制适用于直播流。本地片请用剪辑。";
            return;
        }

        StatusText.Text = "直播录制：请在设置中确认输出目录 " + StreamRecord.RecordingsDir;
    }

    private async void OnClipClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = _preview.MediaPath;
            if (string.IsNullOrWhiteSpace(path) || MediaSourceHelper.IsNonLocalMedia(path))
            {
                StatusText.Text = Loc.Get("Errors.NoMedia");
                return;
            }

            if (_clipMarkStart is null)
            {
                _clipMarkStart = _preview.Position;
                StatusText.Text = $"剪辑起点 {_clipMarkStart:0.0}s — 再按一次设终点";
                return;
            }

            var start = _clipMarkStart.Value;
            var end = _preview.Position;
            _clipMarkStart = null;
            if (Math.Abs(end - start) < VideoClip.MinDurationSec)
            {
                StatusText.Text = "片段太短";
                return;
            }

            var output = VideoClip.DefaultOutputPath(path, start, end);
            StatusText.Text = "正在导出片段…";
            var result = await VideoClip.ExtractAsync(path, start, end, output, CancellationToken.None);
            StatusText.Text = result.Ok ? $"已导出：{result.Path}" : (result.Error ?? "剪辑失败");
        }
        catch (Exception ex)
        {
            StatusText.Text = UserFacingErrors.Message(ex);
        }
    }

    private void OnTransubClick(object? sender, RoutedEventArgs e)
    {
        TransubHandoff.TryOpen(_settings, _preview.MediaPath, out var message);
        StatusText.Text = message;
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings);
        await win.ShowDialog(this);
        Loc.Apply(_settings.UiLanguage);
        TaglineText.Text = Loc.Get("Main.Tagline");
        RefreshChrome();
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e) => _preview.TogglePause();

    private void OnSeekChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty) return;
        if (!SeekSlider.IsPointerOver) return;
        if (e.NewValue is double v && _preview.Duration > 0.5)
            _preview.Seek(v);
    }

    private void OnSubtitleMenuClick(object? sender, RoutedEventArgs e)
    {
        var next = _preview.DisplayMode switch
        {
            SubtitleDisplayMode.Source => SubtitleDisplayMode.Zh,
            SubtitleDisplayMode.Zh => SubtitleDisplayMode.Dual,
            SubtitleDisplayMode.Dual => SubtitleDisplayMode.Off,
            _ => SubtitleDisplayMode.Source,
        };
        _preview.SetDisplayMode(next, announce: true);
        RefreshChrome();
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;
        var paths = files
            .Select(i => i.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();
        if (paths.Count == 0) return;
        _playlist.Replace(paths);
        await OpenPathAsync(paths[0]);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _preview.TogglePause();
                e.Handled = true;
                break;
            case Key.Left:
                _preview.SeekRelative(-5);
                e.Handled = true;
                break;
            case Key.Right:
                _preview.SeekRelative(5);
                e.Handled = true;
                break;
            case Key.Up:
                _preview.AdjustVolume(5);
                e.Handled = true;
                break;
            case Key.Down:
                _preview.AdjustVolume(-5);
                e.Handled = true;
                break;
            case Key.F:
                OnFullscreenClick(null!, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.M:
                _preview.ToggleMute();
                e.Handled = true;
                break;
            case Key.S:
                OnSubtitleMenuClick(null, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.N:
                if (_playlist.TryMoveNext() && _playlist.Current is { } next)
                    await OpenPathAsync(next);
                e.Handled = true;
                break;
        }
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            DropHint.IsVisible = false;
            await _preview.OpenMediaAsync(path, CancellationToken.None);
            RefreshChrome();
        }
        catch (Exception ex)
        {
            StatusText.Text = UserFacingErrors.Message(ex);
        }
    }

    private async void OnMediaEnded()
    {
        if (_playlist.TryMoveNext() && _playlist.Current is { } next)
            await OpenPathAsync(next);
    }

    private void RefreshLanChrome()
    {
        LanShareButton.Content = _lanShare.IsRunning ? "局域网 · 开" : "局域网";
    }

    private void RefreshChrome()
    {
        PlayPauseButton.Content = _preview.Paused ? "播放" : "暂停";
        if (_preview.Duration > 0.5)
        {
            SeekSlider.Maximum = Math.Max(_preview.Duration, 0.1);
            if (!SeekSlider.IsPointerOver)
                SeekSlider.Value = Math.Clamp(_preview.Position, 0, SeekSlider.Maximum);
        }

        TimeText.Text = $"{MediaTimeFormat.Format(_preview.Position)} / {MediaTimeFormat.Format(_preview.Duration)}";
        Title = string.IsNullOrWhiteSpace(_preview.MediaPath)
            ? "Transub Player"
            : $"Transub Player — {Path.GetFileName(_preview.MediaPath)}";
    }

    private async Task ShutdownAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            try { _lanShare.Dispose(); } catch { /* ignore */ }
            await _preview.ShutdownAsync().ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }
        finally
        {
            ChildProcessLifetime.KillRemaining();
            _preview.Dispose();
        }
    }
}
