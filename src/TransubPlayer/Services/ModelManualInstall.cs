using System.Diagnostics;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Opens folders / pages and imports on-disk ASR packs for manual model setup.</summary>
internal static class ModelManualInstall
{
    public static string ResolveModelsRoot(AppSettings settings)
        => AsrModelStore.ResolveModelsRoot(settings);

    public static string AsrModelsRoot(AppSettings settings)
        => Path.Combine(ResolveModelsRoot(settings), "asr");

    public static string AsrModelDir(AppSettings settings, string modelId)
        => Path.Combine(AsrModelsRoot(settings), modelId);

    public static string AsrModelPath(AppSettings settings, string modelId)
    {
        var id = ModelPicker.InstallTarget(modelId);
        if (string.Equals(id, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase))
            return AsrModelStore.TurboPath(ResolveModelsRoot(settings));
        return AsrModelDir(settings, id);
    }

    public static string BuildInstructions(PresetGapReport report, AppSettings settings)
    {
        var lines = new List<string>
        {
            Loc.Format("Settings.ManualInstall.Intro", report.PresetName),
            "",
        };

        foreach (var gap in report.Gaps)
        {
            lines.Add("· " + gap.Title + (string.IsNullOrWhiteSpace(gap.SizeHint) ? "" : $"（{gap.SizeHint}）"));
            switch (gap.Kind)
            {
                case PresetGapKind.AsrModel:
                    lines.Add("  " + Loc.Format("Settings.ManualInstall.AsrPath", AsrModelPath(settings, gap.Id)));
                    if (!string.IsNullOrWhiteSpace(AsrModelCatalog.HfRepo(gap.Id)))
                        lines.Add("  " + Loc.Get("Settings.ManualInstall.AsrPageHint"));
                    else
                        lines.Add("  " + Loc.Get("Settings.ManualInstall.AsrCopyHint"));
                    break;
                case PresetGapKind.GgufModel:
                    var spec = TranslateModels.ResolveSpec(settings.TranslateModelId);
                    lines.Add("  " + Loc.Format("Settings.ManualInstall.GgufPath", Path.Combine(AppPaths.ResolveAdvancedLlmModelsDir(settings), spec.FileName)));
                    break;
                case PresetGapKind.LlamaRuntime:
                    lines.Add("  " + Loc.Format("Settings.ManualInstall.LlamaPath", AppPaths.ResolveAdvancedLlmRuntimeDir(settings)));
                    lines.Add("  " + Loc.Get("Settings.ManualInstall.LlamaHint"));
                    break;
            }

            if (!string.IsNullOrWhiteSpace(gap.Detail))
                lines.Add("  " + gap.Detail);
            lines.Add("");
        }

        lines.Add(Loc.Get("Settings.ManualInstall.AfterPlace"));
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }

    public static void OpenGuidance(PresetGapReport report, AppSettings settings)
    {
        var opened = false;
        foreach (var gap in report.Gaps)
        {
            switch (gap.Kind)
            {
                case PresetGapKind.GgufModel:
                    ManagedLlmInstaller.OpenModelsFolder(settings);
                    ManagedLlmInstaller.OpenGgufDownloadPage(settings.HfEndpoint, settings.TranslateModelId);
                    opened = true;
                    break;
                case PresetGapKind.LlamaRuntime:
                    OpenPath(AppPaths.ResolveAdvancedLlmRuntimeDir(settings));
                    TryOpenUrl(ManagedLlmCatalog.LlamaServerZipUrl);
                    opened = true;
                    break;
                case PresetGapKind.AsrModel:
                    OpenAsrModelFolder(settings, gap.Id);
                    TryOpenAsrModelPage(gap.Id, settings.HfEndpoint);
                    opened = true;
                    break;
            }
        }

        if (!opened)
            OpenAsrModelsRoot(settings);
    }

    public static void OpenAsrModelsRoot(AppSettings settings)
        => OpenPath(AsrModelsRoot(settings));

    public static void OpenAsrModelFolder(AppSettings settings, string modelId)
        => OpenPath(AsrModelDir(settings, modelId));

    public static void OpenLlamaRuntimeFolder(AppSettings? settings = null)
        => OpenPath(AppPaths.ResolveAdvancedLlmRuntimeDir(settings));

    public static void TryOpenAsrModelPage(string modelId, string? hfEndpoint)
    {
        var repo = AsrModelCatalog.HfRepo(modelId);
        if (string.IsNullOrWhiteSpace(repo)) return;
        TryOpenUrl(ManagedLlmCatalog.ApplyHfMirror($"https://huggingface.co/{repo}", hfEndpoint));
    }

    /// <summary>Copies a complete ASR folder into <c>models/asr/{id}</c>. Returns installed model id.</summary>
    public static string ImportAsrFromFolder(string sourceDir, AppSettings settings, IEnumerable<string> candidateIds)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            throw new InvalidOperationException(Loc.Get("Settings.ManualInstall.ImportMissingFolder"));

        var id = DetectAsrModelId(sourceDir, candidateIds)
            ?? throw new InvalidOperationException(Loc.Get("Settings.ManualInstall.ImportUnrecognized"));

        var dest = AsrModelDir(settings, id);
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        CopyDirectory(sourceDir, dest);

        if (!AsrModelStore.IsInstalled(ResolveModelsRoot(settings), id))
            throw new InvalidOperationException(Loc.Format("Settings.ManualInstall.ImportIncomplete", AsrModelCatalog.DisplayName(id)));

        return id;
    }

    public static string? DetectAsrModelId(string sourceDir, IEnumerable<string> candidateIds)
    {
        foreach (var id in candidateIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (AsrModelIntegrity.IsComplete(sourceDir, id))
                return id;
        }

        var name = Path.GetFileName(sourceDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(name)
            && candidateIds.Contains(name, StringComparer.OrdinalIgnoreCase)
            && AsrModelIntegrity.IsComplete(sourceDir, name))
            return name;

        return null;
    }

    public static IEnumerable<string> AsrCandidates(PresetGapReport report)
        => report.Gaps
            .Where(g => g.Kind == PresetGapKind.AsrModel)
            .Select(g => g.Id)
            .Append(report.PreferredAsr)
            .Append(report.FallbackAsr)
            .Concat(ModelPicker.Selectable)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static void OpenPath(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private static void TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            if (AsrModelIntegrity.IsIncompletePath(file)) continue;
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
