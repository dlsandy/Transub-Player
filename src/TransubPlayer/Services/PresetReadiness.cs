using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal enum PresetGapKind
{
    AsrModel,
    LlamaRuntime,
    GgufModel,
}

internal enum PresetSetupChoice
{
    AutoInstall,
    ManualInstall,
    UseFallback,
    Cancel,
}

internal sealed record PresetGap(
    PresetGapKind Kind,
    string Id,
    string Title,
    string Detail,
    string? SizeHint,
    bool CanAutoInstall);

internal sealed class PresetGapReport
{
    public required string PresetId { get; init; }
    public required string PresetName { get; init; }
    public required string PreferredAsr { get; init; }
    public required string FallbackAsr { get; init; }
    public required IReadOnlyList<PresetGap> Gaps { get; init; }
    public bool HasGaps => Gaps.Count > 0;
    public bool CanAutoInstallAny => Gaps.Any(g => g.CanAutoInstall);

    public string SummaryLine()
    {
        if (!HasGaps) return "依赖已就绪";
        return string.Join("；", Gaps.Select(g => g.Title));
    }

    public string DialogBody()
    {
        var lines = new List<string>
        {
            Loc.Get("Main.Deps.DialogIntro"),
            "",
        };
        foreach (var gap in Gaps)
        {
            var size = string.IsNullOrWhiteSpace(gap.SizeHint) ? "" : $"（{gap.SizeHint}）";
            lines.Add("· " + gap.Title + size);
            if (!string.IsNullOrWhiteSpace(gap.Detail))
                lines.Add("  " + gap.Detail);
        }

        lines.Add("");
        lines.Add(Loc.Get("Main.Deps.DialogFallback"));
        lines.Add("");
        lines.Add(Loc.Get("Main.Deps.DialogHint"));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>ASR model metadata for readiness and install prompts.</summary>
internal static class AsrModelCatalog
{
    /// <summary>Directory / primary weight must reach this fraction of <see cref="AsrModelInfo.SizeHintMb"/> to count as complete.</summary>
    public const double CompleteSizeRatio = 0.88;

    private static readonly Dictionary<string, AsrModelInfo> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [ModelPicker.Turbo] = new(ModelPicker.Turbo, "whisper turbo", "约 1 GB", SizeHintMb: 1000, HfRepo: null),
    };

    public static AsrModelInfo? Get(string id)
        => Map.TryGetValue(id, out var info) ? info : null;

    public static string DisplayName(string id)
        => Get(id)?.DisplayName ?? id;

    public static string SizeHint(string id)
        => Get(id)?.SizeHint ?? "";

    /// <summary>Minimum total bytes (excluding Hub incomplete residue) for a complete install; 0 if unknown.</summary>
    public static long MinCompleteBytes(string id)
    {
        var mb = Get(id)?.SizeHintMb ?? 0;
        if (mb <= 0) return 0;
        return (long)(mb * 1024L * 1024L * CompleteSizeRatio);
    }

    public static string? HfRepo(string id)
        => Get(id)?.HfRepo;
}

internal sealed record AsrModelInfo(
    string Id,
    string DisplayName,
    string SizeHint,
    int SizeHintMb,
    string? HfRepo = null);

/// <summary>Probes preferred ASR / Hy-MT deps (no scene-preset ASR chains).</summary>
internal static class PresetReadiness
{
    private static RuntimePacks? _livePacks;

    private sealed record DiskProbeContext(
        string ModelsRoot,
        RuntimePacks Packs,
        bool LlamaOk,
        bool GgufOk);

    private static DiskProbeContext? _diskProbe;
    private static string? _diskProbeKey;
    private static DateTime _diskProbeUtc;

    public static void UpdateLivePacks(RuntimePacks packs)
    {
        _livePacks = packs;
        InvalidateDiskProbe();
    }

    public static void ClearLivePacks()
    {
        _livePacks = null;
        InvalidateDiskProbe();
    }

    public static void InvalidateDiskProbe()
    {
        _diskProbe = null;
        _diskProbeKey = null;
    }

