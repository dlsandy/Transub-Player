using System.IO.Compression;
using System.Net.Http;

namespace TransubPlayer.Services;

internal sealed record ManagedGgufSpec(
    string Id,
    string DisplayName,
    string FileName,
    string GgufUrl,
    string SizeHint,
    int SizeHintMb);

/// <summary>Lightweight GGUF + llama-server catalog (Player default: TranslateGemma 4B Q4).</summary>
internal static class ManagedLlmCatalog
{
    /// <summary>Latest llama.cpp release tag that ships win-vulkan-x64 (pinned at implement time).</summary>
    public const string LlamaCppTag = "b10679";

    /// <summary>GGUF must reach this fraction of <see cref="ManagedGgufSpec.SizeHintMb"/> to count as complete.</summary>
    public const double GgufCompleteSizeRatio = 0.7;

    public static readonly ManagedGgufSpec TranslateGemma4B = new(
        TranslateModels.TranslateGemma4B,
        "TranslateGemma 4B Q4",
        "translategemma-4b-it-Q4_K_M.gguf",
        "https://huggingface.co/bullerwins/translategemma-4b-it-GGUF/resolve/main/translategemma-4b-it-Q4_K_M.gguf",
        "约 2.5 GB",
        SizeHintMb: 2490);

    /// <summary>Default download for wizard / one-click.</summary>
    public static ManagedGgufSpec PreferDefault() => TranslateGemma4B;

    public static long MinGgufBytes(ManagedGgufSpec spec)
        => (long)(spec.SizeHintMb * 1024L * 1024L * GgufCompleteSizeRatio);

    /// <summary>Fallback floor when accepting any *.gguf (unknown name).</summary>
    public static long AnyGgufMinBytes => MinGgufBytes(TranslateGemma4B);

    public static string LlamaServerZipUrl =>
        $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaCppTag}/llama-{LlamaCppTag}-bin-win-vulkan-x64.zip";

    public static string ApplyHfMirror(string url, string? hfEndpoint)
    {
        if (string.IsNullOrWhiteSpace(hfEndpoint)) return url;
        var ep = hfEndpoint.Trim().TrimEnd('/');
        if (url.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
            return ep + url["https://huggingface.co".Length..];
        if (url.StartsWith("http://huggingface.co/", StringComparison.OrdinalIgnoreCase))
            return ep + url["http://huggingface.co".Length..];
        return url;
    }
}

/// <summary>Downloads llama-server runtime and GGUF into the resolved advanced-llm dirs.</summary>
internal static class ManagedLlmInstaller
{
    public static bool HasLlamaRuntime(AppSettings settings)
    {
        var installRoot = AppPaths.ResolveAdvancedLlmInstallRoot(settings);
        if (HasLlamaRuntime(installRoot))
            return true;

        // Custom dir: only that tree counts so an empty chosen folder still gets installed.
        if (!string.IsNullOrWhiteSpace(settings.AdvancedLlmPath))
            return false;

        return HasLlamaRuntime(AppPaths.ResolveAdvancedLlmRoot(settings));
    }

