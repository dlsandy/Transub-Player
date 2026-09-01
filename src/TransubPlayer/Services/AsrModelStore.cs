using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>On-disk layout and download for embedded Whisper GGML models.</summary>
internal static class AsrModelStore
{
    public const string TurboFileName = "ggml-large-v3-turbo.bin";

    /// <summary>Whisper.net v4 classic turbo GGML on Hugging Face.</summary>
    public const string TurboGgmlUrl =
        "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/classic/ggml-large-v3-turbo.bin";

    public static string ResolveModelsRoot(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelsPath))
            return Path.GetFullPath(settings.ModelsPath.Trim());
        return Path.Combine(AppPaths.AppDataDir, "models");
    }

    public static string TurboPath(string modelsRoot)
        => Path.Combine(modelsRoot, "asr", TurboFileName);

    public static bool IsTurboPartial(string modelsRoot)
    {
        try
        {
            var path = TurboPath(modelsRoot);
            if (!File.Exists(path)) return false;
            if (IsTurboInstalled(modelsRoot)) return false;
            return new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsTurboInstalled(string modelsRoot)
    {
        try
        {
            var path = TurboPath(modelsRoot);
            if (!File.Exists(path)) return false;
            return new FileInfo(path).Length >= AsrModelCatalog.MinCompleteBytes(ModelPicker.Turbo);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsInstalled(string modelsRoot, string modelId)
    {
        var id = ModelPicker.InstallTarget(modelId);
        return string.Equals(id, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)
            && IsTurboInstalled(modelsRoot);
    }

    public static Task EnsureTurboAsync(Action<string>? status, Action<string>? log, CancellationToken ct)
    {
        var settings = AppSettings.Load();
        return EnsureTurboAsync(ResolveModelsRoot(settings), settings.HfEndpoint, status, log, ct);
    }

    public static async Task EnsureTurboAsync(
        string modelsRoot,
        string? hfEndpoint,
        Action<string>? status,
        Action<string>? log,
        CancellationToken ct)
    {
        if (IsTurboInstalled(modelsRoot))
            return;

        Directory.CreateDirectory(Path.Combine(modelsRoot, "asr"));
        var dest = TurboPath(modelsRoot);
        status?.Invoke(Loc.Format("Main.Bootstrap.DownloadingModelNamed", AsrModelCatalog.DisplayName(ModelPicker.Turbo)));

        var url = ManagedLlmCatalog.ApplyHfMirror(TurboGgmlUrl, hfEndpoint);
        var minBytes = AsrModelCatalog.MinCompleteBytes(ModelPicker.Turbo);
        log?.Invoke("下载 GGML " + ModelPicker.Turbo + " · " + url);

        await ModelDownloadActivity.RunAsync(
            token => ManagedLlmInstaller.DownloadFileAsync(url, dest, status, token, minBytes),
            ct).ConfigureAwait(false);

        if (!IsTurboInstalled(modelsRoot))
            throw new InvalidOperationException("ASR 模型下载未完成。");
    }
}
