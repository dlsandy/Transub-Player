using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private readonly PlaybackQueue _queue = new();
    private DateTime _openedUtc;
    private bool _advancing;
    private bool _playlistOpen;
    private int _openGen;
    private CancellationTokenSource? _openCts;
    private string[]? _pendingExternalOpen;

    public void HandleExternalOpen(string[] paths)
    {
        if (paths.Length == 0) return;
        WindowChromeUtil.ScheduleBringToFront(this);
        if (!IsLoaded || _preview is null)
        {
            _pendingExternalOpen = paths;
            return;
        }

        _ = OpenFilesAsync(paths, append: false);
    }

    private async Task OpenFilesAsync(IEnumerable<string> incoming, bool append)
    {
        var media = BuildOpenQueue(incoming, append);
        if (media.Count == 0)
        {
            SetStatus(Loc.Get("Main.Status.NoMedia"));
            return;
        }

        if (append && _queue.Count > 0)
        {
            _queue.Append(media);
            SetStatus(Loc.Format("Main.Status.PlaylistAdded", media.Count, _queue.Count));
            _preview?.ShowOsd(Loc.Format("Main.Osd.PlaylistAdded", media.Count));
            if (UserTips.ShouldShow(_settings, UserTips.ShiftAppendPlaylist))
            {
                UserTips.Dismiss(_settings, UserTips.ShiftAppendPlaylist);
                _preview?.ShowOsd(Loc.Get("Main.Osd.ShiftAppend"), 2600);
            }
            ShowPlaylist(true);
            RefreshPlaylistUi();
            return;
        }

        _queue.Replace(media);
        if (media.Count > 1)
        {
            ShowPlaylist(true);
            if (_settings.AddSameFolderToPlaylist && CountRawPaths(incoming) == 1)
                _preview?.ShowOsd(Loc.Format("Main.Osd.SameFolderAdded", media.Count));
        }
        await PlayQueueCurrentAsync();
    }

    private IReadOnlyList<string> BuildOpenQueue(IEnumerable<string> incoming, bool append)
    {
        var collected = PlaybackQueue.CollectMedia(incoming);
        if (append || !_settings.AddSameFolderToPlaylist || collected.Count != 1)
            return collected;
        if (CountRawPaths(incoming) > 1)
            return collected;

        var expanded = PlaybackQueue.CollectSameFolderPlaylist(collected[0]);
        return expanded.Count > 1 ? expanded : collected;
    }

    private static int CountRawPaths(IEnumerable<string> incoming)
    {
        var n = 0;
        foreach (var raw in incoming)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            n++;
        }
        return n;
    }

    private async Task PlayQueueCurrentAsync()
    {
        var path = _queue.Current;
        if (path is null) return;
        await OpenPathAsync(path, syncQueue: false);
    }

    private async Task OpenPathAsync(string path, bool syncQueue = true)
    {
        if (_preview is null) return;
        WindowChromeUtil.BringToFront(this);
        if (syncQueue)
        {
            if (MediaSourceHelper.IsNonLocalMedia(path))
                _queue.Replace([path]);
            else
            {
                var queue = _settings.AddSameFolderToPlaylist
                    ? PlaybackQueue.CollectSameFolderPlaylist(path)
                    : PlaybackQueue.CollectMedia([path]);
                _queue.Replace(queue.Count > 0 ? queue : PlaybackQueue.CollectMedia([path]));
            }
        }

        var gen = Interlocked.Increment(ref _openGen);
        try { _openCts?.Cancel(); } catch { /* ignore */ }
        _openCts?.Dispose();
        _openCts = new CancellationTokenSource();
        var ct = _openCts.Token;

        DropHint.Visibility = Visibility.Collapsed;
        PlayerHost.Visibility = Visibility.Visible;
        TitleLabel.Text = MediaSourceHelper.DisplayName(path);
        _lagOsdShown = false;
        _lagBarDismissed = false;
        _lagSeekStreak = 0;
        _seeking = false;
        _seekHoldTarget = -1;
        _openedUtc = DateTime.UtcNow;
        RefreshPlaylistUi();
        ShowOpeningOverlay(path);
        SetStatus(Loc.Get("Main.Status.Opening"));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        try
        {
            await _preview.OpenMediaAsync(path, ct);
            if (gen != Volatile.Read(ref _openGen)) return;

            RecentFiles.Add(_settings, path);
            _settings.Save();
            RefreshRecentMenu();
            _preview.SetVolume((int)VolumeBar.Value);
            if (_settings.Speed > 0) _preview.SetSpeed(_settings.Speed);
            if (_queue.Count > 1)
                _preview.ShowOsd($"{_queue.Index + 1}/{_queue.Count}  {MediaSourceHelper.DisplayName(path)}");
            if (!MediaSourceHelper.IsNonLocalMedia(path))
                EnqueueUpcomingPrefetch();
        }
        catch (OperationCanceledException)
        {
            // Superseded open or shutdown — never rethrow from the UI open path.
        }
        catch (MpvMissingException ex)
        {
            if (gen != Volatile.Read(ref _openGen)) return;
            SetStatus(UserFacingErrors.Message(ex));
            await OfferFetchMpvAsync();
        }
        catch (Exception ex)
        {
            if (gen != Volatile.Read(ref _openGen)) return;
            SetStatus(UserFacingErrors.Message(ex));
            UserFacingErrors.Show(this, ex);
        }
        finally
        {
            if (gen == Volatile.Read(ref _openGen))
                EndOpeningFilePhase();
        }

        if (gen != Volatile.Read(ref _openGen)) return;
        RefreshChrome();
        RefreshPlaybackEnabled();
        RefreshPlaylistUi();
    }

    private void ResetPlaybackChrome()
    {
        SeekBar.Value = 0;
        SeekBar.Maximum = 1;
        PosLabel.Text = "00:00";
        DurLabel.Text = "00:00";
        SourceFill.Visibility = Visibility.Collapsed;
        ZhFill.Visibility = Visibility.Collapsed;
        SeekArea.ToolTip = Loc.Get(TranslateTargetUi.SubProgressLegendKey(_settings, _preview?.IsEnglishSource == true));
        SeekBar.ToolTip = null;
    }

    private async Task StopMediaAsync()
    {
        if (_preview is null || !HasMedia) return;

        Interlocked.Increment(ref _openGen);
        try { _openCts?.Cancel(); } catch { /* ignore */ }

        HideOpeningOverlay();
        HideFloatingPopups();

        try
        {
            await _preview.CloseMediaAsync();
        }
        catch (Exception ex)
        {
            PlayerLog.Write("停止：" + ex.Message);
        }

        _queue.Clear();
        ShowPlaylist(false);

        DropHint.Visibility = Visibility.Visible;
        PlayerHost.Visibility = Visibility.Collapsed;
        TitleLabel.Text = Loc.Get("Main.Tagline");
        SetStatus(Loc.Get("Main.Status.Ready"));

        _lagOsdShown = false;
        _lagBarDismissed = false;
        _lagSeekStreak = 0;
        _seeking = false;
        _seekHoldTarget = -1;

        ResetPlaybackChrome();
        RefreshChrome();
        RefreshPlaybackEnabled();
        RefreshPlaylistUi();
    }

    private void EnqueueUpcomingPrefetch()
    {
        if (!_settings.PrefetchPlaylistSubtitles || _queue.Count <= 1 || _preview is null) return;
        var upcoming = _queue.Items.Skip(_queue.Index + 1).Take(3).ToList();
        if (upcoming.Count > 0)
            _preview.EnqueuePlaylistPrefetch(upcoming);
    }

    private async void OnMediaEnded()
    {
        if (_advancing) return;
        // Ignore false EOF shortly after open, but allow auto-next for very short clips.
        var dur = _preview?.Duration ?? 0;
        if (dur >= 2.0 && (DateTime.UtcNow - _openedUtc).TotalSeconds < 1.5)
            return;

        _preview?.ClearPlaybackPosition();

        if (_settings.PrefetchPlaylistSubtitles && _queue.Count > 1 && _preview is not null)
        {
            // Auto-next opens the next file live; prefetch starts after that item.
            var start = _settings.AutoPlayNext && _queue.HasNext
                ? _queue.Index + 2
                : _queue.Index + 1;
            if (start < _queue.Count)
            {
                var rest = _queue.Items.Skip(start).ToList();
                _preview.EnqueuePlaylistPrefetch(rest);
                if (rest.Count > 0 && !_settings.AutoPlayNext)
                    SetStatus(Loc.Format("Main.Status.PrefetchAfterEnd", rest.Count));
            }
        }

        if (!_settings.AutoPlayNext)
        {
            if (!_settings.PrefetchPlaylistSubtitles || _queue.Count <= 1)
                SetStatus(Loc.Get("Main.Status.PlaybackEnd"));
            return;
        }

        if (!_queue.TryMoveNext())
        {
            SetStatus(Loc.Get("Main.Status.PlaylistEnd"));
            _preview?.ShowOsd(Loc.Get("Main.Osd.PlaylistEnd"));
            RefreshPlaylistUi();
            return;
        }

        _advancing = true;
        try
        {
            _preview?.ShowOsd(Loc.Get("Main.Osd.NextEpisode"));
            await PlayQueueCurrentAsync();
        }
        finally
        {
            _advancing = false;
        }
    }

    private void TogglePlaylist_Click(object sender, RoutedEventArgs e)
        => ShowPlaylist(PlaylistPanel.Visibility != Visibility.Visible);

    private void ShowPlaylist(bool visible)
    {
        _playlistOpen = visible;
        PlaylistPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlaylistButton.Tag = visible ? "on" : null;
        PlaylistButton.ToolTip = visible ? Loc.Get("Main.Playlist.ShowTip") : Loc.Get("Main.Playlist.HideTip");
        RefreshPlaylistUi();
        if (_isFullscreen)
            UpdateFullscreenChromeVisibility();
    }

    private void RefreshPlaylistUi()
    {
        PlaylistTitle.Text = _queue.Count == 0
            ? Loc.Get("Main.Playlist.Title")
            : Loc.Format("Main.Playlist.TitleCount", _queue.Index + 1, _queue.Count);
        var multi = _queue.Count > 1;
        PlaylistPrevButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        PlaylistNextButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPrevButton.IsEnabled = _queue.HasPrev;
        PlaylistNextButton.IsEnabled = _queue.HasNext;

        var rows = new List<PlaylistRow>(_queue.Count);
        for (var i = 0; i < _queue.Count; i++)
        {
            var path = _queue.Items[i];
            var name = MediaSourceHelper.DisplayName(path);
            var display = i == _queue.Index ? $"▶  {name}" : $"    {name}";
            var badge = "";
            if (_preview is not null && i != _queue.Index)
            {
                if (_preview.IsPrefetchRunning(path))
                    badge = Loc.Get("Main.Playlist.PrefetchRunning");
                else if (PreviewPaths.HasReadyAsr(path))
                    badge = Loc.Get("Main.Playlist.PrefetchReady");
                else if (_preview.IsPrefetchFailed(path))
                    badge = Loc.Get("Main.Playlist.PrefetchFailed");
                else if (_preview.IsPrefetchQueued(path))
                    badge = Loc.Get("Main.Playlist.PrefetchQueued");
            }

            rows.Add(new PlaylistRow(display, badge));
        }

        PlaylistBox.ItemsSource = rows;
        if (_queue.Index >= 0 && _queue.Index < rows.Count)
            PlaylistBox.SelectedIndex = _queue.Index;
    }

    private sealed record PlaylistRow(string Display, string Badge);

    private async void PlaylistNext_Click(object sender, RoutedEventArgs e) => await PlaylistSkipAsync(next: true);
    private async void PlaylistPrev_Click(object sender, RoutedEventArgs e) => await PlaylistSkipAsync(next: false);

    private async Task PlaylistSkipAsync(bool next)
    {
        var moved = next ? _queue.TryMoveNext() : _queue.TryMovePrev();
        if (!moved)
        {
            _preview?.ShowOsd(next ? Loc.Get("Main.Osd.PlaylistLast") : Loc.Get("Main.Osd.PlaylistFirst"));
            return;
        }

        RefreshPlaylistUi();
        _preview?.ShowOsd(next ? Loc.Get("Main.Osd.NextEpisode") : Loc.Get("Main.Osd.PrevEpisode"));
        await PlayQueueCurrentAsync();
    }

    private void PlaylistClear_Click(object sender, RoutedEventArgs e)
    {
        var current = _queue.Current;
        _queue.Clear();
        if (!string.IsNullOrWhiteSpace(current))
            _queue.Replace([current]);
        RefreshPlaylistUi();
        SetStatus(Loc.Get("Main.Status.PlaylistCleared"));
    }

    private async void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedIndex < 0) return;
        if (!_queue.TryActivate(PlaylistBox.SelectedIndex)) return;
        await PlayQueueCurrentAsync();
    }

    private async void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && PlaylistBox.SelectedIndex >= 0)
        {
            var removingCurrent = PlaylistBox.SelectedIndex == _queue.Index;
            _queue.RemoveAt(PlaylistBox.SelectedIndex);
            RefreshPlaylistUi();
            e.Handled = true;
            if (removingCurrent)
            {
                if (_queue.Current is not null)
                    await PlayQueueCurrentAsync();
                else
                    await StopMediaAsync();
            }
        }
        else if (e.Key == Key.Enter && PlaylistBox.SelectedIndex >= 0)
        {
            if (_queue.TryActivate(PlaylistBox.SelectedIndex))
                await PlayQueueCurrentAsync();
            e.Handled = true;
        }
    }

    private void PlaylistPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => e.Handled = true;
}
