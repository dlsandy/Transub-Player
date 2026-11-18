using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>LAN HTTP surface: library browse, media Range stream, session/mode APIs, SSE cues, static web UI.</summary>
internal sealed class LanShareServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppSettings _settings;
    private readonly Func<LanWatchSession?> _getSession;
    private readonly Func<string, CancellationToken, Task<object>> _openSession;
    private readonly Func<Task> _stopSession;
    private readonly Action<string> _log;
    private readonly Action? _viewersChanged;
    private readonly ConcurrentDictionary<Guid, SseClient> _sse = new();
    private readonly LanShareAuth _auth = new();
    private readonly object _broadcastGate = new();
    private LanTcpHttpServer? _http;
    private int _port;
    private string _lanIp = "127.0.0.1";
    private string _invite = "";
    private int _disposed;
    private int _broadcastRevision = -1;
    private DateTime _lastBroadcastUtc = DateTime.MinValue;
    private int _broadcastScheduled;
    private bool _phoneReachable = true;

    public int Port => _port;
    public string LanIp => _lanIp;
    public bool IsRunning => _http is { IsRunning: true };
    /// <summary>False only if we could not bind any non-loopback address (should be rare with TcpListener).</summary>
    public bool PhoneReachable => _phoneReachable;
    public int ViewerCount => _sse.Count;
    public string PublicBaseUrl => $"http://{_lanIp}:{_port}/";
    /// <summary>QR target. Password mode: base URL only; otherwise invite token is embedded.</summary>
    public string PairUrl => _auth.PasswordRequired(_settings)
        ? PublicBaseUrl
        : $"{PublicBaseUrl}?t={Uri.EscapeDataString(_invite)}";

    public LanShareServer(
        AppSettings settings,
        Func<LanWatchSession?> getSession,
        Func<string, CancellationToken, Task<object>> openSession,
        Func<Task> stopSession,
        Action<string> log,
        Action? viewersChanged = null)
    {
        _settings = settings;
        _getSession = getSession;
        _openSession = openSession;
        _stopSession = stopSession;
        _log = log;
        _viewersChanged = viewersChanged;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_http is { IsRunning: true }) return;

        Stop();

        _invite = LanShareNet.EnsureToken(_settings);
        _auth.ResetInvite(_invite);
        _lanIp = LanShareNet.TryGetLanIPv4() ?? "127.0.0.1";
        var preferred = _settings.LanSharePort is > 0 and < 65536 ? _settings.LanSharePort : 8765;

        Exception? last = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var port = preferred + attempt;
            if (port > 65535) port = 8765 + (attempt % 50);
            try
            {
                var http = new LanTcpHttpServer();
                http.Start(port, HandleAsync);
                _http = http;
                _port = http.Port;
                // TcpListener binds IPAddress.Any — reachable from LAN when firewall allows.
                _phoneReachable = !string.Equals(_lanIp, "127.0.0.1", StringComparison.Ordinal);
                LanShareNet.TryEnsureFirewallRule(_port, _log);
                _log($"LAN share · http://{_lanIp}:{_port}/ (TcpListener Any)");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                _log($"LAN bind {port}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            Loc.Format("Lan.Error.BindFailed", preferred, last?.Message ?? "bind failed"),
            last);
    }

    public void RefreshAuthFromSettings()
    {
        _invite = LanShareNet.EnsureToken(_settings);
        _auth.ResetInvite(_invite);
    }

    public void Stop()
    {
        foreach (var kv in _sse)
        {
            try { kv.Value.Cts.Cancel(); } catch { /* ignore */ }
        }

        _sse.Clear();
        try { _http?.Stop(); } catch { /* ignore */ }
        try { _http?.Dispose(); } catch { /* ignore */ }
        _http = null;
        _port = 0;
    }

    public void Broadcast()
    {
        var session = _getSession();
        var rev = session?.Revision ?? 0;
        lock (_broadcastGate)
        {
            if (rev == _broadcastRevision
                && (DateTime.UtcNow - _lastBroadcastUtc) < TimeSpan.FromMilliseconds(200))
                return;
        }

        if (Interlocked.CompareExchange(ref _broadcastScheduled, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120).ConfigureAwait(false);
                BroadcastNow();
            }
            finally
            {
                Interlocked.Exchange(ref _broadcastScheduled, 0);
            }
        });
    }

    private void BroadcastNow()
    {
        var session = _getSession();
        var rev = session?.Revision ?? 0;
        lock (_broadcastGate)
        {
            _broadcastRevision = rev;
            _lastBroadcastUtc = DateTime.UtcNow;
        }

        var stateJson = JsonSerializer.Serialize(WrapState(session), JsonOpts);
        var cuesJson = session is null
            ? JsonSerializer.Serialize(new { type = "cues", revision = 0, mode = "zh", cues = Array.Empty<object>() }, JsonOpts)
            : JsonSerializer.Serialize(MergeType(session.BuildCuesDto(), "cues"), JsonOpts);

        foreach (var kv in _sse.ToArray())
        {
            try
            {
                kv.Value.Send("data: " + stateJson + "\n\n");
                kv.Value.Send("data: " + cuesJson + "\n\n");
            }
            catch
            {
                _sse.TryRemove(kv.Key, out _);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }

    private async Task HandleAsync(LanHttpExchange ctx, CancellationToken ct)
    {
        try
        {
            var path = ctx.Path is { Length: > 0 } p ? p : "/";

            if (path is "/" or "/index.html")
            {
                await WriteAssetAsync(ctx, "index.html", "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (path is "/app.js")
            {
                await WriteAssetAsync(ctx, "app.js", "application/javascript; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (path is "/app.css")
            {
                await WriteAssetAsync(ctx, "app.css", "text/css; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (path is "/logo.png" or "/favicon.ico")
            {
                await WriteAssetAsync(ctx, "logo.png", "image/png").ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/bootstrap", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx, new
                {
                    ok = true,
                    passwordRequired = _auth.PasswordRequired(_settings),
                    inviteRequired = !_auth.PasswordRequired(_settings),
                }).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/login", StringComparison.OrdinalIgnoreCase)
                && ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = ReadJson(ctx.Body);
                var password = body.ValueKind == JsonValueKind.Undefined
                    ? null
                    : (body.TryGetProperty("password", out var pw) ? pw.GetString() : null);
                var invite = body.ValueKind == JsonValueKind.Undefined
                    ? null
                    : (body.TryGetProperty("invite", out var inv) ? inv.GetString() : null);
                invite ??= ctx.Query["t"];
                if (!_auth.TryLogin(_settings, password, invite, out var session, out var err))
                {
                    await WriteTextAsync(ctx, 401, err).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(ctx, new { ok = true, token = session }).ConfigureAwait(false);
                return;
            }

            if (!IsAuthorized(ctx))
            {
                await WriteTextAsync(ctx, 401, "unauthorized").ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMediaAsync(ctx, path["/media/".Length..]).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx, BuildStatus()).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/library", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx, BuildLibrary(ctx.Query)).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/session", StringComparison.OrdinalIgnoreCase)
                && ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var body = ReadJson(ctx.Body);
                var id = body.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var err = "bad id";
                if (!LanPathGuard.TryDecodeMediaId(id, out var rootIndex, out var rel)
                    || !LanPathGuard.TryResolveMedia(CurrentRoots(), rootIndex, rel, out var full, out err))
                {
                    await WriteTextAsync(ctx, 400, string.IsNullOrWhiteSpace(err) ? "bad id" : err).ConfigureAwait(false);
                    return;
                }

                var state = await _openSession(full, ct).ConfigureAwait(false);
                await WriteJsonAsync(ctx, state).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/session", StringComparison.OrdinalIgnoreCase)
                && ctx.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                await _stopSession().ConfigureAwait(false);
                await WriteJsonAsync(ctx, BuildStatus()).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/mode", StringComparison.OrdinalIgnoreCase)
                && ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var session = _getSession();
                if (session is null)
                {
                    await WriteTextAsync(ctx, 409, "no session").ConfigureAwait(false);
                    return;
                }

                var body = ReadJson(ctx.Body);
                var modeRaw = body.TryGetProperty("mode", out var m) ? m.GetString() : "zh";
                session.SetMode(SubtitleDisplayModeUtil.Parse(modeRaw));
                Broadcast();
                await WriteJsonAsync(ctx, session.BuildStateDto()).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/seek", StringComparison.OrdinalIgnoreCase)
                && ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var session = _getSession();
                if (session is null)
                {
                    await WriteTextAsync(ctx, 409, "no session").ConfigureAwait(false);
                    return;
                }

                var body = ReadJson(ctx.Body);
                var sec = body.TryGetProperty("sec", out var s) ? s.GetDouble() : 0;
                await session.SeekAsync(sec, ct).ConfigureAwait(false);
                Broadcast();
                await WriteJsonAsync(ctx, session.BuildStateDto()).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/skip-wait", StringComparison.OrdinalIgnoreCase)
                && ctx.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var session = _getSession();
                if (session is null)
                {
                    await WriteTextAsync(ctx, 409, "no session").ConfigureAwait(false);
                    return;
                }

                session.SkipWait();
                Broadcast();
                await WriteJsonAsync(ctx, session.BuildStateDto()).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/cues", StringComparison.OrdinalIgnoreCase))
            {
                var session = _getSession();
                if (session is null)
                {
                    await WriteJsonAsync(ctx, new { revision = 0, mode = "zh", cues = Array.Empty<object>() }).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(ctx, session.BuildCuesDto()).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSseAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            await WriteTextAsync(ctx, 404, "not found").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("LAN request: " + ex.Message);
            try { await WriteTextAsync(ctx, 500, ex.Message).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }

    private async Task HandleSseAsync(LanHttpExchange ctx, CancellationToken ct)
    {
        ctx.KeepOpen = true;
        ctx.StatusCode = 200;
        ctx.ContentType = "text/event-stream; charset=utf-8";
        ctx.SetHeader("Cache-Control", "no-store");
        ctx.SetHeader("Connection", "keep-alive");
        // No Content-Length — stream until client disconnects.
        ctx.ContentLength = null;

        var id = Guid.NewGuid();
        var clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var client = new SseClient(ctx, clientCts);
        _sse[id] = client;
        try { _viewersChanged?.Invoke(); } catch { /* ignore */ }

        try
        {
            // Force headers before first event.
            await ctx.WriteBodyAsync(ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);

            var session = _getSession();
            client.Send("data: " + JsonSerializer.Serialize(WrapState(session), JsonOpts) + "\n\n");
            if (session is not null)
                client.Send("data: " + JsonSerializer.Serialize(MergeType(session.BuildCuesDto(), "cues"), JsonOpts) + "\n\n");

            while (!clientCts.IsCancellationRequested)
            {
                await Task.Delay(15000, clientCts.Token).ConfigureAwait(false);
                try { client.Send(": keepalive\n\n"); } catch { break; }
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
        finally
        {
            _sse.TryRemove(id, out _);
            try { _viewersChanged?.Invoke(); } catch { /* ignore */ }
            try { clientCts.Cancel(); } catch { /* ignore */ }
            try { clientCts.Dispose(); } catch { /* ignore */ }
            try { ctx.KeepOpen = false; } catch { /* ignore */ }
        }
    }

    private async Task HandleMediaAsync(LanHttpExchange ctx, string idRaw)
    {
        var id = Uri.UnescapeDataString(idRaw);
        var err = "not found";
        if (!LanPathGuard.TryDecodeMediaId(id, out var rootIndex, out var rel)
            || !LanPathGuard.TryResolveMedia(CurrentRoots(), rootIndex, rel, out var full, out err))
        {
            await WriteTextAsync(ctx, 404, string.IsNullOrWhiteSpace(err) ? "not found" : err).ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(full);
        var length = info.Length;
        long start = 0;
        long end = length - 1;
        var range = ctx.GetRequestHeader("Range");
        if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var spec = range["bytes=".Length..].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var a = spec[..dash];
                var b = spec[(dash + 1)..];
                if (long.TryParse(a, out var s)) start = Math.Clamp(s, 0, Math.Max(0, length - 1));
                if (long.TryParse(b, out var e)) end = Math.Clamp(e, start, Math.Max(0, length - 1));
                else end = length - 1;
            }

            ctx.StatusCode = 206;
            ctx.SetHeader("Content-Range", $"bytes {start}-{end}/{length}");
        }
        else
        {
            ctx.StatusCode = 200;
        }

        var contentLen = end - start + 1;
        ctx.SetHeader("Accept-Ranges", "bytes");
        ctx.ContentType = GuessMime(full);
        ctx.ContentLength = contentLen;
        if (ctx.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await ctx.WriteBodyAsync(ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            return;
        }

        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[81920];
        var remaining = contentLen;
        while (remaining > 0)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
            if (read <= 0) break;
            await ctx.WriteBodyAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private object BuildStatus()
    {
        var roots = CurrentRoots();
        var session = _getSession();
        return new
        {
            ok = true,
            status = session?.StatusText ?? Loc.Get("Lan.Status.Ready"),
            port = _port,
            lanIp = _lanIp,
            phoneReachable = _phoneReachable,
            viewers = ViewerCount,
            roots = roots.Select((r, i) => new
            {
                index = i,
                name = Path.GetFileName(r.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : r,
                path = r,
            }).ToList(),
            session = session?.BuildStateDto(),
        };
    }

    private object BuildLibrary(NameValueCollection query)
    {
        var roots = CurrentRoots();
        var rootIndex = 0;
        _ = int.TryParse(query["root"], out rootIndex);
        if (rootIndex < 0 || rootIndex >= roots.Count) rootIndex = 0;
        var rel = (query["path"] ?? "").Replace('\\', '/').Trim('/');

        var items = new List<object>();
        var crumbs = new List<object>();
        if (roots.Count == 0)
        {
            return new
            {
                status = Loc.Get("Lan.Status.NoRoots"),
                roots = Array.Empty<object>(),
                crumbs,
                items,
            };
        }

        var root = roots[rootIndex];
        var dir = root;
        if (!string.IsNullOrWhiteSpace(rel) && !TryResolveDir(root, rel, out dir))
            return new { status = "bad path", roots = RootDtos(roots), crumbs, items };

        var acc = "";
        foreach (var part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = string.IsNullOrEmpty(acc) ? part : acc + "/" + part;
            crumbs.Add(new { name = part, path = acc });
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue;
                var childRel = string.IsNullOrEmpty(rel) ? name : rel + "/" + name;
                items.Add(new { type = "dir", name, path = childRel.Replace('\\', '/') });
            }

            foreach (var file in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(file);
                if (!MediaFileTypes.PlaybackExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    continue;
                var name = Path.GetFileName(file);
                var childRel = string.IsNullOrEmpty(rel) ? name : rel + "/" + name;
                childRel = childRel.Replace('\\', '/');
                var compat = LanMediaCompat.Classify(file);
                items.Add(new
                {
                    type = "file",
                    name,
                    path = childRel,
                    id = LanPathGuard.EncodeMediaId(rootIndex, childRel),
                    compat,
                    compatHint = compat == LanMediaCompat.Warn
                        ? Loc.Get("Lan.Compat.Warn")
                        : Loc.Get("Lan.Compat.Ok"),
                });
            }
        }
        catch (Exception ex)
        {
            return new
            {
                status = ex.Message,
                roots = RootDtos(roots),
                crumbs,
                items,
            };
        }

        return new
        {
            status = Loc.Get("Lan.Status.Ready"),
            roots = RootDtos(roots),
            crumbs,
            items,
        };
    }

    private IReadOnlyList<string> CurrentRoots()
        => LanPathGuard.NormalizedRoots(_settings.LanShareRoots);

    private static object RootDtos(IReadOnlyList<string> roots)
        => roots.Select((r, i) => new
        {
            index = i,
            name = Path.GetFileName(r.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : r,
            path = r,
        }).ToList();

    private static bool TryResolveDir(string root, string relative, out string full)
    {
        full = "";
        try
        {
            var rel = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (rel.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || rel == "..")
                return false;
            var combined = Path.GetFullPath(Path.Combine(root, rel));
            if (!LanPathGuard.IsUnderRoot(root, combined) || !Directory.Exists(combined))
                return false;
            full = combined;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsAuthorized(LanHttpExchange ctx)
    {
        var q = ctx.Query["t"];
        if (_auth.IsSessionOrInviteAuthorized(_settings, q))
            return true;

        var auth = ctx.GetRequestHeader("Authorization");
        if (!string.IsNullOrWhiteSpace(auth)
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && _auth.IsSessionOrInviteAuthorized(_settings, auth["Bearer ".Length..].Trim()))
            return true;

        return false;
    }

    private object WrapState(LanWatchSession? session)
    {
        if (session is null)
        {
            return new
            {
                type = "state",
                media = (string?)null,
                mode = "zh",
                status = Loc.Get("Lan.Status.Ready"),
                cueCount = 0,
                translatedCount = 0,
                subFrontier = 0.0,
                zhFrontier = 0.0,
                duration = 0.0,
                revision = 0,
                pending = false,
                reusedCache = false,
                desktopPaused = false,
                waiting = false,
                waitTitle = "",
                waitDetail = "",
                waitHint = "",
                waitSkip = "",
                waitTarget = 0.0,
                viewers = ViewerCount,
            };
        }

        var map = MergeType(session.BuildStateDto(), "state");
        map["viewers"] = ViewerCount;
        return map;
    }

    private static Dictionary<string, object?> MergeType(object dto, string type)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["type"] = type };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, JsonOpts));
        foreach (var p in doc.RootElement.EnumerateObject())
            map[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? l : p.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => JsonSerializer.Deserialize<object>(p.Value.GetRawText()),
            };
        return map;
    }

    private static JsonElement ReadJson(byte[] body)
    {
        if (body.Length == 0) return default;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private static async Task WriteJsonAsync(LanHttpExchange ctx, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        ctx.StatusCode = 200;
        ctx.ContentType = "application/json; charset=utf-8";
        ctx.ContentLength = bytes.Length;
        await ctx.WriteBodyAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteTextAsync(LanHttpExchange ctx, int code, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.StatusCode = code;
        ctx.ContentType = "text/plain; charset=utf-8";
        ctx.ContentLength = bytes.Length;
        await ctx.WriteBodyAsync(bytes).ConfigureAwait(false);
    }

    private static async Task WriteAssetAsync(LanHttpExchange ctx, string fileName, string contentType)
    {
        var bytes = LoadAsset(fileName);
        ctx.StatusCode = 200;
        ctx.ContentType = contentType;
        ctx.ContentLength = bytes.Length;
        await ctx.WriteBodyAsync(bytes).ConfigureAwait(false);
    }

    private static byte[] LoadAsset(string fileName)
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "Assets", "lan-share");
        var path = Path.Combine(baseDir, fileName);
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        var alt = Path.Combine(AppPaths.ProjectRoot, "src", "TransubPlayer", "Assets", "lan-share", fileName);
        if (File.Exists(alt))
            return File.ReadAllBytes(alt);

        throw new FileNotFoundException("LAN asset missing: " + fileName);
    }

    private static string GuessMime(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" or ".opus" => "audio/ogg",
            _ => "application/octet-stream",
        };
    }

    private sealed class SseClient(LanHttpExchange ctx, CancellationTokenSource cts)
    {
        private readonly object _gate = new();
        public CancellationTokenSource Cts { get; } = cts;

        public void Send(string chunk)
        {
            lock (_gate)
            {
                var bytes = Encoding.UTF8.GetBytes(chunk);
                ctx.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.OutputStream.Flush();
            }
        }
    }
}
