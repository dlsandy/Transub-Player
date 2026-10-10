using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace TransubPlayer.Services;

/// <summary>
/// Starts Transub's managed llama-server (advanced-llm) for preview translation MT.
/// Does not download GGUF — reuses whatever Transub already installed.
/// </summary>
internal sealed class LlamaServerProcess : IDisposable
{
    public const int DefaultPort = 39281;
    public static readonly string DefaultBaseUrl = $"http://127.0.0.1:{DefaultPort}";

    /// <summary>
    /// All known MT GGUF filenames. Prefer <see cref="TranslateModels.PreferredFilenamesFor"/> at runtime.
    /// </summary>
    public static readonly string[] PreferredModels =
        TranslateModels.PreferredFilenames.ToArray();

    private static readonly SemaphoreSlim StartGate = new(1, 1);

    private Process? _process;
    public string BaseUrl { get; private set; } = DefaultBaseUrl;
    public bool Spawned { get; private set; }
    public string? ModelPath { get; private set; }
    public string? ExePath { get; private set; }

    public static async Task<bool> IsHealthyAsync(string baseUrl, CancellationToken ct)
        => await LooksLikeLlamaModelsAsync(baseUrl, ct).ConfigureAwait(false);

    /// <summary>
    /// Something is listening but it is not llama-server (e.g. Netease GameViewer on 39281).
    /// </summary>
    private static async Task<bool> PortBlockedByForeignServiceAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var res = await LocalHttp.Client
                .GetAsync($"{baseUrl.TrimEnd('/')}/v1/models", ct)
                .ConfigureAwait(false);
            if (await LooksLikeLlamaModelsAsync(baseUrl, ct, res).ConfigureAwait(false))
                return false;
            // Any HTTP response here means another service owns the port.
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> LooksLikeLlamaModelsAsync(
        string baseUrl,
        CancellationToken ct,
        HttpResponseMessage? existing = null)
    {
        try
        {
            using var res = existing ?? await LocalHttp.Client
                .GetAsync($"{baseUrl.TrimEnd('/')}/v1/models", ct)
                .ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return true;
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task EnsureRunningAsync(
        AppSettings settings,
        Action<string>? log,
        CancellationToken ct,
        IReadOnlyList<string>? preferredModels = null,
        bool preferCpu = false)
    {
        await StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRunningLockedAsync(settings, log, ct, preferredModels, preferCpu).ConfigureAwait(false);
        }
        finally
        {
            StartGate.Release();
        }
    }

    private async Task EnsureRunningLockedAsync(
        AppSettings settings,
        Action<string>? log,
        CancellationToken ct,
        IReadOnlyList<string>? preferredModels,
        bool preferCpu)
    {
        var modelId = TranslateModels.Normalize(settings.TranslateModelId);
        var preferred = preferredModels is { Count: > 0 }
            ? preferredModels
            : TranslateModels.PreferredFilenamesFor(modelId);
        var url = string.IsNullOrWhiteSpace(settings.TranslateUrl) ? DefaultBaseUrl : settings.TranslateUrl.Trim().TrimEnd('/');
        BaseUrl = url;
        var forceNewPort = false;
        if (await IsHealthyAsync(url, ct).ConfigureAwait(false))
        {
            // Alias is always "hymt"; match on local ModelPath / filename, not remote id.
            var mismatch = ModelPath is not null && !NameMatchesPreferred(ModelPath, modelId);
            var unknownFamily = ModelPath is null;

            if (Spawned && (mismatch || unknownFamily))
            {
                log?.Invoke(mismatch
                    ? $"预设需要另一套翻译模型，正在切换 · 当前 {Path.GetFileName(ModelPath)}"
                    : "已启动的翻译服务模型未知，正在按当前预设重启");
                Stop();
            }
            else if (!mismatch && !unknownFamily)
            {
                Spawned = Spawned && _process is { HasExited: false };
                log?.Invoke($"已连接翻译服务 · {url} · {Path.GetFileName(ModelPath)}");
                return;
            }
            else
            {
                // External / unknown process on the URL — only adopt when we cannot spawn our GGUF.
                var adoptRoot = FindAdvancedLlmRoot(settings);
                var canSpawnPreferred = adoptRoot is not null
                    && FindServerExe(adoptRoot) is not null
                    && FindModel(adoptRoot, preferred, modelId) is not null;
                if (!canSpawnPreferred)
                {
                    if (mismatch)
                        log?.Invoke("已有翻译服务与当前预设模型不完全匹配，仍先使用现有服务（无法替换外部进程）");
                    Spawned = Spawned && _process is { HasExited: false };
                    log?.Invoke($"已连接翻译服务 · {url}" + (ModelPath is null ? "" : " · " + Path.GetFileName(ModelPath)));
                    return;
                }

                log?.Invoke(unknownFamily
                    ? "端口上已有翻译服务但模型未知，改用本机 GGUF 启动"
                    : "端口上已有翻译服务与当前预设不符，改用本机 GGUF 启动");
                forceNewPort = true;
            }
        }

        var root = FindAdvancedLlmRoot(settings);
        if (root is null)
            throw new InvalidOperationException(
                "未找到 Transub 的 advanced-llm（llama-server + GGUF）。请先在 Transub 下载翻译模型，或手动启动 llama-server。");

        ExePath = FindServerExe(root);
        ModelPath = FindModel(root, preferred, modelId);
        if (ExePath is null)
            throw new InvalidOperationException($"未找到 llama-server.exe：{Path.Combine(root, "runtime")}");
        if (ModelPath is null)
            throw new InvalidOperationException($"未找到可用 GGUF：{Path.Combine(root, "models")}");

        var port = DefaultPort;
        try
        {
            var uri = new Uri(url);
            if (!uri.IsDefaultPort) port = uri.Port;
        }
        catch { /* keep default */ }

        if (forceNewPort || await PortBlockedByForeignServiceAsync(url, ct).ConfigureAwait(false))
        {
            var blocked = port;
            port = FindFreePort();
            url = $"http://127.0.0.1:{port}";
            BaseUrl = url;
            log?.Invoke(forceNewPort
                ? $"翻译改用空闲端口 {port}（原 {blocked} 上的服务模型未验证）"
                : $"翻译端口 {blocked} 已被其他程序占用（非 llama-server），改用 {port}");
        }

        // Prefer GPU; when ASR already holds VRAM, try CPU first to avoid multi-minute ngl retries.
        // Locals survive Stop() — Stop only kills the process, not the chosen exe/GGUF paths.
        var exe = ExePath ?? throw new InvalidOperationException("llama-server 路径未解析");
        var model = ModelPath ?? throw new InvalidOperationException("翻译 GGUF 路径未解析");
        Exception? last = null;
        var nglOrder = preferCpu ? new[] { 0, 20, 40 } : new[] { 99, 40, 20, 0 };
        foreach (var ngl in nglOrder)
        {
            ct.ThrowIfCancellationRequested();
            Stop();
            try
            {
                StartOnce(exe, model, port, ngl, log);
                // Shorter waits when we already know GPU is contended.
                var timeoutSec = preferCpu
                    ? (ngl == 0 ? 120 : 45)
                    : (ngl > 0 ? 90 : 180);
                var ok = await WaitHealthyAsync(url, timeoutSec, ct, log).ConfigureAwait(false);
                if (ok)
                {
                    Spawned = true;
                    ExePath = exe;
                    ModelPath = model;
                    log?.Invoke($"翻译模型已启动 · {Path.GetFileName(model)} · ngl={ngl} · {url}");
                    return;
                }

                last = new TimeoutException($"llama-server 未在时限内就绪（ngl={ngl}）");
            }
            catch (Exception ex)
            {
                last = ex;
                log?.Invoke($"翻译模型启动失败 ngl={ngl}：{ex.Message}");
            }
        }

        Stop();
        throw last ?? new InvalidOperationException("无法启动 llama-server");
    }

    public void Dispose()
    {
        Stop();
        ModelPath = null;
        ExePath = null;
    }

    public void Stop()
    {
        ChildProcessLifetime.Stop(ref _process);
        Spawned = false;
    }

    private void StartOnce(string exe, string model, int port, int ngl, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(exe) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("llama-server 或 GGUF 路径为空");

        var start = new ProcessStartInfo
        {
            FileName = exe,
            // Same fallback as MpvPlayer — GetDirectoryName can be null for bare filenames.
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-m", model,
            "--host", "127.0.0.1",
            "--port", port.ToString(),
            "-c", "4096",
            "-ngl", ngl.ToString(),
            "-a", "hymt",
        })
        {
            start.ArgumentList.Add(a);
        }

        // TranslateGemma ships a strict HF Jinja template that llama-server cannot parse with
        // OpenAI-style string messages. Murasaki's Qwen/ChatML jinja also needs a built-in template.
        var fileName = Path.GetFileName(model);
        if (fileName.Contains("translategemma", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--no-jinja");
            start.ArgumentList.Add("--chat-template");
            start.ArgumentList.Add("gemma");
        }
        else if (fileName.Contains("murasaki", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("--no-jinja");
            start.ArgumentList.Add("--chat-template");
            start.ArgumentList.Add("chatml");
        }

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("[llama] " + e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke("[llama] " + e.Data);
        };
        if (!_process.Start())
            throw new InvalidOperationException("无法创建 llama-server 进程");
        ChildProcessLifetime.Track(_process);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private async Task<bool> WaitHealthyAsync(string url, int timeoutSec, CancellationToken ct, Action<string>? log)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var proc = _process;
            try
            {
                if (proc is { HasExited: true })
                {
                    var code = 0;
                    try { code = proc.ExitCode; } catch { /* disposed */ }
                    log?.Invoke($"llama-server 已退出（代码 {code}）");
                    return false;
                }
            }
            catch (InvalidOperationException)
            {
                // Includes ObjectDisposedException; process torn down between checks.
                return false;
            }

            if (await IsHealthyAsync(url, ct).ConfigureAwait(false))
                return true;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        return false;
    }

    public static string? FindAdvancedLlmRoot(AppSettings? settings = null)
    {
        string? best = null;
        var bestScore = 0;
        var modelId = TranslateModels.Normalize(settings?.TranslateModelId);
        foreach (var candidate in AdvancedLlmCandidates(settings))
        {
            foreach (var root in NormalizeAdvancedLlmRoots(candidate))
            {
                var score = ScoreAdvancedLlmRoot(root, modelId);
                if (score <= bestScore) continue;
                bestScore = score;
                best = root;
            }
        }

        // Empty Player data/advanced-llm (created by EnsureSub) must not win over Transub.
        return bestScore > 0 ? best : null;
    }

    /// <summary>
    /// Prefer roots that actually have llama-server and/or complete GGUF.
    /// Empty <c>runtime/</c> or <c>models/</c> placeholders score 0.
    /// Strongly prefer a root that holds the currently selected MT family (Player download dir vs Transub).
    /// </summary>
    private static int ScoreAdvancedLlmRoot(string root, string? translateModelId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return 0;

            var score = 0;
            if (FindServerExe(root) is not null)
                score += 10;

            var models = Path.Combine(root, "models");
            if (!Directory.Exists(models))
                return score;

            var ggufCount = 0;
            foreach (var f in Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly))
            {
                if (!ManagedLlmInstaller.IsCompleteGguf(f, ManagedLlmCatalog.AnyGgufMinBytes))
                    continue;
                ggufCount++;
                var name = Path.GetFileName(f);
                if (ManagedLlmInstaller.IsTranslationGguf(f))
                    score += 4;
            }

            if (ggufCount > 0)
                score += 5 + Math.Min(ggufCount, 5);

            // Installer writes into Player data/advanced-llm; Transub may score higher on bulk GGUFs.
            // Without this boost, a Player-only download looks "missing" vs Transub bulk GGUFs.
            if (ManagedLlmInstaller.HasGgufForModel(root, translateModelId))
                score += 50;

            return score;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> NormalizeAdvancedLlmRoots(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            yield break;

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch
        {
            yield break;
        }

        yield return full;

        var nested = Path.Combine(full, "advanced-llm");
        if (Directory.Exists(nested))
            yield return nested;
    }

    private static string? ResolveAdvancedLlmBesideExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            return string.IsNullOrWhiteSpace(dir) ? null : Path.Combine(dir, "advanced-llm");
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> AdvancedLlmCandidates(AppSettings? settings)
    {
        // User-chosen install root first (wizard / settings), then Transub reuse paths.
        if (!string.IsNullOrWhiteSpace(settings?.AdvancedLlmPath))
            yield return settings.AdvancedLlmPath.Trim();

        if (!string.IsNullOrWhiteSpace(settings?.TransubInstallPath))
        {
            foreach (var dir in InstallAdvancedLlmDirs(settings.TransubInstallPath.Trim()))
                yield return dir;
        }

        var exe = TransubInstall.FindExe(settings ?? new AppSettings());
        var exeAdvanced = ResolveAdvancedLlmBesideExe(exe);
        if (!string.IsNullOrWhiteSpace(exeAdvanced))
            yield return exeAdvanced;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Transub", "advanced-llm");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Transub", "advanced-llm");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Transub", "advanced-llm");

        var programsExe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Transub", "Transub.exe");
        if (File.Exists(programsExe))
            yield return Path.Combine(Path.GetDirectoryName(programsExe)!, "advanced-llm");

        // Sibling Transub next to Player (e.g. F:\Transub beside F:\Transub Player).
        string? siblingAdvanced = null;
        try
        {
            var parent = Path.GetDirectoryName(AppPaths.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent))
                siblingAdvanced = Path.Combine(parent, "Transub", "advanced-llm");
        }
        catch
        {
            // ignore
        }

        if (!string.IsNullOrWhiteSpace(siblingAdvanced))
            yield return siblingAdvanced;

        // Player-owned install (auto/manual GGUF + llama-server) — last so empty dirs lose.
        yield return AppPaths.AdvancedLlmDir;
    }

