using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>Local HLS proxy: resolves master playlists, decrypts MOUFLON media playlists for mpv.</summary>
internal sealed class StripchatHlsProxy : IDisposable
{
    private static StripchatHlsProxy? _shared;
    private static readonly object Gate = new();

    private static readonly Regex UriAttrRe = new(
        @"URI=""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _port;
    private int _pollCount;

    public static string Wrap(string sourceUrl, string pageReferer, string? masterUrl = null)
    {
        lock (Gate)
        {
            _shared ??= new StripchatHlsProxy();
            return _shared.WrapInternal(sourceUrl, pageReferer, masterUrl);
        }
    }

    public static void StopShared()
    {
        lock (Gate)
        {
            _shared?.Dispose();
            _shared = null;
        }
    }

    private string WrapInternal(string sourceUrl, string pageReferer, string? masterUrl)
    {
        EnsureStarted();
        var query = $"src={Uri.EscapeDataString(sourceUrl)}&ref={Uri.EscapeDataString(pageReferer)}";
        if (!string.IsNullOrWhiteSpace(masterUrl))
            query += $"&master={Uri.EscapeDataString(masterUrl)}";
        return $"http://127.0.0.1:{_port}/stripchat.m3u8?{query}";
    }

    private void EnsureStarted()
    {
        if (_loop is not null) return;
        _port = FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(ctx), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PlayerLog.Write("Stripchat proxy: " + ex.Message);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (!path.EndsWith("stripchat.m3u8", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(ctx, 404, "not found").ConfigureAwait(false);
                return;
            }

            var src = ctx.Request.QueryString["src"];
            var referer = ctx.Request.QueryString["ref"] ?? "https://stripchat.com/";
            var master = ctx.Request.QueryString["master"];
            if (string.IsNullOrWhiteSpace(src))
            {
                await WriteTextAsync(ctx, 400, "missing src").ConfigureAwait(false);
                return;
            }

            var playlist = await BuildPlaylistAsync(src, referer, master, ctx.Request.HttpMethod == "HEAD").ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(playlist);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/vnd.apple.mpegurl";
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            ctx.Response.Headers["Pragma"] = "no-cache";
            ctx.Response.ContentLength64 = bytes.Length;
            if (ctx.Request.HttpMethod != "HEAD")
                await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            PlayerLog.Write("Stripchat proxy request: " + ex.Message);
            try
            {
                await WriteTextAsync(ctx, 502, ex.Message).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<string> BuildPlaylistAsync(string sourceUrl, string pageReferer, string? masterUrl, bool headOnly)
    {
        if (headOnly)
            return "#EXTM3U\n";

        string mediaUrl;
        if (!string.IsNullOrWhiteSpace(masterUrl))
        {
            // Re-fetch master on every mpv playlist poll so psch/pkey auth never goes stale (~2 min).
            mediaUrl = StripchatHlsPlaylist.IsMasterPlaylist(sourceUrl)
                ? await StripchatHlsPlaylist.ResolveMediaUrlAsync(masterUrl, pageReferer, CancellationToken.None).ConfigureAwait(false)
                : await StripchatHlsPlaylist.ResolveVariantMediaUrlAsync(masterUrl, sourceUrl, pageReferer, CancellationToken.None).ConfigureAwait(false);
        }
        else if (StripchatHlsPlaylist.IsMasterPlaylist(sourceUrl))
        {
            mediaUrl = await StripchatHlsPlaylist.ResolveMediaUrlAsync(sourceUrl, pageReferer, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            mediaUrl = sourceUrl;
        }

        var poll = Interlocked.Increment(ref _pollCount);
        if (poll == 1 || poll % 20 == 0)
            PlayerLog.Write($"HLS proxy poll #{poll} → {ShortUrl(mediaUrl)}");

        var raw = await StripchatHlsPlaylist.FetchTextAsync(mediaUrl, pageReferer, CancellationToken.None).ConfigureAwait(false);
        var decoded = StripchatMouflonDecoder.DecodePlaylist(raw);
        decoded = StripchatHlsPlaylist.NormalizeLivePlaylist(decoded);
        return AbsolutizePlaylist(decoded, mediaUrl);
    }

    private static string ShortUrl(string url)
    {
        if (url.Length <= 96) return url;
        return url[..80] + "…";
    }

    /// <summary>
    /// Resolve relative playlist URIs against the real CDN media playlist URL,
    /// so mpv (which sees a localhost proxy URL) still fetches segments from the CDN.
    /// </summary>
    internal static string AbsolutizePlaylist(string playlist, string mediaUrl)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var baseUri))
            return playlist;

        var lines = playlist.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith('#'))
            {
                lines[i] = UriAttrRe.Replace(line, m =>
                {
                    var uri = m.Groups[1].Value;
                    var abs = ResolveAgainst(baseUri, uri);
                    return $"URI=\"{abs}\"";
                });
                continue;
            }

            lines[i] = ResolveAgainst(baseUri, line.Trim());
        }

        return string.Join('\n', lines);
    }

    private static string ResolveAgainst(Uri baseUri, string relativeOrAbsolute)
    {
        var s = relativeOrAbsolute.Trim();
        if (string.IsNullOrEmpty(s)) return s;
        if (Uri.TryCreate(s, UriKind.Absolute, out _))
            return s;
        try
        {
            return new Uri(baseUri, s).ToString();
        }
        catch
        {
            return s;
        }
    }

    private static async Task WriteTextAsync(HttpListenerContext ctx, int code, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "text/plain";
        ctx.Response.ContentLength64 = bytes.Length;
        if (ctx.Request.HttpMethod != "HEAD")
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _pollCount = 0;
    }
}
