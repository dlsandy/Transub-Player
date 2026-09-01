namespace TransubPlayer.Services;

/// <summary>
/// Watches the media folder for new/updated Transub-finished sidecars after a Player→Transub handoff.
/// </summary>
internal sealed class FinishedSubtitleMonitor : IDisposable
{
    private readonly object _gate = new();
    private string? _mediaPath;
    private Dictionary<string, SidecarFingerprint> _baseline = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private int _disposed;
    private string? _pendingPath;
    private string? _ignoredPath;
    private bool _offerDismissed;

    public event Action? Changed;

    public string? PendingPath
    {
        get { lock (_gate) return _pendingPath; }
    }

    public bool HasPending
    {
        get { lock (_gate) return !string.IsNullOrWhiteSpace(_pendingPath) && !_offerDismissed; }
    }

    /// <summary>Snapshot current sidecars and watch for new/updated files beside the media.</summary>
    public void Arm(string mediaPath)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        mediaPath = Path.GetFullPath(mediaPath);
        var dir = Path.GetDirectoryName(mediaPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return;

        lock (_gate)
        {
            TearDownWatcher_NoLock();
            _mediaPath = mediaPath;
            _baseline = SubtitleFile.SnapshotSidecars(mediaPath);
            _pendingPath = null;
            _ignoredPath = null;
            _offerDismissed = false;

            try
            {
                var stem = Path.GetFileNameWithoutExtension(mediaPath);
                _watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = stem + ".*",
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnFsEvent;
                _watcher.Changed += OnFsEvent;
                _watcher.Renamed += OnFsRenamed;
            }
            catch
            {
                TearDownWatcher_NoLock();
            }
        }

        // Immediate check (file may already exist from a prior Transub run).
        Probe();
    }

    public void Disarm()
    {
        lock (_gate)
        {
            TearDownWatcher_NoLock();
            _mediaPath = null;
            _baseline = new(StringComparer.OrdinalIgnoreCase);
            _pendingPath = null;
            _ignoredPath = null;
            _offerDismissed = false;
        }
    }

    /// <summary>Re-scan without requiring a filesystem event (e.g. window Activated).</summary>
    public void Probe()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        string? media;
        Dictionary<string, SidecarFingerprint> baseline;
        string? ignore;
        lock (_gate)
        {
            media = _mediaPath;
            baseline = _baseline;
            ignore = _ignoredPath;
            if (string.IsNullOrWhiteSpace(media) || _offerDismissed)
                return;
        }

        var best = SubtitleFile.FindUpdatedSidecar(media!, baseline);
        if (best is null)
            return;
        if (!string.IsNullOrWhiteSpace(ignore)
            && string.Equals(best, ignore, StringComparison.OrdinalIgnoreCase))
            return;

        var raised = false;
        lock (_gate)
        {
            if (_offerDismissed) return;
            if (string.Equals(_pendingPath, best, StringComparison.OrdinalIgnoreCase))
                return;
            _pendingPath = best;
            raised = true;
        }

        if (raised)
            Changed?.Invoke();
    }

    public void DismissOffer()
    {
        lock (_gate)
        {
            _offerDismissed = true;
            _pendingPath = null;
        }

        Changed?.Invoke();
    }

    public void MarkAccepted(string path)
    {
        lock (_gate)
        {
            _ignoredPath = path;
            _pendingPath = null;
            _offerDismissed = true;
            if (!string.IsNullOrWhiteSpace(_mediaPath))
                _baseline = SubtitleFile.SnapshotSidecars(_mediaPath);
        }

        Changed?.Invoke();
    }

    private void OnFsRenamed(object sender, RenamedEventArgs e) => ScheduleProbe();

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!SubtitleFile.IsSidecarExtension(Path.GetExtension(e.FullPath)))
            return;
        ScheduleProbe();
    }

    private void ScheduleProbe()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            try { _debounceCts?.Cancel(); } catch { /* ignore */ }
            try { _debounceCts?.Dispose(); } catch { /* ignore */ }
            _debounceCts = cts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, cts.Token).ConfigureAwait(false);
                Probe();
            }
            catch (OperationCanceledException)
            {
                // debounce
            }
        });
    }

    private void TearDownWatcher_NoLock()
    {
        try { _debounceCts?.Cancel(); } catch { /* ignore */ }
        try { _debounceCts?.Dispose(); } catch { /* ignore */ }
        _debounceCts = null;
        if (_watcher is null) return;
        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFsEvent;
            _watcher.Changed -= OnFsEvent;
            _watcher.Renamed -= OnFsRenamed;
            _watcher.Dispose();
        }
        catch { /* ignore */ }
        _watcher = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
            TearDownWatcher_NoLock();
    }
}
