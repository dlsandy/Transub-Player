using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private readonly SeekThumbGrabber _seekThumbs = new();
    private CancellationTokenSource? _seekPreviewCts;
    private double _seekPreviewSeconds = -1;

    private void BindSeekThumbMedia()
    {
        var path = _preview?.MediaPath;
        if (string.IsNullOrWhiteSpace(path) || MediaSourceHelper.IsNonLocalMedia(path))
            _seekThumbs.BindMedia(null);
        else
            _seekThumbs.BindMedia(path);
    }

    private void SeekArea_HoverMove(object sender, MouseEventArgs e)
    {
        if (_seeking || !SeekBar.IsEnabled || _preview is null || !_preview.CanSeek)
        {
            HideSeekPreview();
            return;
        }

        if (!_seekThumbs.CanPreview)
        {
            HideSeekPreview();
            return;
        }

        var pos = e.GetPosition(SeekArea);
        var width = Math.Max(1, SeekArea.ActualWidth);
        if (pos.X < 0 || pos.X > width)
        {
            HideSeekPreview();
            return;
        }

        var ratio = Math.Clamp(pos.X / width, 0, 1);
        var seconds = SeekBar.Minimum + (SeekBar.Maximum - SeekBar.Minimum) * ratio;
        ShowSeekPreview(seconds, pos.X);
    }

    private void SeekArea_HoverLeave(object sender, MouseEventArgs e)
        => HideSeekPreview();

    private void ShowSeekPreview(double seconds, double localX)
    {
        SeekPreviewTime.Text = MediaTimeFormat.Format(seconds);
        PositionSeekPreviewPopup(localX);
        if (!SeekPreviewPopup.IsOpen)
            SeekPreviewPopup.IsOpen = true;

        if (Math.Abs(seconds - _seekPreviewSeconds) < 0.35 && SeekPreviewImage.Source is not null)
            return;

        _seekPreviewSeconds = seconds;
        _ = LoadSeekPreviewAsync(seconds);
    }

    private void PositionSeekPreviewPopup(double localX)
    {
        var popupW = 204;
        var popupH = 140;
        var areaW = Math.Max(1, SeekArea.ActualWidth);
        SeekPreviewPopup.HorizontalOffset = Math.Clamp(localX - popupW / 2, 0, Math.Max(0, areaW - popupW));
        SeekPreviewPopup.VerticalOffset = -popupH - 8;
    }

    private async Task LoadSeekPreviewAsync(double seconds)
    {
        try { _seekPreviewCts?.Cancel(); } catch { /* ignore */ }
        _seekPreviewCts?.Dispose();
        _seekPreviewCts = new CancellationTokenSource();
        var ct = _seekPreviewCts.Token;
        var target = seconds;

        try
        {
            var bmp = await _seekThumbs.GrabAsync(seconds, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || bmp is null)
                return;
            if (Math.Abs(target - _seekPreviewSeconds) > 1.0)
                return;
            SeekPreviewImage.Source = bmp;
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch
        {
            // ignore preview failures
        }
    }

    private void HideSeekPreview()
    {
        try { _seekPreviewCts?.Cancel(); } catch { /* ignore */ }
        _seekPreviewSeconds = -1;
        if (SeekPreviewPopup.IsOpen)
            SeekPreviewPopup.IsOpen = false;
        SeekPreviewImage.Source = null;
    }
}
