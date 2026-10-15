using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// Hardened HTTPS file downloads for ASR / GGUF / llama-server:
/// TLS 1.2+, system proxy, HF official↔mirror and GitHub release proxy fallbacks.
/// </summary>
internal static class RemoteFileDownloader
{
    private static readonly string[] GitHubProxyPrefixes =
    [
        "https://ghfast.top/",
        "https://gh-proxy.com/",
        "https://mirror.ghproxy.com/",
    ];

    private static readonly Lazy<HttpClient> SharedHttp = new(CreateClient);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TransubPlayer/1.0");
        return http;
    }

    public static async Task DownloadAsync(
        string url,
        string destPath,
        Action<string>? status,
        CancellationToken ct,
        long? minCompleteBytes = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var candidates = BuildCandidates(url);
        Exception? last = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            ct.ThrowIfCancellationRequested();
            try
            {
                if (i > 0)
                    PlayerLog.Write("download retry · " + candidate);
                else
                    PlayerLog.Write("download · " + candidate);

                await DownloadOnceAsync(candidate, destPath, status, ct, minCompleteBytes).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(destPath);
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex))
            {
                last = ex;
                PlayerLog.Write("download failed · " + FormatLogError(ex) + " · " + candidate);
                TryDelete(destPath);
                if (i + 1 < candidates.Count)
                    await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }

        TryDelete(destPath);
        if (last is IncompleteDownloadException incomplete)
            throw new InvalidOperationException(incomplete.Message, incomplete);
        throw new InvalidOperationException(FormatError(last), last);
    }

    internal static IReadOnlyList<string> BuildCandidates(string url)
    {
        var list = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            u = u.Trim();
            if (!list.Contains(u, StringComparer.OrdinalIgnoreCase))
                list.Add(u);
        }

        Add(url);

        if (TryGetHfAlternate(url, out var hfAlt))
            Add(hfAlt);

        if (IsGitHubReleaseUrl(url))
        {
            foreach (var prefix in GitHubProxyPrefixes)
                Add(prefix + url);
        }

        // Generic: one extra attempt of the same URL (brief backoff in DownloadAsync).
        if (list.Count == 1)
            Add(url);

        return list;
    }

    private static bool TryGetHfAlternate(string url, out string alternate)
    {
        alternate = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        string? newHost = null;
        if (host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
            newHost = "hf-mirror.com";
        else if (host.Equals("hf-mirror.com", StringComparison.OrdinalIgnoreCase))
            newHost = "huggingface.co";
        else
            return false;

        var b = new UriBuilder(uri) { Host = newHost, Port = -1 };
        alternate = b.Uri.AbsoluteUri;
        return true;
    }

    private static bool IsGitHubReleaseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DownloadOnceAsync(
        string url,
        string destPath,
        Action<string>? status,
        CancellationToken ct,
        long? minCompleteBytes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await SharedHttp.Value
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                Loc.Format("Download.Error.Http", (int)res.StatusCode, res.ReasonPhrase ?? ""),
                null,
                res.StatusCode);
        }

        var total = res.Content.Headers.ContentLength;
        await using var input = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(
            destPath, FileMode.Create, FileAccess.Write, FileShare.None, 82_000, useAsync: true);
        await DownloadProgressUi.CopyStreamWithProgressAsync(input, output, status, total, ct)
            .ConfigureAwait(false);

        var actual = new FileInfo(destPath).Length;
        if (minCompleteBytes is > 0 && actual >= minCompleteBytes.Value)
            return;

        // HF mirrors often misreport Content-Length; only fail on obvious truncation.
        if (total is > 0 && actual < (long)(total.Value * 0.95))
        {
            throw new IncompleteDownloadException(
                Loc.Format(
                    "Download.Error.Incomplete",
                    DownloadProgressUi.FormatBytes(actual),
                    DownloadProgressUi.FormatBytes(total.Value)));
        }

        if (minCompleteBytes is > 0 && actual < minCompleteBytes.Value)
        {
            throw new IncompleteDownloadException(
                Loc.Format(
                    "Download.Error.IncompleteMin",
                    DownloadProgressUi.FormatBytes(actual),
                    DownloadProgressUi.FormatBytes(minCompleteBytes.Value)));
        }
    }

    private static bool IsRetryable(Exception ex)
    {
        // Truncated body — next mirror may help.
        if (ex is IncompleteDownloadException)
            return true;

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or IOException or AuthenticationException or TimeoutException)
                return true;
            // HttpClient.Timeout (not user cancel).
            if (e is TaskCanceledException)
                return true;
        }

        return IsSslRelated(ex);
    }

    private sealed class IncompleteDownloadException : InvalidOperationException
    {
        public IncompleteDownloadException(string message) : base(message) { }
    }

    private static bool IsSslRelated(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (msg.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TLS", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("证书", StringComparison.Ordinal)
                || msg.Contains("authentication failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("transport connection", StringComparison.OrdinalIgnoreCase)
                || e is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    public static string FormatError(Exception? ex)
    {
        if (ex is null)
            return Loc.Get("Download.Error.Ssl");

        if (ex is InvalidOperationException && !IsSslRelated(ex))
            return Truncate(ex.Message, 160);

        if (IsSslRelated(ex))
        {
            var detail = InnermostMessage(ex);
            if (!string.IsNullOrWhiteSpace(detail)
                && !detail.Contains("see inner exception", StringComparison.OrdinalIgnoreCase))
            {
                return Loc.Format("Download.Error.SslDetail", Truncate(detail, 80));
            }

            return Loc.Get("Download.Error.Ssl");
        }

        if (ex is HttpRequestException httpEx)
            return Truncate(httpEx.Message, 160);

        return Truncate(InnermostMessage(ex), 160);
    }

    private static string FormatLogError(Exception ex)
        => InnermostMessage(ex);

    private static string InnermostMessage(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
            e = e.InnerException;
        return e.Message;
    }

    private static string Truncate(string s, int max)
    {
        s = s.Trim();
        if (s.Length <= max) return s;
        return s[..max] + "…";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
