using TransubPlayer.Services;

namespace TransubPlayer.Platform;

/// <summary>Tray, file association, single-instance, URL scheme — shell-specific.</summary>
internal interface IOsIntegration
{
    void EnsureSingleInstance(Action<IReadOnlyList<string>> onOpenFiles);

    void RegisterFileAssociations();

    FileAssociationApplyResult ApplyPlaybackAssociations(AppSettings settings);

    void RegisterUrlScheme();

    IDisposable? CreateTrayIcon(Action showMain, Action quit);

    bool TryHandoffToTransub(string? mediaPath, string? draftSubtitlePath, out string message);
}