    private static IEnumerable<string> InstallAdvancedLlmDirs(string installOrEnginePath)
    {
        string? root = null;
        try
        {
            if (Directory.Exists(installOrEnginePath))
                root = Path.GetFullPath(installOrEnginePath);
        }
        catch
        {
            root = null;
        }

        if (string.IsNullOrWhiteSpace(root))
            yield break;

        yield return Path.Combine(root, "advanced-llm");
        var parent = Path.GetDirectoryName(root);
        if (!string.IsNullOrWhiteSpace(parent))
            yield return Path.Combine(parent, "advanced-llm");
    }

    private static string? FindServerExe(string root)
    {
        var direct = Path.Combine(root, "runtime", "llama-server.exe");
        if (File.Exists(direct)) return direct;
        try
        {
            return Directory.EnumerateFiles(Path.Combine(root, "runtime"), "llama-server.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindModel(string root, IReadOnlyList<string>? preferred = null, string? modelId = null)
    {
        var models = Path.Combine(root, "models");
        if (!Directory.Exists(models)) return null;
        var id = TranslateModels.Normalize(modelId);
        var spec = TranslateModels.ResolveSpec(id);
        var minBytes = ManagedLlmCatalog.MinGgufBytes(spec);
        var list = preferred is { Count: > 0 } ? preferred : TranslateModels.PreferredFilenamesFor(id);
        foreach (var name in list)
        {
            var p = Path.Combine(models, name);
            if (ManagedLlmInstaller.IsCompleteGguf(p, minBytes)
                && TranslateModels.MatchesFamily(p, id))
                return p;
        }

        try
        {
            return Directory.EnumerateFiles(models, "*.gguf", SearchOption.TopDirectoryOnly)
                .Where(f => ManagedLlmInstaller.IsCompleteGguf(f, minBytes)
                            && TranslateModels.MatchesFamily(f, id))
                .OrderBy(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool NameMatchesPreferred(string modelPath, string? modelId)
        => TranslateModels.MatchesFamily(modelPath, modelId);
}
