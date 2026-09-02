using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal enum TransubHandoffMode
{
    Empty,
    Queue,
    EditDraft,
}

internal sealed record TransubHandoffRequest(
    string? MediaPath,
    string? SourceLanguage = null,
    string? TranslateTarget = null,
    string? ContentProfile = null);

/// <summary>
/// Opens Transub for production subtitles via <c>transub://</c> (preferred) or CLI fallback.
/// Does not run Transub's ASR engine inside Player.
/// </summary>
internal static class TransubHandoff
{
    public const string ProtocolScheme = "transub";

    private static readonly JsonSerializerOptions HandoffJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string? FindExe(AppSettings settings)
        => TransubInstall.FindExe(settings);

    public static bool TryOpen(AppSettings settings, string? mediaPath, out string message)
        => TryOpen(settings, new TransubHandoffRequest(mediaPath), out message);

    public static bool TryOpen(AppSettings settings, TransubHandoffRequest request, out string message)
    {
        var mediaPath = NormalizeExistingFile(request.MediaPath);
        var draftSub = mediaPath is null
            ? null
            : FindBestDraftSub(mediaPath, request.TranslateTarget ?? settings.TranslateTarget);
        var mode = ResolveMode(mediaPath, draftSub);

        string? manifestPath = null;
        if (mode == TransubHandoffMode.EditDraft && mediaPath is not null && draftSub is not null)
            manifestPath = TryWriteManifest(mediaPath, draftSub, request, settings);

        var uri = BuildProtocolUri(mode, mediaPath, draftSub, request, settings, manifestPath);

        // Prefer transub:// when registered (portable exe path / multi-install safe).
        if (!string.IsNullOrWhiteSpace(uri) && IsProtocolRegistered())
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true,
                });
                message = StatusForMode(mode);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("transub:// launch failed: " + ex.Message);
            }
        }

        var exe = FindExe(settings);
        if (exe is null)
        {
            message = Loc.Get("Main.Transub.NotFound");
            return false;
        }

        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
        };

        switch (mode)
        {
            case TransubHandoffMode.EditDraft:
                start.ArgumentList.Add($"--edit-sub={draftSub}");
                start.ArgumentList.Add($"--edit-video={mediaPath}");
                break;
            case TransubHandoffMode.Queue:
                // Transub only imports media via --files= (bare paths are ignored).
                start.ArgumentList.Add($"--files={mediaPath}");
                break;
        }

        Process.Start(start);
        message = StatusForMode(mode);
        return true;
    }

    internal static string StatusForMode(TransubHandoffMode mode)
        => mode switch
        {
            TransubHandoffMode.EditDraft => Loc.Get("Main.Transub.OpenedDraft"),
            TransubHandoffMode.Queue => Loc.Get("Main.Transub.Opened"),
            _ => Loc.Get("Main.Transub.OpenedEmpty"),
        };

    internal static TransubHandoffMode ResolveMode(string? mediaPath, string? draftSub)
    {
        if (!string.IsNullOrWhiteSpace(mediaPath) && !string.IsNullOrWhiteSpace(draftSub))
            return TransubHandoffMode.EditDraft;
        if (!string.IsNullOrWhiteSpace(mediaPath))
            return TransubHandoffMode.Queue;
        return TransubHandoffMode.Empty;
    }

    /// <summary>
    /// Prefer translated preview, then dual, source ASR, then display track.
    /// </summary>
    internal static string? FindBestDraftSub(string mediaPath, string? translateTarget)
    {
        foreach (var candidate in DraftCandidates(mediaPath, translateTarget))
        {
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 32)
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // ignore unreadable candidate
            }
        }

        return null;
    }

    internal static string BuildProtocolUri(
        TransubHandoffMode mode,
        string? mediaPath,
        string? draftSub,
        TransubHandoffRequest request,
        AppSettings settings,
        string? manifestPath = null)
    {
        var modeToken = mode switch
        {
            TransubHandoffMode.EditDraft => "edit",
            TransubHandoffMode.Queue => "queue",
            _ => "open",
        };

        var sb = new StringBuilder();
        sb.Append(ProtocolScheme).Append("://handoff?mode=").Append(modeToken);
        if (!string.IsNullOrWhiteSpace(mediaPath))
            sb.Append("&media=").Append(Uri.EscapeDataString(mediaPath));
        if (!string.IsNullOrWhiteSpace(draftSub))
            sb.Append("&sub=").Append(Uri.EscapeDataString(draftSub));

        var src = string.IsNullOrWhiteSpace(request.SourceLanguage)
            ? settings.SourceLanguage
            : request.SourceLanguage;
        var tgt = string.IsNullOrWhiteSpace(request.TranslateTarget)
            ? settings.TranslateTarget
            : request.TranslateTarget;
        var profile = string.IsNullOrWhiteSpace(request.ContentProfile)
            ? "general"
            : request.ContentProfile;

        if (!string.IsNullOrWhiteSpace(src))
            sb.Append("&src=").Append(Uri.EscapeDataString(src));
        if (!string.IsNullOrWhiteSpace(tgt))
            sb.Append("&tgt=").Append(Uri.EscapeDataString(tgt));
        if (!string.IsNullOrWhiteSpace(profile))
            sb.Append("&profile=").Append(Uri.EscapeDataString(profile));
        if (!string.IsNullOrWhiteSpace(manifestPath))
            sb.Append("&manifest=").Append(Uri.EscapeDataString(manifestPath));

        return sb.ToString();
    }

    /// <summary>True when Windows has a <c>transub://</c> handler (HKCU or HKLM).</summary>
    public static bool IsProtocolRegistered()
    {
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProtocolScheme);
            if (hkcu is not null) return true;
        }
        catch
        {
            // ignore
        }

        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + ProtocolScheme);
            if (hklm is not null) return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static IEnumerable<string> DraftCandidates(string mediaPath, string? translateTarget)
    {
        yield return PreviewPaths.TranslatedPreviewSrt(mediaPath, translateTarget);
        // Also try zh when target is a Chinese script variant that may have written .zh.preview.srt historically.
        if (!string.Equals(TranslateTargets.FileSuffix(translateTarget), "zh", StringComparison.OrdinalIgnoreCase))
            yield return PreviewPaths.ZhSrt(mediaPath);
        yield return PreviewPaths.DualSrt(mediaPath);
        yield return PreviewPaths.SourceSrt(mediaPath);
        yield return PreviewPaths.DisplaySrt(mediaPath);
    }

    private static string? TryWriteManifest(
        string mediaPath,
        string draftSub,
        TransubHandoffRequest request,
        AppSettings settings)
    {
        try
        {
            var dir = PreviewPaths.OutDir(mediaPath);
            Directory.CreateDirectory(dir);
            var payload = new
            {
                version = 1,
                source = "transub-player",
                mediaPath = Path.GetFullPath(mediaPath),
                sourceLanguage = string.IsNullOrWhiteSpace(request.SourceLanguage)
                    ? settings.SourceLanguage
                    : request.SourceLanguage,
                translateTarget = string.IsNullOrWhiteSpace(request.TranslateTarget)
                    ? settings.TranslateTarget
                    : request.TranslateTarget,
                contentProfile = string.IsNullOrWhiteSpace(request.ContentProfile)
                    ? "general"
                    : request.ContentProfile,
                draftSub = Path.GetFullPath(draftSub),
                draftSource = ExistingOrNull(PreviewPaths.SourceSrt(mediaPath)),
                draftTranslated = ExistingOrNull(
                    PreviewPaths.TranslatedPreviewSrt(mediaPath, request.TranslateTarget ?? settings.TranslateTarget)),
                draftDual = ExistingOrNull(PreviewPaths.DualSrt(mediaPath)),
                createdAt = DateTime.UtcNow.ToString("O"),
            };
            var path = Path.Combine(dir, "player-handoff.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload, HandoffJson));
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExistingOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
