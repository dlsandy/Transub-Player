using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal sealed record AppUpdateRelease(
    string VersionText,
    Version Version,
    string TagName,
    string Name,
    string Body,
    string HtmlUrl,
    string SourceId,
    string SourceDisplayName,
    string AssetName,
    string AssetUrl,
    long? AssetSize);

internal enum AppUpdateCheckKind
{
    UpToDate,
    Available,
    NoAsset,
    Failed,
}

internal sealed record AppUpdateCheckResult(
    AppUpdateCheckKind Kind,
    Version CurrentVersion,
    AppUpdateRelease? Release,
    string? ErrorMessage,
    string? TriedSources);

/// <summary>Check GitHub/GitCode releases and apply portable zip updates in place.</summary>
internal static class AppUpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex SemVerPrefix = new(
        @"^v?(?<ver>\d+(?:\.\d+){0,3})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TransubPlayer-Updater");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdateService).Assembly.GetName().Version;
            return v is null ? new Version(1, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>
    /// Portable layout only: zip overwrite keeps side-by-side data/.
    /// Installed (Program Files + LocalAppData) and raw dev bin folders open the releases page instead.
    /// </summary>
    public static bool CanApplyInPlace()
    {
        if (AppPaths.IsDevTree)
            return false;

        if (!AppPaths.IsPortable)
            return false;

        var root = InstallRoot();
        if (!File.Exists(Path.Combine(root, "TransubPlayer.exe")))
            return false;
        if (!File.Exists(Path.Combine(root, "mpv", "mpv.exe")))
            return false;

        var norm = root.Replace('/', '\\');
        if (norm.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || norm.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase)
            || norm.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsUnderProgramFiles(norm))
            return false;

        try
        {
            var probe = Path.Combine(root, ".update-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch
        {
            return false;
        }

        return true;
    }

    public static string InstallRoot() => AppPaths.InstallRoot;

    private static bool IsUnderProgramFiles(string normPath)
    {
        foreach (var special in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(special)) continue;
            var pf = Path.GetFullPath(special).TrimEnd('\\') + "\\";
            if (normPath.StartsWith(pf, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string UpdatesDir
    {
        get
        {
            var dir = Path.Combine(AppPaths.CacheDir, "updates");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static async Task<AppUpdateCheckResult> CheckAsync(AppSettings settings, CancellationToken ct)
    {
        var current = CurrentVersion;
        var errors = new List<string>();
        var tried = new List<string>();
        AppUpdateRelease? newestWithoutAsset = null;

        foreach (var source in AppUpdateEndpoints.OrderedSources(settings.UpdateSource))
        {
            tried.Add(source.DisplayName);
            try
            {
                var release = await FetchLatestAsync(source, ct).ConfigureAwait(false);
                if (release is null)
                {
                    errors.Add($"{source.DisplayName}: empty");
                    continue;
                }

                if (release.Version <= current)
                {
                    settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    settings.Save();
                    return new AppUpdateCheckResult(AppUpdateCheckKind.UpToDate, current, release, null,
                        string.Join(" → ", tried));
                }

                // GitCode may publish tags without portable zip attachments (size limits).
                // Keep looking so GitHub (or the other mirror) can supply the download asset.
                if (string.IsNullOrWhiteSpace(release.AssetUrl))
                {
                    errors.Add($"{source.DisplayName}: no zip asset");
                    if (newestWithoutAsset is null || release.Version > newestWithoutAsset.Version)
                        newestWithoutAsset = release;
                    continue;
                }

                settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                settings.Save();
                return new AppUpdateCheckResult(AppUpdateCheckKind.Available, current, release, null,
                    string.Join(" → ", tried));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{source.DisplayName}: {ex.Message}");
                PlayerLog.Write($"更新检查失败（{source.DisplayName}）：{ex.Message}");
            }
        }

        if (newestWithoutAsset is not null)
        {
            settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            settings.Save();
            return new AppUpdateCheckResult(AppUpdateCheckKind.NoAsset, current, newestWithoutAsset, null,
                string.Join(" → ", tried));
        }

        return new AppUpdateCheckResult(
            AppUpdateCheckKind.Failed,
            current,
            null,
            string.Join("；", errors),
            string.Join(" → ", tried));
    }

    public static bool ShouldAutoCheck(AppSettings settings)
    {
        if (!settings.CheckUpdatesOnStartup)
            return false;

        if (!DateTime.TryParse(settings.LastUpdateCheckUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var last))
            return true;

        return DateTime.UtcNow - last.ToUniversalTime() >= TimeSpan.FromHours(24);
    }

    public static async Task DownloadAndStageAsync(
        AppUpdateRelease release,
        Action<string>? status,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);

        var zipPath = Path.Combine(UpdatesDir, SanitizeFileName(release.AssetName));
        var extractDir = Path.Combine(UpdatesDir, "extract-" + SanitizeFileName(release.VersionText));
        var partial = zipPath + ".partial";

        status?.Invoke(Loc.Get("Update.Status.Downloading"));
        await DownloadFileAsync(release.AssetUrl, partial, release.AssetSize, status, ct).ConfigureAwait(false);

        if (File.Exists(zipPath))
            File.Delete(zipPath);
        File.Move(partial, zipPath);

        status?.Invoke(Loc.Get("Update.Status.Extracting"));
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        var payload = FindPayloadRoot(extractDir)
            ?? throw new InvalidOperationException(Loc.Get("Update.Error.NoPayload"));

        var marker = Path.Combine(UpdatesDir, "pending-apply.json");
        var json = JsonSerializer.Serialize(new
        {
            version = release.VersionText,
            source = release.SourceId,
            payload,
            install = InstallRoot(),
            exe = Path.Combine(InstallRoot(), "TransubPlayer.exe"),
            zip = zipPath,
        });
        await File.WriteAllTextAsync(marker, json, ct).ConfigureAwait(false);

        status?.Invoke(Loc.Get("Update.Status.Ready"));
    }

    /// <summary>
    /// Starts a detached PowerShell apply script then returns. Caller must shut down the app.
    /// </summary>
    public static void LaunchApplyAndExit()
    {
        var marker = Path.Combine(UpdatesDir, "pending-apply.json");
        if (!File.Exists(marker))
            throw new InvalidOperationException(Loc.Get("Update.Error.NoPending"));

        using var doc = JsonDocument.Parse(File.ReadAllText(marker));
        var root = doc.RootElement;
        var payload = root.GetProperty("payload").GetString()
            ?? throw new InvalidOperationException(Loc.Get("Update.Error.NoPending"));
        var install = root.GetProperty("install").GetString()
            ?? InstallRoot();
        var exe = root.GetProperty("exe").GetString()
            ?? Path.Combine(install, "TransubPlayer.exe");

        var scriptPath = Path.Combine(UpdatesDir, "apply-update.ps1");
        File.WriteAllText(scriptPath, BuildApplyScript(), Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = UpdatesDir,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-Payload");
        psi.ArgumentList.Add(payload);
        psi.ArgumentList.Add("-Install");
        psi.ArgumentList.Add(install);
        psi.ArgumentList.Add("-Exe");
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add("-TargetPid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-Marker");
        psi.ArgumentList.Add(marker);

        var proc = Process.Start(psi);
        if (proc is null)
            throw new InvalidOperationException(Loc.Get("Update.Error.LaunchApply"));
    }

    public static bool TryOpenReleasesPage(AppSettings settings)
    {
        try
        {
            var url = AppUpdateEndpoints.Describe(AppUpdateEndpoints.Resolve(settings.UpdateSource)).ReleasesPageUrl;
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<AppUpdateRelease?> FetchLatestAsync(AppUpdateSource source, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, source.LatestApiUrl);
        if (source.Id == AppUpdateEndpoints.GitHub)
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode == 404)
            throw new InvalidOperationException(Loc.Format("Update.Error.NoRelease", source.DisplayName));
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return ParseRelease(doc.RootElement, source);
    }

    private static AppUpdateRelease? ParseRelease(JsonElement root, AppUpdateSource source)
    {
        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        if (!TryParseVersion(tag, out var version, out var versionText))
            return null;

        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? tag : tag;
        var body = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        var html = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? source.ReleasesPageUrl
            : source.ReleasesPageUrl;

        string? assetName = null;
        string? assetUrl = null;
        long? assetSize = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            PickAsset(assets, versionText, out assetName, out assetUrl, out assetSize);

        return new AppUpdateRelease(
            versionText,
            version,
            tag,
            name,
            body,
            html,
            source.Id,
            source.DisplayName,
            assetName ?? "",
            assetUrl ?? "",
            assetSize);
    }

    private static void PickAsset(
        JsonElement assets,
        string versionText,
        out string? name,
        out string? url,
        out long? size)
    {
        name = null;
        url = null;
        size = null;

        var candidates = new List<(int Score, string Name, string Url, long? Size)>();
        foreach (var a in assets.EnumerateArray())
        {
            var n = a.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(n)) continue;
            if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            // Require product name so GitCode/Gitee auto source archives (e.g. v1.5.1.zip) are ignored.
            if (!n.Contains("TransubPlayer", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Contains("source", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Contains("symbols", StringComparison.OrdinalIgnoreCase)) continue;
            if (n.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;

            var u = a.TryGetProperty("browser_download_url", out var uEl) ? uEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(u) && a.TryGetProperty("download_url", out var dEl))
                u = dEl.GetString();
            if (string.IsNullOrWhiteSpace(u)) continue;

            long? sz = null;
            if (a.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out var s))
                sz = s;

            var score = 0;
            if (n.Equals("TransubPlayer-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                score += 100;
            if (n.Contains("TransubPlayer", StringComparison.OrdinalIgnoreCase))
                score += 40;
            if (n.Contains(versionText, StringComparison.OrdinalIgnoreCase))
                score += 20;
            if (n.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                || n.Contains("win64", StringComparison.OrdinalIgnoreCase))
                score += 10;
            if (n.Contains("framework", StringComparison.OrdinalIgnoreCase))
                score -= 30;

            candidates.Add((score, n, u!, sz));
        }

        if (candidates.Count == 0) return;
        var best = candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).First();
        name = best.Name;
        url = best.Url;
        size = best.Size;
    }

    internal static bool TryParseVersion(string raw, out Version version, out string text)
    {
        version = new Version(0, 0, 0);
        text = raw;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var m = SemVerPrefix.Match(raw.Trim());
        if (!m.Success) return false;
        var core = m.Groups["ver"].Value;
        if (!Version.TryParse(core, out var parsed))
        {
            // "1.0" → 1.0.0
            var parts = core.Split('.');
            if (parts.Length == 1 && int.TryParse(parts[0], out var major))
                parsed = new Version(major, 0, 0);
            else if (parts.Length == 2
                     && int.TryParse(parts[0], out major)
                     && int.TryParse(parts[1], out var minor))
                parsed = new Version(major, minor, 0);
            else
                return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
        text = version.ToString(3);
        return true;
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        long? expectedSize,
        Action<string>? status,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        if (File.Exists(destPath))
            File.Delete(destPath);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? expectedSize;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await DownloadProgressUi.CopyStreamWithProgressAsync(input, output, status, total, ct).ConfigureAwait(false);
    }

    private static string? FindPayloadRoot(string extractDir)
    {
        var direct = Path.Combine(extractDir, "TransubPlayer.exe");
        if (File.Exists(direct))
            return extractDir;

        foreach (var dir in Directory.EnumerateDirectories(extractDir))
        {
            if (File.Exists(Path.Combine(dir, "TransubPlayer.exe")))
                return dir;
        }

        foreach (var dir in Directory.EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, "TransubPlayer.exe")))
                return dir;
        }

        return null;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString();
    }

    private static string BuildApplyScript() => """
param(
  [Parameter(Mandatory=$true)][string]$Payload,
  [Parameter(Mandatory=$true)][string]$Install,
  [Parameter(Mandatory=$true)][string]$Exe,
  [Parameter(Mandatory=$true)][int]$TargetPid,
  [Parameter(Mandatory=$true)][string]$Marker
)
$ErrorActionPreference = 'Stop'
$log = Join-Path $env:TEMP 'TransubPlayer-update.log'
function Log($m) { Add-Content -LiteralPath $log -Value ("[{0}] {1}" -f (Get-Date -Format o), $m) }
try {
  Log "Waiting for PID $TargetPid"
  $proc = Get-Process -Id $TargetPid -ErrorAction SilentlyContinue
  if ($proc) { Wait-Process -Id $TargetPid -Timeout 120 -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 1

  if (-not (Test-Path -LiteralPath $Payload)) { throw "Payload missing: $Payload" }
  if (-not (Test-Path -LiteralPath $Install)) { throw "Install missing: $Install" }

  Log "Copying from $Payload to $Install"
  # Preserve user data/; skip updater scratch under data/cache/updates while copying.
  & robocopy $Payload $Install /E /R:2 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np `
    /XD data | Out-Null
  $rc = $LASTEXITCODE
  if ($rc -ge 8) { throw "robocopy failed: $rc" }

  Get-ChildItem -LiteralPath $Install -Recurse -File -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue

  if (Test-Path -LiteralPath $Marker) { Remove-Item -LiteralPath $Marker -Force -ErrorAction SilentlyContinue }
  Log "Launching $Exe"
  Start-Process -FilePath $Exe -WorkingDirectory $Install
}
catch {
  Log ("FAILED: " + $_)
  exit 1
}
""";
}
