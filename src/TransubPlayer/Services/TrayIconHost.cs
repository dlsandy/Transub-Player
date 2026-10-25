using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// NotifyIcon for minimize-to-tray. Exit from the tray menu must close the main window
/// so <see cref="ChildProcessLifetime"/> cleanup still runs.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private readonly Window _window;
    private readonly Action _requestExit;
    private Forms.NotifyIcon? _icon;
    private Drawing.Icon? _ownedIcon;
    private int _disposed;
    private bool _inTray;

    public bool IsInTray => _inTray;

    public TrayIconHost(Window window, Action requestExit)
    {
        _window = window;
        _requestExit = requestExit;
    }

    public void EnsureCreated()
    {
        if (_icon is not null || _disposed != 0) return;

        var icon = new Forms.NotifyIcon
        {
            Text = "Transub Player",
            Visible = false,
        };

        _ownedIcon = LoadAppIcon();
        icon.Icon = _ownedIcon;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.Get("Main.Tray.Show"), null, (_, _) => Restore());
        menu.Items.Add(Loc.Get("Main.Tray.Exit"), null, (_, _) => _requestExit());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => Restore();
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                Restore();
        };

        _icon = icon;
    }

    /// <summary>
    /// Prefer the same logo as the window / installer: exe embedded icon, then
    /// Assets/app.ico on disk, then pack resource, else system default.
    /// </summary>
    private static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var fromExe = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (fromExe is not null)
                    return fromExe;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icoPath))
                return new Drawing.Icon(icoPath);
        }
        catch
        {
            // fall through
        }

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo?.Stream is not null)
            {
                using (streamInfo.Stream)
                    return new Drawing.Icon(streamInfo.Stream);
            }
        }
        catch
        {
            // fall through
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    public void MinimizeToTray()
    {
        if (_disposed != 0 || _inTray) return;
        EnsureCreated();
        if (_icon is null) return;

        _inTray = true;
        _icon.Text = "Transub Player";
        _icon.Visible = true;
        _window.ShowInTaskbar = false;
        // Leave Minimized so a later Show() does not flash a minimized frame.
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Hide();
    }

    public void Restore()
    {
        if (_disposed != 0) return;
        _inTray = false;
        if (_icon is not null)
            _icon.Visible = false;

        _window.ShowInTaskbar = true;
        if (!_window.IsVisible)
            _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inTray = false;
        var icon = _icon;
        _icon = null;
        if (icon is not null)
        {
            try { icon.Visible = false; } catch { /* ignore */ }
            try { icon.Dispose(); } catch { /* ignore */ }
        }

        var owned = _ownedIcon;
        _ownedIcon = null;
        try { owned?.Dispose(); } catch { /* ignore */ }
    }
}
