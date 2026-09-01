using TransubPlayer.Localization;



namespace TransubPlayer.Services;



internal sealed record ModelAssetSummary(

    string AsrStatus,

    string GgufStatus,

    bool HasPreferredAsr,

    bool HasGguf,

    string PreferredAsrId);



/// <summary>Summarizes and removes on-disk ASR / Hy-MT GGUF.</summary>

internal static class PresetAssetManager

{

    public static ModelAssetSummary SummarizeModels(AppSettings settings)

    {

        var engine = EngineLocator.Find(settings);

        var modelsRoot = EngineLocator.ResolveModelsRoot(settings, engine);

        var packs = RuntimePacks.FromDisk(modelsRoot);

        var preferred = ModelPicker.InstallTarget(settings.AsrModel);

        var asr = DescribeAsr(modelsRoot, packs, preferred);

        var hasAsr = IsInstalled(packs, preferred, modelsRoot);

        var hasGguf = ManagedLlmInstaller.HasPreferredGguf(settings);

        var gguf = hasGguf

            ? Loc.Get("Settings.PackStatus.GgufReady")

            : Loc.Get("Settings.PackStatus.GgufMissing");

        return new ModelAssetSummary(asr, gguf, hasAsr, hasGguf, preferred);

    }



    public static void DeleteAsr(AppSettings settings, string modelId, Action<string> log)
    {
        var id = ModelPicker.InstallTarget(modelId);
        var modelsRoot = AsrModelStore.ResolveModelsRoot(settings);

        if (string.Equals(id, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase))
        {
            var path = AsrModelStore.TurboPath(modelsRoot);
            if (!File.Exists(path))
                return;
            File.Delete(path);
            log("已删除 ASR " + id);
            return;
        }

        var dir = Path.Combine(modelsRoot, "asr", id);
        if (!Directory.Exists(dir))
            return;

        Directory.Delete(dir, recursive: true);
        log("已删除 ASR " + id);
    }



    /// <summary>Legacy alias — deletes turbo.</summary>

    public static void DeleteTurbo(AppSettings settings, Action<string> log)

        => DeleteAsr(settings, ModelPicker.Turbo, log);



    public static void DeleteGguf(AppSettings settings, Action<string> log)

    {

        var root = AppPaths.ResolveAdvancedLlmInstallRoot(settings);
        if (!Directory.Exists(root))
        {
            root = AppPaths.ResolveAdvancedLlmRoot(settings);
            if (root is null) return;
        }

        var models = Path.Combine(root, "models");

        if (!Directory.Exists(models)) return;



        var modelId = TranslateModels.Normalize(settings.TranslateModelId);

        var preferred = TranslateModels.ResolveSpec(modelId);

        TryDeleteFile(Path.Combine(models, preferred.FileName), log);



        try

        {

            foreach (var path in Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly))

            {

                if (!TranslateModels.MatchesFamily(path, modelId))

                    continue;

                TryDeleteFile(path, log);

            }

        }

        catch

        {

            // ignore

        }

    }



    private static void TryDeleteFile(string path, Action<string> log)

    {

        if (!File.Exists(path)) return;

        File.Delete(path);

        log("已删除 GGUF " + Path.GetFileName(path));

    }



    private static bool IsInstalled(RuntimePacks packs, string modelId, string modelsRoot)

    {

        if (string.Equals(modelId, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase))

            return packs.TurboInstalled || EngineLocator.IsAsrInstalled(modelsRoot, modelId);

        return packs.IsAsrInstalled(modelId) || EngineLocator.IsAsrInstalled(modelsRoot, modelId);

    }



    private static string DescribeAsr(string modelsRoot, RuntimePacks packs, string modelId)

    {

        if (IsInstalled(packs, modelId, modelsRoot))

            return Loc.Format("Settings.PackStatus.AsrReady", AsrModelCatalog.DisplayName(modelId));



        var dir = Path.Combine(modelsRoot, "asr", modelId);

        if (string.Equals(modelId, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)
            && AsrModelStore.IsTurboPartial(modelsRoot))
            return Loc.Format("Settings.PackStatus.AsrPartial", AsrModelCatalog.DisplayName(modelId));

        if (AsrModelIntegrity.IsPartiallyPresent(dir, modelId))

            return Loc.Format("Settings.PackStatus.AsrPartial", AsrModelCatalog.DisplayName(modelId));



        return Loc.Format("Settings.PackStatus.AsrMissing", AsrModelCatalog.DisplayName(modelId));

    }

}


