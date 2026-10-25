using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>
/// Minimal HTTP/1.1 over <see cref="TcpListener"/> bound to <see cref="IPAddress.Any"/>.
/// Avoids HttpListener/http.sys URL ACL (Access Denied on LAN without elevation).
/// </summary>
internal sealed class LanTcpHttpServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _disposed;

    public int Port { get; private set; }
    public bool IsRunning => _listener is not null && _loop is not null;

    public void Start(int port, Func<LanHttpExchange, CancellationToken, Task> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Stop();

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => AcceptLoopAsync(listener, handler, ct), ct);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _loop = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
        Port = 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }

    private static async Task AcceptLoopAsync(
        TcpListener listener,
        Func<LanHttpExchange, CancellationToken, Task> handler,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                var captured = client;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (captured)
                        {
                            await using var stream = captured.GetStream();
                            var exchange = await LanHttpExchange.ReadAsync(stream, ct).ConfigureAwait(false);
                            if (exchange is null) return;
                            await handler(exchange, ct).ConfigureAwait(false);
                            await exchange.CompleteAsync().ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        // client gone / cancelled
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) break;
            }
            catch
            {
                try { client?.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}

internal sealed class LanHttpExchange
{
    private readonly NetworkStream _stream;
    private readonly Dictionary<string, string> _responseHeaders = new(StringComparer.OrdinalIgnoreCase);
    private bool _headersSent;
    private bool _completed;

    public string Method { get; }
    public string Path { get; }
    public NameValueCollection Query { get; }
    public IReadOnlyDictionary<string, string> RequestHeaders { get; }
    public byte[] Body { get; }
    public int StatusCode { get; set; } = 200;
    public string? ContentType { get; set; }
    public long? ContentLength { get; set; }
    /// <summary>SSE / long-lived: do not close after handler returns.</summary>
    public bool KeepOpen { get; set; }

    public NetworkStream OutputStream => _stream;

    private LanHttpExchange(
        NetworkStream stream,
        string method,
        string path,
        NameValueCollection query,
        Dictionary<string, string> headers,
        byte[] body)
    {
        _stream = stream;
        Method = method;
        Path = path;
        Query = query;
        RequestHeaders = headers;
        Body = body;
    }

    public string? GetRequestHeader(string name)
        => RequestHeaders.TryGetValue(name, out var v) ? v : null;

    public static async Task<LanHttpExchange?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var buf = new byte[4096];
        using var ms = new MemoryStream();
        var headerEnd = -1;
        while (ms.Length < 64 * 1024)
        {
            var n = await stream.ReadAsync(buf.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (n <= 0) break;
            ms.Write(buf, 0, n);
            headerEnd = IndexOfHeaderEnd(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;
        var all = ms.ToArray();
        var headerLen = headerEnd + 4;
        var text = Encoding.ASCII.GetString(all, 0, headerLen);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) return null;

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var path = target;
        var query = new NameValueCollection();
        var qIdx = target.IndexOf('?');
        if (qIdx >= 0)
        {
            path = target[..qIdx];
            ParseQuery(target[(qIdx + 1)..], query);
        }

        try { path = Uri.UnescapeDataString(path); }
        catch { /* keep raw */ }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var need = 0;
        if (headers.TryGetValue("Content-Length", out var cl)
            && int.TryParse(cl, out var nLen)
            && nLen > 0)
            need = Math.Min(nLen, 4 * 1024 * 1024);

        var have = Math.Max(0, all.Length - headerLen);
        var body = new byte[need];
        if (have > 0 && need > 0)
            Buffer.BlockCopy(all, headerLen, body, 0, Math.Min(have, need));

        var filled = Math.Min(have, need);
        while (filled < need)
        {
            var got = await stream.ReadAsync(body.AsMemory(filled, need - filled), timeout.Token)
                .ConfigureAwait(false);
            if (got <= 0) break;
            filled += got;
        }

        if (filled < need)
            Array.Resize(ref body, filled);

        return new LanHttpExchange(stream, method, path, query, headers, body);
    }

    public void SetHeader(string name, string value) => _responseHeaders[name] = value;

    public async Task WriteBodyAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        await EnsureHeadersAsync(ct).ConfigureAwait(false);
        if (data.Length > 0)
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public Task WriteBodyAsync(byte[] data, CancellationToken ct = default)
        => WriteBodyAsync(data.AsMemory(), ct);

    public async Task CompleteAsync()
    {
        if (_completed) return;
        _completed = true;
        if (KeepOpen) return;
        try
        {
            await EnsureHeadersAsync(CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    private async Task EnsureHeadersAsync(CancellationToken ct)
    {
        if (_headersSent) return;
        _headersSent = true;

        if (!string.IsNullOrWhiteSpace(ContentType))
            _responseHeaders["Content-Type"] = ContentType!;
        if (ContentLength is { } len)
            _responseHeaders["Content-Length"] = len.ToString();
        if (!_responseHeaders.ContainsKey("Cache-Control"))
            _responseHeaders["Cache-Control"] = "no-store";
        _responseHeaders.TryAdd("Connection", KeepOpen ? "keep-alive" : "close");
        _responseHeaders.TryAdd("Access-Control-Allow-Origin", "*");

        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(StatusCode).Append(' ')
            .Append(Reason(StatusCode)).Append("\r\n");
        foreach (var kv in _responseHeaders)
            sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append("\r\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        206 => "Partial Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        409 => "Conflict",
        500 => "Internal Server Error",
        _ => "OK",
    };

    private static void ParseQuery(string raw, NameValueCollection into)
    {
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                into[Uri.UnescapeDataString(part.Replace('+', ' '))] = "";
                continue;
            }

            into[Uri.UnescapeDataString(part[..eq].Replace('+', ' '))] =
                Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
    }

    private static int IndexOfHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }

        return -1;
    }
}
