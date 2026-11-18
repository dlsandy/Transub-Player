using System.Diagnostics;
using TransubPlayer.Localization;
using TransubPlayer.Platform;
using TransubPlayer.Services;

namespace TransubPlayer.Desktop.Platform;

internal sealed class MacOsIntegration : IOsIntegration
{
    private readonly AppSettings _settings;

    public MacOsIntegration(AppSettings settings) => _settings = settings;

    public void EnsureSingleInstance(Action<IReadOnlyList<string>> onOpenFiles)
    {
        // Avalonia desktop lifetime + named mutex can be added later.
    }

    public void RegisterFileAssociations()
    {
        // Bundled Info.plist declares document types; runtime LSSetDefaultRoleHandler optional.
    }

    public FileAssociationApplyResult ApplyPlaybackAssociations(AppSettings settings)
    {
        // LaunchServices associations are declared in the .app Info.plist for Mac packages.
        return new FileAssociationApplyResult(0, 0, Loc.Get("Settings.Association.MacInfoPlist"));
    }

    public void RegisterUrlScheme()
    {
        // Info.plist CFBundleURLTypes registers transub:// for the Mac app bundle.
    }

    public IDisposable? CreateTrayIcon(Action showMain, Action quit)
        => null; // Tray wired from MainWindow when needed.

    public bool TryHandoffToTransub(string? mediaPath, string? draftSubtitlePath, out string message)
        => TransubHandoff.TryOpen(_settings, mediaPath, out message);
}
