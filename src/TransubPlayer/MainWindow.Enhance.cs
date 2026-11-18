using System.Windows;
using System.Windows.Controls;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private void RefreshVideoEnhanceMenu()
    {
        if (VideoEnhanceMenu is null) return;

        VideoEnhanceMenu.Items.Clear();
        var current = _preview?.GetVideoEnhanceMode() ?? _settings.GetVideoEnhanceMode();

        foreach (var mode in new[] { VideoEnhanceMode.Off, VideoEnhanceMode.Light, VideoEnhanceMode.Enhance })
        {
            var item = new MenuItem
            {
                Header = Loc.Get(VideoEnhanceModes.LabelKey(mode)),
                IsCheckable = true,
                IsChecked = mode == current,
                Tag = mode,
            };
            var captured = mode;
            item.Click += (_, _) => SetVideoEnhanceFromUi(captured);
            VideoEnhanceMenu.Items.Add(item);
        }

        VideoEnhanceMenu.Items.Add(new Separator());

        var cycle = new MenuItem
        {
            Header = Loc.Get("Main.Menu.VideoEnhance.Cycle"),
            InputGestureText = "E",
        };
        cycle.Click += (_, _) => CycleVideoEnhanceFromUi();
        VideoEnhanceMenu.Items.Add(cycle);

        VideoEnhanceMenu.Header = current == VideoEnhanceMode.Off
            ? Loc.Get("Main.Menu.VideoEnhance")
            : Loc.Format("Main.Menu.VideoEnhanceCurrent", Loc.Get(VideoEnhanceModes.LabelKey(current)));
    }

    private void SetVideoEnhanceFromUi(VideoEnhanceMode mode)
    {
        if (_preview is null)
        {
            _settings.SetVideoEnhanceMode(mode, saveSoon: true);
            RefreshVideoEnhanceMenu();
            return;
        }

        _preview.SetVideoEnhanceMode(mode, persist: true, announce: true);
        RefreshVideoEnhanceMenu();
    }

    private void CycleVideoEnhanceFromUi()
    {
        if (_preview is null)
        {
            var next = VideoEnhanceModes.Cycle(_settings.GetVideoEnhanceMode());
            _settings.SetVideoEnhanceMode(next, saveSoon: true);
            RefreshVideoEnhanceMenu();
            return;
        }

        _preview.CycleVideoEnhanceMode(announce: true);
        RefreshVideoEnhanceMenu();
    }
}
