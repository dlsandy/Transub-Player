using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// Owns the LAN HTTP server and at most one <see cref="LanWatchSession"/>.
/// Wired from MainWindow; stopped on app shutdown.
/// </summary>
public sealed class LanShareHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _status;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task>? _yieldGpu;
    private readonly Action? _endExclusive;
    private readonly Func<string?>? _getDesktopMediaPath;
    private readonly object _gate = new();
    private LanShareServer? _server;
    private LanWatchSession? _session;
    private CancellationTokenSource? _lifetime;
    private int _disposed;
    private string? _lastError;

    public bool IsRunning
    {
        get { lock (_gate) return _server?.IsRunning == true; }
    }

    public bool PhoneReachable
    {
        get { lock (_gate) return _server?.PhoneReachable != false; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public string? PairUrl
    {
        get { lock (_gate) return _server?.PairUrl; }
    }

    public string? PublicBaseUrl
    {
        get { lock (_gate) return _server?.PublicBaseUrl; }
    }

    public int Port
    {
        get { lock (_gate) return _server?.Port ?? 0; }
    }

    public string? LanIp
    {
        get { lock (_gate) return _server?.LanIp; }
    }

    public string? SessionStatus
    {
        get { lock (_gate) return _session?.StatusText; }
    }

    public string? ActiveMediaName
    {
        get { lock (_gate) return _session?.MediaFileName; }
    }

    public bool HasActiveSession
    {
        get { lock (_gate) return _session?.MediaPath is not null; }
    }

    public int ViewerCount
    {
        get { lock (_gate) return _server?.ViewerCount ?? 0; }
    }

    public string SummaryStatus
    {
        get
        {
            lock (_gate)
            {
                if (_server?.IsRunning != true)
                {
                    if (!string.IsNullOrWhiteSpace(_lastError))
                        return Loc.Format("Lan.Status.StartFailed", _lastError);
                    return Loc.Get("Lan.Status.Stopped");
                }

                if (!_server.PhoneReachable)
                    return Loc.Format("Lan.Status.LoopbackOnly", _server.Port);

                var viewers = _server.ViewerCount;
                var media = _session?.MediaFileName;
                if (!string.IsNullOrWhiteSpace(media))
                {
                    return viewers > 0
                        ? Loc.Format("Lan.Status.WatchingWithViewers", media, viewers)
                        : Loc.Format("Lan.Status.Watching", media);
                }

                return viewers > 0
                    ? Loc.Format("Lan.Status.ListeningWithViewers", _server.LanIp, _server.Port, viewers)
                    : Loc.Format("Lan.Status.Listening", _server.LanIp, _server.Port);
            }
        }
    }

    public event Action? Changed;

    public LanShareHost(
        AppSettings settings,
        Action<string> status,
        Action<string> log,
        Func<CancellationToken, Task>? yieldGpu = null,
        Action? endExclusive = null,
        Func<string?>? getDesktopMediaPath = null)
    {
        _settings = settings;
        _status = status;
        _log = log;
        _yieldGpu = yieldGpu;
        _endExclusive = endExclusive;
        _getDesktopMediaPath = getDesktopMediaPath;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_gate)
        {
            if (_server?.IsRunning == true) return;
            _lifetime?.Cancel();
            _lifetime?.Dispose();
            _lifetime = new CancellationTokenSource();

            _server?.Dispose();
            _server = new LanShareServer(
                _settings,
                () => { lock (_gate) return _session; },
                OpenSessionAsync,
                StopSessionAsync,
                _log,
                RaiseChanged);
            try
            {
                _server.Start();
                _lastError = null;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _server.Dispose();
                _server = null;
                throw;
            }

            if (_server.PhoneReachable)
                _status(Loc.Format("Lan.Status.Listening", _server.LanIp, _server.Port));
            else
                _status(Loc.Format("Lan.Status.LoopbackOnly", _server.Port));
        }

        RaiseChanged();
    }

    /// <summary>If settings say enabled but the listener is down, start it (e.g. after a failed cold start).</summary>
    public bool EnsureStartedFromSettings()
    {
        if (!_settings.LanShareEnabled) return IsRunning;
        if (IsRunning) return true;
        Start();
        return IsRunning;
    }

    public async Task StopAsync(bool announcePhoneDisconnect = true)
    {
        LanShareServer? server;
        LanWatchSession? session;
        lock (_gate)
        {
            server = _server;
            session = _session;
            _server = null;
            _session = null;
            try { _lifetime?.Cancel(); } catch { /* ignore */ }
        }

        if (session is not null)
        {
            try { await session.StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            try { session.Dispose(); } catch { /* ignore */ }
            try { _endExclusive?.Invoke(); } catch { /* ignore */ }
        }

        server?.Dispose();
        lock (_gate)
        {
            try { _lifetime?.Dispose(); } catch { /* ignore */ }
            _lifetime = null;
        }

        _status(announcePhoneDisconnect
            ? Loc.Get("Lan.Status.StoppedPhoneHint")
            : Loc.Get("Lan.Status.Stopped"));
        RaiseChanged();
    }

    public void ApplySettingsRestartIfNeeded(bool wantEnabled)
    {
        if (wantEnabled)
        {
            if (!IsRunning)
                Start();
            else
            {
                lock (_gate) _server?.RefreshAuthFromSettings();
                RaiseChanged();
            }
        }
        else if (IsRunning)
        {
            _ = StopAsync();
        }
    }

    public void RefreshAuthFromSettings()
    {
        lock (_gate) _server?.RefreshAuthFromSettings();
        RaiseChanged();
    }

    public byte[]? QrPng()
    {
        var url = PairUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;
        return LanQrEncoder.PngBytes(url);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { StopAsync().GetAwaiter().GetResult(); }
        catch { /* ignore */ }
    }

    private async Task<object> OpenSessionAsync(string mediaPath, CancellationToken ct)
    {
        LanWatchSession? previous;
        lock (_gate)
        {
            previous = _session;
            _session = null;
        }

        if (previous is not null)
        {
            try { await previous.StopAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            try { previous.Dispose(); } catch { /* ignore */ }
        }

        var session = new LanWatchSession(
            _settings,
            msg =>
            {
                _status(msg);
                try { _server?.Broadcast(); } catch { /* ignore */ }
                RaiseChanged();
            },
            _log,
            _yieldGpu,
            _getDesktopMediaPath);
        session.Changed += () =>
        {
            try { _server?.Broadcast(); } catch { /* ignore */ }
            RaiseChanged();
        };

        lock (_gate) _session = session;

        CancellationTokenSource? linkedCts = null;
        try
        {
            var linked = ct;
            if (_lifetime is not null)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                linked = linkedCts.Token;
            }

            await session.OpenAsync(mediaPath, linked).ConfigureAwait(false);
        }
        finally
        {
            linkedCts?.Dispose();
        }

        try { _server?.Broadcast(); } catch { /* ignore */ }
        RaiseChanged();
        return session.BuildStateDto();
    }

    private async Task StopSessionAsync()
    {
        LanWatchSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null) return;
        try { await session.StopAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
        try { session.Dispose(); } catch { /* ignore */ }
        try { _endExclusive?.Invoke(); } catch { /* ignore */ }
        try { _server?.Broadcast(); } catch { /* ignore */ }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }
}
