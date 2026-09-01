using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Installs missing runtime dependencies (ASR via engine, GPU, llama-server, GGUF).</summary>
internal sealed class PresetDependencyInstaller
{
    private readonly AppSettings _settings;
    private readonly Action<string> _status;
    private readonly Action<string> _log;

    public PresetDependencyInstaller(AppSettings settings, Action<string> status, Action<string> log)
    {
        _settings = settings;
        _status = status;
        _log = log;
    }

    public async Task InstallAsync(
        PresetGapReport report,
        AsrPipeline engine,
        CancellationToken ct)
    {
        foreach (var gap in report.Gaps.Where(g => g.CanAutoInstall))
        {
            ct.ThrowIfCancellationRequested();
            switch (gap.Kind)
            {
                case PresetGapKind.AsrModel:
                    await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
                    _status(Loc.Format("Main.Deps.InstallingGap", gap.Title));
                    _log("下载 ASR " + gap.Id);
                    await engine.DownloadModelsAsync([gap.Id], ct).ConfigureAwait(false);
                    break;
                case PresetGapKind.LlamaRuntime:
                    _status(Loc.Format("Main.Deps.InstallingGap", gap.Title));
                    await ManagedLlmInstaller.EnsureLlamaRuntimeAsync(_settings, _status, _log, ct).ConfigureAwait(false);
                    break;
                case PresetGapKind.GgufModel:
                    _status(Loc.Format("Main.Deps.InstallingGap", gap.Title));
                    await ManagedLlmInstaller.EnsureGgufAsync(
                            _settings.HfEndpoint, _status, _log, ct, _settings.TranslateModelId, _settings)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }

    public void OpenManualGuidance(PresetGapReport report)
        => ModelManualInstall.OpenGuidance(report, _settings);
}
