using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>First-run mpv / ASR model / translation setup.</summary>
internal static class SetupWizard
{
    public static bool ShouldShow(AppSettings settings)
    {
        if (!UserTips.ShouldShow(settings, UserTips.SetupWizard))
            return false;

        if (!UserTips.ShouldShow(settings, UserTips.StartupChecklist))
            return false;

        if (IsReady(settings))
            return false;

        return true;
    }

    /// <summary>Core playback + live subtitles (mpv + whisper turbo).</summary>
    public static bool IsCoreReady(AppSettings settings)
    {
        if (MpvLocator.Find() is null)
            return false;
        var modelsRoot = AsrModelStore.ResolveModelsRoot(settings);
        return AsrModelStore.IsTurboInstalled(modelsRoot);
    }

    public static bool IsReady(AppSettings settings)
        => IsCoreReady(settings) && (!settings.TranslateEnabled || IsTranslationReady(settings));

    public static bool IsTranslationReady(AppSettings settings)
        => ManagedLlmInstaller.HasLlamaRuntime(settings)
           && ManagedLlmInstaller.HasPreferredGguf(settings);

    public static bool NeedsInstall(AppSettings settings)
    {
        var detect = Detect(settings);
        if (!detect.HasMpv || !detect.HasAsrModel)
            return true;
        if (settings.TranslateEnabled && (!detect.HasLlamaRuntime || !detect.HasGguf))
            return true;
        return false;
    }

    public static bool CanProceedFromInstall(AppSettings settings)
        => IsReady(settings);

    public static SetupDetectResult Detect(AppSettings settings)
    {
        var modelsRoot = AsrModelStore.ResolveModelsRoot(settings);
        return new SetupDetectResult(
            HasMpv: MpvLocator.Find() is not null,
            HasAsrModel: AsrModelStore.IsTurboInstalled(modelsRoot),
            HasLlamaRuntime: ManagedLlmInstaller.HasLlamaRuntime(settings),
            HasGguf: ManagedLlmInstaller.HasPreferredGguf(settings),
            ModelsRoot: modelsRoot,
            AdvancedLlmRoot: AppPaths.ResolveAdvancedLlmInstallRoot(settings));
    }

    /// <summary>Download mpv, whisper turbo, and optional MT deps in one pass.</summary>
    public static async Task EnsureAllComponentsAsync(
        AppSettings settings,
        Action<string> status,
        Action<string> log,
        CancellationToken ct)
    {
        var detect = Detect(settings);

        if (!detect.HasMpv)
        {
            status(Loc.Get("Wizard.Install.Step.Mpv"));
            await EnsureMpvAsync(log, ct).ConfigureAwait(false);
        }

        detect = Detect(settings);
        if (!detect.HasAsrModel)
        {
            status(Loc.Get("Wizard.Install.Step.Asr"));
            await EnsureAsrModelAsync(settings, status, log, ct).ConfigureAwait(false);
        }

        if (settings.TranslateEnabled)
        {
            detect = Detect(settings);
            if (!detect.HasLlamaRuntime || !detect.HasGguf)
            {
                status(Loc.Get("Wizard.Install.Step.Mt"));
                await EnsureTranslationAsync(settings, status, log, ct).ConfigureAwait(false);
            }
        }

        EngineLocator.Invalidate();
        PresetReadiness.InvalidateDiskProbe();
    }

    public static async Task EnsureMpvAsync(Action<string> log, CancellationToken ct)
    {
        await FirstRunHelp.RunFetchMpvAsync(log, ct).ConfigureAwait(false);
    }

    public static async Task EnsureAsrModelAsync(
        AppSettings settings,
        Action<string> status,
        Action<string> log,
        CancellationToken ct)
    {
        var root = AsrModelStore.ResolveModelsRoot(settings);
        await AsrModelStore.EnsureTurboAsync(root, settings.HfEndpoint, status, log, ct).ConfigureAwait(false);
    }

    public static async Task EnsureTranslationAsync(
        AppSettings settings,
        Action<string> status,
        Action<string> log,
        CancellationToken ct)
    {
        await ManagedLlmInstaller.EnsureLlamaRuntimeAsync(settings, status, log, ct).ConfigureAwait(false);
        await ManagedLlmInstaller.EnsureGgufAsync(
                settings.HfEndpoint, status, log, ct, TranslateModels.TranslateGemma4B, settings)
            .ConfigureAwait(false);
    }

    public static void MarkComplete(AppSettings settings)
    {
        UserTips.Dismiss(settings, UserTips.SetupWizard, save: false);
        UserTips.Dismiss(settings, UserTips.StartupChecklist, save: false);
        settings.Save();
    }

    public static FileAssociationApplyResult ApplyPlaybackAssociations(AppSettings settings)
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
}

internal sealed record SetupDetectResult(
    bool HasMpv,
    bool HasAsrModel,
    bool HasLlamaRuntime,
    bool HasGguf,
    string ModelsRoot,
    string AdvancedLlmRoot);
