using System.Windows;
using TransubPlayer.Localization;
using TransubPlayer.Services;

namespace TransubPlayer;

public partial class MainWindow
{
    private TrayIconHost? _tray;

    private void EnsureTray()
    {
        if (_tray is not null) return;
        _tray = new TrayIconHost(this, RequestExitFromTray);
    }

    private void RequestExitFromTray()
    {
        // Run on the WPF dispatcher — NotifyIcon callbacks are on a WinForms thread.
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownStarted || _shutdownComplete) return;
            Close();
        });
    }

    private void MinToTray_Click(object sender, RoutedEventArgs e)
    {
        EnsureTray();
        _tray?.MinimizeToTray();
        SetStatus(Loc.Get("Main.Tray.Minimized"));
    }

    private void DisposeTray()
    {
        try { _tray?.Dispose(); } catch { /* ignore */ }
        _tray = null;
    }
}
