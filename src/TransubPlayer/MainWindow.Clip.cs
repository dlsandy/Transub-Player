using System.Diagnostics;
using System.IO;
using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private double? _clipStartSec;
    private string? _clipMediaPath;
    private bool _clipBusy;
    private CancellationTokenSource? _clipCts;

    private async void ToggleClip_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ToggleVideoClipAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (_shutdownStarted) return;
            SetStatus(UserFacingErrors.Message(ex));
            RefreshClipUi();
        }
    }

    private void CancelClipMark()
    {
        if (_clipBusy || _clipStartSec is null) return;
        _clipStartSec = null;
        _clipMediaPath = null;
        _preview?.ShowOsd(Loc.Get("Main.Osd.ClipCancelled"), 1400);
        SetStatus(Loc.Get("Main.Status.ClipCancelled"));
        RefreshClipUi();
    }

    private void CancelVideoClip()
    {
        try { _clipCts?.Cancel(); } catch { /* ignore */ }
    }

    private async Task ToggleVideoClipAsync()
    {
        if (_shutdownStarted) return;
        if (_clipBusy)
        {
            SetStatus(Loc.Get("Main.Status.ClipBusy"));
            _preview?.ShowOsd(Loc.Get("Main.Osd.ClipExtracting"), 1200);
            return;
        }

        if (_preview is null || !HasMedia) return;

        var path = _preview.MediaPath!;
        if (_preview.IsStreamPlayback || MediaSourceHelper.IsNonLocalMedia(path) || !File.Exists(path))
        {
            _preview.ShowOsd(Loc.Get("Main.Osd.ClipNotLocal"), 1800);
            SetStatus(Loc.Get("Main.Status.ClipNotLocal"));
            return;
        }

        if (_clipStartSec is null || !string.Equals(_clipMediaPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _clipStartSec = Math.Max(0, _preview.Position);
            _clipMediaPath = path;
            var t = MediaTimeFormat.Format(_clipStartSec.Value);
            _preview.ShowOsd(Loc.Format("Main.Osd.ClipStarted", t), 2000);
            SetStatus(Loc.Format("Main.Status.ClipStarted", t));
            RefreshClipUi();
            return;
        }

        var start = _clipStartSec.Value;
        var end = Math.Max(0, _preview.Position);
        if (end < start) (start, end) = (end, start);
        _clipStartSec = null;
        _clipMediaPath = null;
        RefreshClipUi();

        if (end - start < VideoClip.MinDurationSec)
        {
            _preview.ShowOsd(Loc.Get("Main.Osd.ClipTooShort"), 1600);
            SetStatus(Loc.Get("Main.Status.ClipTooShort"));
            return;
        }

        var output = VideoClip.DefaultOutputPath(path, start, end);
        _clipBusy = true;
        _clipCts?.Dispose();
        _clipCts = new CancellationTokenSource();
        var ct = _clipCts.Token;
        RefreshClipUi();

        var startLabel = MediaTimeFormat.Format(start);
        var endLabel = MediaTimeFormat.Format(end);
        SetStatus(Loc.Format("Main.Status.ClipExtracting", startLabel, endLabel));
        _preview.ShowOsd(Loc.Get("Main.Osd.ClipExtracting"), 1600);

        try
        {
            var result = await VideoClip.ExtractAsync(path, start, end, output, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || _shutdownStarted) return;
            if (result.Ok)
            {
                var name = Path.GetFileName(result.Path);
                _preview?.ShowOsd(Loc.Format("Main.Osd.ClipSaved", name), 2200);
                SetStatus(string.IsNullOrWhiteSpace(result.Note)
                    ? Loc.Format("Main.Status.ClipSaved", result.Path)
                    : Loc.Format("Main.Status.ClipSavedNote", result.Path, result.Note));
            }
            else
            {
                _preview?.ShowOsd(Loc.Get("Main.Osd.ClipFailed"), 2000);
                SetStatus(Loc.Format("Main.Status.ClipFailed", result.Error ?? Loc.Get("Main.Clip.Failed")));
            }
        }
        catch (OperationCanceledException)
        {
            // window closing
        }
        finally
        {
            _clipBusy = false;
            if (!_shutdownStarted)
                RefreshClipUi();
        }
    }

    private void OpenClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = VideoClip.OutputDirectory(_preview?.MediaPath);
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void RefreshClipUi()
    {
        if (_clipStartSec is not null
            && !string.Equals(_clipMediaPath, _preview?.MediaPath, StringComparison.OrdinalIgnoreCase))
        {
            _clipStartSec = null;
            _clipMediaPath = null;
        }

        var on = HasMedia;
        var stream = _preview?.IsStreamPlayback == true;
        var marking = _clipStartSec is not null;
        var can = on && !stream;

        if (ClipMenu is not null)
        {
            ClipMenu.IsEnabled = can && !_clipBusy;
            ClipMenu.Header = _clipBusy
                ? Loc.Get("Main.Menu.ClipBusy")
                : marking
                    ? Loc.Get("Main.Menu.ClipStop")
                    : Loc.Get("Main.Menu.Clip");
            ClipMenu.InputGestureText = "C";
        }

        if (ClipActivePanel is null) return;
        ClipActivePanel.Visibility = can && (marking || _clipBusy)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (ClipStopButton is not null)
            ClipStopButton.IsEnabled = marking && !_clipBusy;
        if (ClipRangeLabel is null) return;

        if (_clipBusy)
            ClipRangeLabel.Text = Loc.Get("Main.Menu.ClipBusy");
        else if (marking)
            ClipRangeLabel.Text = MediaTimeFormat.Format(_clipStartSec!.Value)
                                   + " → "
                                   + MediaTimeFormat.Format(_preview?.Position ?? _clipStartSec.Value);
    }
}
