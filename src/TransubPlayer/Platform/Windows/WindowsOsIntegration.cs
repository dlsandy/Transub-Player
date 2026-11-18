using TransubPlayer.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Platform.Windows;

internal sealed class WindowsOsIntegration : IOsIntegration
{
    private readonly AppSettings _settings;
    private readonly Func<System.Windows.Window?> _mainWindow;

    public WindowsOsIntegration(AppSettings settings, Func<System.Windows.Window?> mainWindow)
    {
        _settings = settings;
        _mainWindow = mainWindow;
    }

    public void EnsureSingleInstance(Action<IReadOnlyList<string>> onOpenFiles)
    {
        // Wired from App.xaml.cs via existing SingleInstanceHost.
    }

    public void RegisterFileAssociations()
    {
        try { ApplyPlaybackAssociations(_settings); } catch { /* ignore */ }
    }

    public FileAssociationApplyResult ApplyPlaybackAssociations(AppSettings settings)
    {
        var r = FileAssociationService.Apply(MediaFileTypes.PlaybackExtensions, associate: true);
        if (r.Succeeded > 0)
        {
            var set = new HashSet<string>(
                settings.AssociatedExtensions.Select(MediaFileTypes.NormalizeExtension),
                StringComparer.OrdinalIgnoreCase);
            foreach (var ext in MediaFileTypes.PlaybackExtensions)
                set.Add(MediaFileTypes.NormalizeExtension(ext));
            settings.AssociatedExtensions = [..set];
        }

        return r;
    }

    public void RegisterUrlScheme()
    {
        // transub:// is owned by Transub installer on Windows.
    }

    public IDisposable? CreateTrayIcon(Action showMain, Action quit)
    {
        // Tray is owned by MainWindow.Tray — keep null here.
        return null;
    }

    public bool TryHandoffToTransub(string? mediaPath, string? draftSubtitlePath, out string message)
        => TransubHandoff.TryOpen(_settings, mediaPath, out message);
}