    public static bool HasLlamaRuntime(string? root = null)
    {
        root ??= AppPaths.ResolveAdvancedLlmRoot();
        if (root is null) return false;
        var runtime = Path.Combine(root, "runtime");
        if (!Directory.Exists(runtime)) return false;
        if (File.Exists(Path.Combine(runtime, "llama-server.exe"))) return true;
        try
        {
            return Directory.EnumerateFiles(runtime, "llama-server.exe", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    public static bool HasPreferredGguf(AppSettings settings)
        => HasSelectedGguf(settings);

    /// <summary>
    /// True when the user-selected MT family GGUF is present.
    /// Checks the install root first; when no custom path is set, also accepts a scored Transub tree.
    /// </summary>
    public static bool HasSelectedGguf(AppSettings settings)
    {
        var modelId = settings.TranslateModelId;
        var installRoot = AppPaths.ResolveAdvancedLlmInstallRoot(settings);
        if (HasGgufForModel(installRoot, modelId))
            return true;

        if (!string.IsNullOrWhiteSpace(settings.AdvancedLlmPath))
            return false;

        var root = AppPaths.ResolveAdvancedLlmRoot(settings);
        if (root is null) return false;
        try
        {
            if (string.Equals(
                    Path.GetFullPath(root),
                    Path.GetFullPath(installRoot),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
            // fall through
        }

        return HasGgufForModel(root, modelId);
    }

    /// <summary>
    /// True when any TranslateGemma GGUF is present.
    /// Used by wizard completion when the default MT path still applies.
    /// </summary>
    public static bool HasAnyTranslationGguf(string? root = null)
    {
        root ??= AppPaths.ResolveAdvancedLlmRoot();
        if (root is null) return false;
        var models = Path.Combine(root, "models");
        if (!Directory.Exists(models)) return false;
        var minPreferred = ManagedLlmCatalog.MinGgufBytes(ManagedLlmCatalog.PreferDefault());
        foreach (var name in LlamaServerProcess.PreferredModels)
        {
            var p = Path.Combine(models, name);
            if (IsCompleteGguf(p, minPreferred) && IsTranslationGguf(p))
                return true;
        }

        try
        {
            return Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly)
                .Any(f => IsCompleteGguf(f, ManagedLlmCatalog.AnyGgufMinBytes) && IsTranslationGguf(f));
        }
        catch
        {
            return false;
        }
    }

    public static bool HasPreferredGguf(string? root = null)
        => HasAnyTranslationGguf(root);

    public static bool HasGgufForModel(string root, string? modelId)
    {
        var models = Path.Combine(root, "models");
        if (!Directory.Exists(models)) return false;
        var spec = TranslateModels.ResolveSpec(modelId);
        var minBytes = ManagedLlmCatalog.MinGgufBytes(spec);
        foreach (var name in TranslateModels.PreferredFilenames)
        {
            var p = Path.Combine(models, name);
            if (IsCompleteGguf(p, minBytes) && TranslateModels.MatchesFamily(p, modelId))
                return true;
        }

        try
        {
            return Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly)
                .Any(f => IsCompleteGguf(f, minBytes) && TranslateModels.MatchesFamily(f, modelId));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>TranslateGemma — not bare chat Instruct / Qwen models.</summary>
    public static bool IsTranslationGguf(string path)
    {
        var name = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(name)
               && name.Contains("translategemma", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsCompleteGguf(string path, long minBytes)
    {
        // Hub / installer leftovers (*.partial, *.incomplete) must not count as installed.
        if (string.IsNullOrWhiteSpace(path) || AsrModelIntegrity.IsIncompletePath(path))
            return false;
        return MeetsMinGgufSize(path, minBytes);
    }

    /// <summary>Size check only — used on the in-progress <c>.partial</c> file before rename.</summary>
    public static bool MeetsMinGgufSize(string path, long minBytes)
    {
        if (!File.Exists(path)) return false;
        try
        {
            return new FileInfo(path).Length >= Math.Max(minBytes, 1024L * 1024);
        }
        catch
        {
            return false;
        }
    }

    public static Task EnsureLlamaRuntimeAsync(
        Action<string> status,
        Action<string> log,
        CancellationToken ct)
        => EnsureLlamaRuntimeAsync(settings: null, status, log, ct);

    public static async Task EnsureLlamaRuntimeAsync(
        AppSettings? settings,
        Action<string> status,
        Action<string> log,
        CancellationToken ct)
    {
        if (settings is not null ? HasLlamaRuntime(settings) : HasLlamaRuntime())
        {
            status("翻译运行时已就绪");
            return;
        }

        await ModelDownloadActivity.RunAsync(async token =>
        {
            var root = AppPaths.ResolveAdvancedLlmInstallRoot(settings);
            var runtimeDir = AppPaths.ResolveAdvancedLlmRuntimeDir(settings);
            Directory.CreateDirectory(runtimeDir);
            var zipPath = Path.Combine(root, "llama-server-vulkan.zip");
            status("正在下载翻译运行时 llama-server（约 32 MB）…");
            log("下载 " + ManagedLlmCatalog.LlamaServerZipUrl);
            await DownloadFileAsync(ManagedLlmCatalog.LlamaServerZipUrl, zipPath, status, token, minCompleteBytes: 20_000_000)
                .ConfigureAwait(false);

            status("正在解压 llama-server…");
            var extractTemp = Path.Combine(root, "runtime-extract");
            if (Directory.Exists(extractTemp))
                Directory.Delete(extractTemp, recursive: true);
            Directory.CreateDirectory(extractTemp);
            ZipFile.ExtractToDirectory(zipPath, extractTemp, overwriteFiles: true);

            var exe = Directory.EnumerateFiles(extractTemp, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("压缩包内未找到 llama-server.exe。");
            var exeDir = Path.GetDirectoryName(exe)!;
            foreach (var file in Directory.EnumerateFiles(exeDir, "*", SearchOption.TopDirectoryOnly))
            {
                var dest = Path.Combine(runtimeDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            try { File.Delete(zipPath); } catch { /* ignore */ }
            try { Directory.Delete(extractTemp, recursive: true); } catch { /* ignore */ }

            if (!File.Exists(Path.Combine(runtimeDir, "llama-server.exe")))
                throw new InvalidOperationException("llama-server 安装失败。");
            status("翻译运行时已安装");
            log("llama-server → " + runtimeDir);
        }, ct).ConfigureAwait(false);
    }

    public static Task EnsureGgufAsync(
        string? hfEndpoint,
        Action<string> status,
        Action<string> log,
        CancellationToken ct)
        => EnsureGgufAsync(hfEndpoint, status, log, ct, modelId: null, settings: null);

    public static Task EnsureGgufAsync(
        string? hfEndpoint,
        Action<string> status,
        Action<string> log,
        CancellationToken ct,
        string? modelId)
        => EnsureGgufAsync(hfEndpoint, status, log, ct, modelId, settings: null);

    public static async Task EnsureGgufAsync(
        string? hfEndpoint,
        Action<string> status,
        Action<string> log,
        CancellationToken ct,
        string? modelId,
        AppSettings? settings)
    {
        var spec = string.IsNullOrWhiteSpace(modelId)
            ? ManagedLlmCatalog.PreferDefault()
            : TranslateModels.ResolveSpec(modelId);
        var root = AppPaths.ResolveAdvancedLlmInstallRoot(settings);
        if (HasGgufForModel(root, spec.Id))
        {
            status("翻译模型已就绪");
            return;
        }

        var modelsDir = AppPaths.ResolveAdvancedLlmModelsDir(settings);
        Directory.CreateDirectory(modelsDir);
        var dest = Path.Combine(modelsDir, spec.FileName);
        var minBytes = ManagedLlmCatalog.MinGgufBytes(spec);
        if (IsCompleteGguf(dest, minBytes))
        {
            status("翻译模型已就绪");
            return;
        }

        var url = ManagedLlmCatalog.ApplyHfMirror(spec.GgufUrl, hfEndpoint);
        await ModelDownloadActivity.RunAsync(async token =>
        {
            status($"正在下载翻译模型 {spec.DisplayName}（{spec.SizeHint}）…");
            log("下载 " + url);
            var partial = dest + ".partial";
            try
            {
                await DownloadFileAsync(url, partial, status, token, minCompleteBytes: minBytes).ConfigureAwait(false);
                // Do not call IsCompleteGguf on *.partial — that helper rejects the suffix as Hub residue.
                if (!MeetsMinGgufSize(partial, minBytes))
                {
                    var actual = File.Exists(partial)
                        ? DownloadProgressUi.FormatBytes(new FileInfo(partial).Length)
                        : "0 B";
                    throw new InvalidOperationException(
                        $"翻译模型下载不完整（已下载 {actual}，期望约 {spec.SizeHint}）。请重试或检查网络。");
                }

                if (File.Exists(dest)) File.Delete(dest);
                File.Move(partial, dest);
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { /* ignore */ }
                throw;
            }

            status("翻译模型已下载");
            log("GGUF → " + dest);
        }, ct).ConfigureAwait(false);
    }

    public static void OpenModelsFolder(AppSettings? settings = null)
    {
        var dir = AppPaths.ResolveAdvancedLlmModelsDir(settings);
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    public static void OpenGgufDownloadPage(string? hfEndpoint, string? modelId = null)
    {
        var spec = string.IsNullOrWhiteSpace(modelId)
            ? ManagedLlmCatalog.PreferDefault()
            : TranslateModels.ResolveSpec(modelId);
        var url = ManagedLlmCatalog.ApplyHfMirror(spec.GgufUrl, hfEndpoint);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    internal static async Task DownloadFileAsync(
        string url,
        string destPath,
        Action<string>? status,
        CancellationToken ct,
        long? minCompleteBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength;
        await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 82_000, useAsync: true);
        await DownloadProgressUi.CopyStreamWithProgressAsync(input, output, status, total, ct).ConfigureAwait(false);

        var actual = new FileInfo(destPath).Length;
        if (minCompleteBytes is > 0 && actual >= minCompleteBytes.Value)
            return;

        // HF mirrors often misreport Content-Length; only fail on obvious truncation.
        if (total is > 0 && actual < (long)(total.Value * 0.95))
        {
            throw new InvalidOperationException(
                $"下载不完整（{DownloadProgressUi.FormatBytes(actual)} / {DownloadProgressUi.FormatBytes(total.Value)}）。请重试。");
        }

        if (minCompleteBytes is > 0 && actual < minCompleteBytes.Value)
        {
            throw new InvalidOperationException(
                $"下载不完整（{DownloadProgressUi.FormatBytes(actual)} / 期望至少 {DownloadProgressUi.FormatBytes(minCompleteBytes.Value)}）。请重试。");
        }
    }
}
