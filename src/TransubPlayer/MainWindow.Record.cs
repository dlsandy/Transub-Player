using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private async void ToggleStreamRecord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ToggleStreamRecordAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingErrors.Message(ex));
            RefreshRecordUi();
        }
    }

    private bool _streamQualityBoxSync;

    private async void StreamQualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_streamQualityBoxSync || _preview is null) return;
        if (StreamQualityBox.SelectedValue is not string id || string.IsNullOrWhiteSpace(id)) return;
        try
        {
            await _preview.SetStreamQualityAsync(id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            RefreshStreamQualityUi();
        }
    }

    private void RefreshStreamQualityUi()
    {
        if (StreamQualityBox is null) return;
        var has = _preview?.HasStreamQualities == true;
        StreamQualityBox.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        StreamQualityBox.IsEnabled = has && _preview?.IsRecording != true;

        _streamQualityBoxSync = true;
        try
        {
            if (!has)
            {
                StreamQualityBox.ItemsSource = null;
                StreamQualityBox.SelectedIndex = -1;
                return;
            }

            var items = _preview!.StreamQualities;
            StreamQualityBox.ItemsSource = items;
            var selected = _preview.SelectedStreamQualityId;
            StreamQualityBox.SelectedValue = items.Any(q => q.Id == selected)
                ? selected
                : items[0].Id;
        }
        finally
        {
            _streamQualityBoxSync = false;
        }
    }

    private async Task ToggleStreamRecordAsync()
    {
        if (_preview is null || !_preview.CanRecordStream) return;

        if (_preview.IsRecording)
        {
            SetStatus(Loc.Get("Main.Status.StreamRecordStopping"));
            RefreshRecordUi();
            var result = await _preview.StopStreamRecordAsync().ConfigureAwait(true);
            if (result.Ok)
            {
                var name = Path.GetFileName(result.Path);
                _preview.ShowOsd(Loc.Format("Main.Osd.StreamRecordSaved", name), 2200);
                if (!string.IsNullOrWhiteSpace(result.Error))
                    SetStatus(Loc.Format("Main.Status.StreamRecordSavedNote", result.Path, result.Error));
                else
                    SetStatus(Loc.Format("Main.Status.StreamRecordSaved", result.Path));
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
                SetStatus(result.Error);
            RefreshRecordUi();
            return;
        }

        var defaultName = StreamRecord.DefaultOutputPath(MediaSourceHelper.DisplayName(_preview.MediaPath!));
        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("Main.StreamRecord.SaveTitle"),
            Filter = Loc.Get("Main.StreamRecord.SaveFilter"),
            FileName = Path.GetFileName(defaultName),
            InitialDirectory = StreamRecord.RecordingsDir,
            AddExtension = true,
            DefaultExt = ".mp4",
        };
        if (dlg.ShowDialog(this) != true) return;

        var output = StreamRecord.EnsureOutputExtension(dlg.FileName);
        try
        {
            SetStatus(Loc.Get("Main.Status.StreamRecordStarting"));
            await _preview.StartStreamRecordAsync(output).ConfigureAwait(true);
            _preview.ShowOsd(Loc.Get("Main.Osd.StreamRecording"), 1800);
            SetStatus(Loc.Format("Main.Status.StreamRecording", output));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, Loc.Get("Main.StreamRecord.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshRecordUi();
    }

    private void OpenRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = StreamRecord.RecordingsDir;
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

    private void RefreshRecordUi()
    {
        var canRecord = _preview?.CanRecordStream == true;
        var recording = _preview?.IsRecording == true;

        if (RecordButton is not null)
        {
            RecordButton.Visibility = canRecord && !recording ? Visibility.Visible : Visibility.Collapsed;
            RecordButton.IsEnabled = canRecord && !recording;
            RecordButton.Content = "\uE7C8";
            RecordButton.ToolTip = Loc.Get("Main.StreamRecord.StartTip");
        }

        if (RecordActivePanel is not null)
            RecordActivePanel.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;

        if (recording && RecordElapsedLabel is not null && _preview is not null)
            RecordElapsedLabel.Text = StreamRecord.FormatElapsed(_preview.RecordingElapsed);

        if (RecordMenu is not null)
        {
            RecordMenu.IsEnabled = canRecord;
            RecordMenu.Header = recording
                ? Loc.Get("Main.Menu.StreamRecordStop")
                : Loc.Get("Main.Menu.StreamRecord");
            RecordMenu.InputGestureText = "Ctrl+Shift+R";
        }
    }
}
