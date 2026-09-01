using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

/// <summary>Modal progress UI for model / runtime downloads started from Settings.</summary>
public partial class DownloadProgressWindow : Window
{
    private readonly Func<Action<string>, CancellationToken, Task> _work;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _finished;
    private bool _allowClose;
    private long _lastSpeedBytes;
    private DateTime _lastSpeedSampleUtc;

    internal DownloadProgressResult Result { get; private set; } = DownloadProgressResult.Cancelled;

    private DownloadProgressWindow(string heading, Func<Action<string>, CancellationToken, Task> work)
    {
        _work = work;
        InitializeComponent();
        HeadingText.Text = heading;
        Title = Loc.Get("DownloadProgress.Title");
    }

    /// <summary>
    /// Shows a modal progress dialog, runs <paramref name="work"/>, and returns when finished or cancelled.
    /// </summary>
    internal static DownloadProgressResult ShowAndRun(
        Window owner,
        string heading,
        Func<Action<string>, CancellationToken, Task> work)
    {
        var dlg = new DownloadProgressWindow(heading, work) { Owner = owner };
        try
        {
            dlg.ShowDialog();
            return dlg.Result;
        }
        finally
        {
            try { dlg._cts.Dispose(); } catch { /* ignore */ }
        }
    }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;

        try
        {
            await _work(ReportProgress, _cts.Token).ConfigureAwait(true);
            Result = DownloadProgressResult.Ok;
            ApplyTerminalState(Loc.Get("DownloadProgress.Done"), percent: 100, indeterminate: false);
            _finished = true;
            _allowClose = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            Result = DownloadProgressResult.Cancelled;
            ApplyTerminalState(Loc.Get("DownloadProgress.Cancelled"), indeterminate: false);
            _finished = true;
            _allowClose = true;
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            Result = DownloadProgressResult.Failed;
            ApplyTerminalState(Loc.Format("DownloadProgress.Failed", ex.Message), indeterminate: false);
            ProgressBar.Value = 0;
            CancelButton.Content = Loc.Get("Common.Close");
            _finished = true;
        }
    }

    private void ApplyTerminalState(string message, double? percent = null, bool indeterminate = false)
    {
        StatusText.Text = message;
        SizeText.Visibility = Visibility.Collapsed;
        SpeedText.Visibility = Visibility.Collapsed;
        PercentText.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = indeterminate;
        if (percent is not null)
        {
            ProgressBar.Value = percent.Value;
            PercentText.Text = Loc.Format("DownloadProgress.Percent", (int)percent.Value);
            PercentText.Visibility = Visibility.Visible;
        }
    }

    private void ReportProgress(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportProgress(line));
            return;
        }

        DownloadProgressUi.TryParseLine(line, out var snap);
        var hasBytes = snap.DownloadedBytes is not null || snap.TotalBytes is not null;
        var hasPercent = snap.Percent is not null;

        StatusText.Text = hasBytes || hasPercent
            ? Loc.Get("DownloadProgress.DownloadingPhase")
            : line;

        if (hasPercent)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = 100;
            ProgressBar.Value = snap.Percent!.Value;
            PercentText.Text = Loc.Format("DownloadProgress.Percent", (int)snap.Percent.Value);
            PercentText.Visibility = Visibility.Visible;
        }
        else if (hasBytes && snap.TotalBytes is > 0 && snap.DownloadedBytes is >= 0)
        {
            var pct = 100.0 * snap.DownloadedBytes.Value / snap.TotalBytes.Value;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Maximum = 100;
            ProgressBar.Value = Math.Clamp(pct, 0, 100);
            PercentText.Text = Loc.Format("DownloadProgress.Percent", (int)Math.Clamp(pct, 0, 100));
            PercentText.Visibility = Visibility.Visible;
        }
        else if (!hasBytes)
        {
            ProgressBar.IsIndeterminate = true;
            PercentText.Visibility = Visibility.Collapsed;
        }

        if (snap.DownloadedBytes is >= 0 && snap.TotalBytes is > 0)
        {
            SizeText.Text = Loc.Format("DownloadProgress.SizePair",
                DownloadProgressUi.FormatBytes(snap.DownloadedBytes.Value),
                DownloadProgressUi.FormatBytes(snap.TotalBytes.Value));
            SizeText.Visibility = Visibility.Visible;
        }
        else if (snap.DownloadedBytes is >= 0)
        {
            SizeText.Text = Loc.Format("DownloadProgress.DownloadedOnly",
                DownloadProgressUi.FormatBytes(snap.DownloadedBytes.Value));
            SizeText.Visibility = Visibility.Visible;
        }
        else
        {
            SizeText.Visibility = Visibility.Collapsed;
        }

        var speed = snap.SpeedBytesPerSec ?? EstimateSpeed(snap.DownloadedBytes);
        if (speed is > 0)
        {
            SpeedText.Text = Loc.Format("DownloadProgress.Speed",
                DownloadProgressUi.FormatSpeed(speed.Value));
            SpeedText.Visibility = Visibility.Visible;
        }
        else
        {
            SpeedText.Visibility = Visibility.Collapsed;
        }
    }

    private double? EstimateSpeed(long? downloadedBytes)
    {
        if (downloadedBytes is not >= 0) return null;
        var now = DateTime.UtcNow;
        if (_lastSpeedSampleUtc == default)
        {
            _lastSpeedBytes = downloadedBytes.Value;
            _lastSpeedSampleUtc = now;
            return null;
        }

        var elapsed = (now - _lastSpeedSampleUtc).TotalSeconds;
        if (elapsed < 0.2 || downloadedBytes.Value <= _lastSpeedBytes)
            return null;

        var speed = (downloadedBytes.Value - _lastSpeedBytes) / elapsed;
        _lastSpeedBytes = downloadedBytes.Value;
        _lastSpeedSampleUtc = now;
        return speed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_finished)
        {
            try { _cts.Cancel(); } catch { /* ignore */ }
            ApplyTerminalState(Loc.Get("DownloadProgress.Cancelling"), indeterminate: true);
            CancelButton.IsEnabled = false;
            return;
        }

        _allowClose = true;
        DialogResult = Result == DownloadProgressResult.Ok;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose || _finished)
            return;

        e.Cancel = true;
        try { _cts.Cancel(); } catch { /* ignore */ }
        ApplyTerminalState(Loc.Get("DownloadProgress.Cancelling"), indeterminate: true);
        CancelButton.IsEnabled = false;
    }
}

internal enum DownloadProgressResult
{
    Ok,
    Cancelled,
    Failed,
}