    private static DiskProbeContext GetDiskProbe(AppSettings settings)
    {
        var key = (settings.ModelsPath ?? "") + "\0"
                  + (settings.AdvancedLlmPath ?? "") + "\0"
                  + settings.TranslateEnabled + "\0"
                  + ModelPicker.Normalize(settings.AsrModel) + "\0"
                  + TranslateModels.Normalize(settings.TranslateModelId);
        if (_diskProbeKey == key
            && _diskProbe is not null
            && (DateTime.UtcNow - _diskProbeUtc).TotalSeconds < 60)
        {
            return _diskProbe;
        }

        var modelsRoot = AsrModelStore.ResolveModelsRoot(settings);
        var packs = _livePacks ?? RuntimePacks.FromDisk(modelsRoot);
        var llamaOk = ManagedLlmInstaller.HasLlamaRuntime(settings);
        var probe = new DiskProbeContext(
            modelsRoot,
            packs,
            llamaOk,
            ManagedLlmInstaller.HasPreferredGguf(settings));
        _diskProbe = probe;
        _diskProbeKey = key;
        _diskProbeUtc = DateTime.UtcNow;
        return probe;
    }

    public static PresetGapReport AnalyzeDisk(AppSettings settings, bool wantsMt)
    {
        var probe = GetDiskProbe(settings);
        var translateReady = !wantsMt || (probe.LlamaOk && probe.GgufOk);
        return Analyze(
            settings.AsrModel,
            probe.ModelsRoot,
            probe.Packs,
            wantsMt,
            translateReady,
            llamaRuntimePresent: probe.LlamaOk,
            preferredGgufPresent: probe.GgufOk,
            translateModelId: settings.TranslateModelId,
            mtModelsDir: AppPaths.ResolveAdvancedLlmModelsDir(settings));
    }

    public static PresetGapReport Analyze(
        string? preferredAsrModel,
        string modelsRoot,
        RuntimePacks packs,
        bool wantsMt,
        bool translateReady,
        bool llamaRuntimePresent,
        bool preferredGgufPresent,
        string? translateModelId = null,
        string? mtModelsDir = null)
    {
        var preferred = ModelPicker.InstallTarget(preferredAsrModel);
        var gaps = new List<PresetGap>();

        if (!IsAsrReady(preferred, packs))
            AddAsrGaps(gaps, preferred, modelsRoot, packs);

        if (wantsMt && !translateReady)
        {
            if (!llamaRuntimePresent)
            {
                gaps.Add(new PresetGap(
                    PresetGapKind.LlamaRuntime,
                    "llama-server",
                    "翻译运行时 llama-server",
                    "可自动下载 Vulkan 版（约 32 MB），或使用已安装的 Transub advanced-llm。",
                    "约 32 MB",
                    CanAutoInstall: true));
            }

            if (!preferredGgufPresent)
            {
                var gguf = TranslateModels.ResolveSpec(translateModelId);
                var dir = string.IsNullOrWhiteSpace(mtModelsDir)
                    ? AppPaths.ResolveAdvancedLlmModelsDir()
                    : mtModelsDir;
                var detail = $"Google TranslateGemma 4B Q4（{gguf.SizeHint}）。下载后放到 {dir}。";
                gaps.Add(new PresetGap(
                    PresetGapKind.GgufModel,
                    gguf.Id,
                    "翻译模型 " + gguf.DisplayName,
                    detail,
                    gguf.SizeHint,
                    CanAutoInstall: true));
            }
        }

        return new PresetGapReport
        {
            PresetId = ModelPicker.Normalize(preferredAsrModel),
            PresetName = AsrModelCatalog.DisplayName(preferred),
            PreferredAsr = preferred,
            FallbackAsr = ModelPicker.Turbo,
            Gaps = gaps,
        };
    }

    private static void AddAsrGaps(
        List<PresetGap> gaps,
        string modelId,
        string modelsRoot,
        RuntimePacks packs)
    {
        var name = AsrModelCatalog.DisplayName(modelId);
        var size = AsrModelCatalog.SizeHint(modelId);
        var modelDir = Path.Combine(modelsRoot, "asr", modelId);
        var partial = AsrModelIntegrity.IsPartiallyPresent(modelDir, modelId);

        if (!packs.IsAsrInstalled(modelId)
            && !(string.Equals(modelId, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase) && packs.TurboInstalled))
        {
            gaps.Add(new PresetGap(
                PresetGapKind.AsrModel,
                modelId,
                partial ? Loc.Format("Settings.Presets.PartialAsr", name) : "识别模型 " + name,
                partial
                    ? Loc.Get("Settings.Presets.PartialAsrDetail")
                    : "可通过识别引擎自动下载。",
                string.IsNullOrWhiteSpace(size) ? null : size,
                CanAutoInstall: true));
        }
    }

    private static bool IsAsrReady(string modelId, RuntimePacks packs)
        => AsrQualities.IsUsable(modelId, packs);
}
