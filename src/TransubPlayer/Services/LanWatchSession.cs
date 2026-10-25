using System.Net.Http;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// Headless preview pipeline for one LAN phone session: desktop ASR + MT, formatted cues for the browser.
/// </summary>
internal sealed class LanWatchSession : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _status;
    private readonly Action<string> _log;
    private readonly Func<CancellationToken, Task>? _yieldGpu;
    private readonly Func<string?>? _getDesktopMediaPath;
    private readonly object _cueLock = new();
    private readonly List<Cue> _cues = [];

    private AsrPipeline? _engine;
    private LlamaServerProcess? _llama;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private int _reloadGen;
    private Task? _translateLoop;
    private int _sseEpoch;
    private int _disposed;
    private int _mtFailStreak;
    private SubtitleDisplayMode _mode = SubtitleDisplayMode.Zh;
    private string? _mediaPath;
    private string? _sourceSrt;
    private SceneProfile _scene = SceneProfiles.Default;
    private double _durationSec;
    private double _waitPlayheadSec;
    private bool _reusedCache;
    private bool _pausedDesktopPreview;
    private bool _waitSkipped;

    public string? MediaPath => _mediaPath;
    public string? MediaFileName => _mediaPath is null ? null : Path.GetFileName(_mediaPath);
    public SubtitleDisplayMode Mode => _mode;
    public string StatusText { get; private set; } = "";
    public double SubFrontier { get; private set; }
    public double ZhFrontier { get; private set; }
    public double DurationSec => _durationSec;
    public int CueCount { get; private set; }
    public int TranslatedCount { get; private set; }
    public bool ReusedCache => _reusedCache;
    public bool PausedDesktopPreview => _pausedDesktopPreview;
    public int Revision => Volatile.Read(ref _sseEpoch);

    public event Action? Changed;

    public LanWatchSession(
        AppSettings settings,
        Action<string> status,
        Action<string> log,
        Func<CancellationToken, Task>? yieldGpu = null,
        Func<string?>? getDesktopMediaPath = null)
    {
        _settings = settings;
        _status = status;
        _log = log;
        _yieldGpu = yieldGpu;
        _getDesktopMediaPath = getDesktopMediaPath;
        _mode = SubtitleDisplayModeUtil.Parse(settings.SubtitleMode);
        if (!SubtitleDisplayModeUtil.IsContentMode(_mode))
            _mode = SubtitleDisplayMode.Zh;
    }

    public async Task OpenAsync(string mediaPath, CancellationToken outerCt)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        mediaPath = Path.GetFullPath(mediaPath);
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException(mediaPath);

        await StopCoreAsync().ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;
        _mediaPath = mediaPath;
        _reusedCache = false;
        _pausedDesktopPreview = false;
        _waitSkipped = false;
        _waitPlayheadSec = 0;
        _scene = SceneProfiles.Resolve(_settings.SourceLanguage, mediaPath, out _);
        SetStatus(Loc.Format("Lan.Status.Opening", Path.GetFileName(mediaPath)));

        try
        {
            var probed = await AsrAudioExtract.ProbeDurationAsync(mediaPath, ct).ConfigureAwait(false);
            if (!AsrAudioExtract.IsFallbackDuration(probed) && probed > 1)
                _durationSec = probed;
        }
        catch
        {
            // duration optional for progress UI
        }

        var outDir = PreviewPaths.OutDir(mediaPath);
        Directory.CreateDirectory(outDir);
        _sourceSrt = PreviewPaths.SourceSrt(mediaPath);

        lock (_cueLock) _cues.Clear();
        CueCount = 0;
        TranslatedCount = 0;
        SubFrontier = 0;
        ZhFrontier = 0;

        var desktopPath = _getDesktopMediaPath?.Invoke();
        var sameAsDesktop = !string.IsNullOrWhiteSpace(desktopPath)
            && string.Equals(
                Path.GetFullPath(desktopPath),
                mediaPath,
                StringComparison.OrdinalIgnoreCase);

        // Same as desktop: PreferExternalSubtitle → use sidecar, skip Whisper.
        SubtitleFile.InvalidateDirectoryListingCache(Path.GetDirectoryName(mediaPath));
        if (_settings.PreferExternalSubtitle)
        {
            var existing = SubtitleFile.FindExistingSubtitle(mediaPath);
            if (existing is not null && TryLoadExternalSidecar(existing))
            {
                _reusedCache = true;
                _waitSkipped = true; // finished sidecar — no wait overlay
                SetStatus(Loc.Format("Lan.Status.ExternalSub", Path.GetFileName(existing)));
                NotifyChanged();
                return;
            }
        }

        // Reuse on-disk preview cache — do not cancel desktop Whisper for the same (or any) ready file.
        if (PreviewPaths.HasReadyAsr(mediaPath) && File.Exists(_sourceSrt))
        {
            _reusedCache = true;
            ReloadCuesFromDisk();
            SetStatus(sameAsDesktop
                ? Loc.Get("Lan.Status.ReuseSameFile")
                : Loc.Get("Lan.Status.CacheLoaded"));
            NotifyChanged();
            _translateLoop = Task.Run(() => TranslateLoopAsync(ct), ct);
            if (_settings.TranslateEnabled)
                _ = EnsureTranslateAsync(ct);
            return;
        }

        // Need a fresh ASR job — yield desktop GPU / cancel live preview.
        if (_yieldGpu is not null)
        {
            await _yieldGpu(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(desktopPath) && !sameAsDesktop)
            {
                _pausedDesktopPreview = true;
                SetStatus(Loc.Format("Lan.Status.DesktopPaused", Path.GetFileName(desktopPath)));
            }
            else if (sameAsDesktop)
            {
                _pausedDesktopPreview = true;
                SetStatus(Loc.Get("Lan.Status.DesktopSameFileAsr"));
            }
        }

        _engine = new AsrPipeline(_settings, msg => SetStatus(msg), _log);
        await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        var packs = await _engine.EnsureAsrModelAsync(ct).ConfigureAwait(false);
        var asr = SceneProfiles.PickAsr(_settings.AsrModel, packs, _scene.Language);

        WatchSource();
        var body = new AsrJobRequest(
            mediaPath,
            outDir,
            _scene.Language,
            asr,
            _scene.ContentProfile,
            KnownDurationSec: _durationSec);
        await _engine.StartJobAsync(
            body,
            ct,
            async () =>
            {
                ReloadCuesFromDisk();
                PreviewPaths.MarkAsrDone(mediaPath);
                if (WantsMt())
                {
                    await EnsureTranslateAsync(ct).ConfigureAwait(false);
                    await DrainTranslateAsync(ct).ConfigureAwait(false);
                }

                SetStatus(Loc.Get("Lan.Status.AsrDone"));
                NotifyChanged();
            },
            terminal =>
            {
                if (terminal == "error")
                    SetStatus(Loc.Get("Lan.Status.AsrFailed"));
                else if (terminal == "cancelled")
                    SetStatus(Loc.Get("Lan.Status.AsrCancelled"));
                NotifyChanged();
            },
            onSourceFlushed: () =>
            {
                ReloadCuesFromDisk();
                NotifyChanged();
                return Task.CompletedTask;
            }).ConfigureAwait(false);

        if (!_pausedDesktopPreview)
            SetStatus(Loc.Get("Lan.Status.AsrStarted"));
        else
            SetStatus(Loc.Format("Lan.Status.AsrStartedDesktopPaused", Path.GetFileName(mediaPath)));
        NotifyChanged();
        _translateLoop = Task.Run(() => TranslateLoopAsync(ct), ct);
        if (WantsMt())
            _ = EnsureTranslateAsync(ct);
    }

    public void SetMode(SubtitleDisplayMode mode)
    {
        if (mode == SubtitleDisplayMode.Off)
            _mode = SubtitleDisplayMode.Off;
        else if (SubtitleDisplayModeUtil.IsContentMode(mode))
            _mode = mode;
        else
            return;
        // Switching back into 译文/双语 re-applies the desktop wait policy unless user skipped.
        NotifyChanged();
    }

    /// <summary>Phone tapped「不等了」— mirror desktop WaitZh skip.</summary>
    public void SkipWait()
    {
        if (_waitSkipped) return;
        _waitSkipped = true;
        SetStatus(Loc.Get("Main.Osd.WaitZhSkipped"));
        NotifyChanged();
    }

    public async Task SeekAsync(double seconds, CancellationToken ct)
    {
        if (_mediaPath is null || _engine is null || _sourceSrt is null) return;
        seconds = Math.Max(0, seconds);
        _waitPlayheadSec = seconds;
        lock (_cueLock)
        {
            _cues.RemoveAll(c => c.Start >= seconds - 0.05);
            foreach (var c in _cues)
            {
                if (c.End > seconds)
                    c.End = Math.Max(c.Start + 0.05, seconds);
            }

            CueCount = _cues.Count;
            TranslatedCount = _cues.Count(c => !string.IsNullOrWhiteSpace(c.Zh));
            SubFrontier = SubtitleFile.Frontier(_cues);
            ZhFrontier = SubtitleFile.TranslatedFrontier(_cues);
            try { SubtitleFile.WriteSrt(_sourceSrt, _cues, chinese: false); }
            catch { /* ignore */ }
        }

        PreviewPaths.ClearAsrDone(_mediaPath);
        NotifyChanged();

        var packs = await _engine.EnsureAsrModelAsync(ct).ConfigureAwait(false);
        var asr = SceneProfiles.PickAsr(_settings.AsrModel, packs, _scene.Language);
        var outDir = PreviewPaths.OutDir(_mediaPath);
        List<Cue> seed;
        lock (_cueLock) seed = _cues.Select(CloneCue).ToList();

        var body = new AsrJobRequest(
            _mediaPath,
            outDir,
            _scene.Language,
            asr,
            _scene.ContentProfile,
            StartFromSeconds: seconds,
            SeedCues: seed);
        SetStatus(Loc.Format("Lan.Status.SeekRestart", FormatTime(seconds)));
        await _engine.StartJobAsync(
            body,
            ct,
            async () =>
            {
                ReloadCuesFromDisk();
                PreviewPaths.MarkAsrDone(_mediaPath!);
                if (WantsMt())
                    await DrainTranslateAsync(ct).ConfigureAwait(false);
                SetStatus(Loc.Get("Lan.Status.AsrDone"));
                NotifyChanged();
            },
            _ => NotifyChanged(),
            onSourceFlushed: () =>
            {
                ReloadCuesFromDisk();
                NotifyChanged();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        NotifyChanged();
    }

    public object BuildStateDto()
    {
        lock (_cueLock)
        {
            var waiting = IsWaitingForZh();
            var target = WaitZhTargetSeconds();
            return new
            {
                media = _mediaPath is null ? null : Path.GetFileName(_mediaPath),
                mode = SubtitleDisplayModeUtil.ToSetting(_mode),
                status = StatusText,
                cueCount = CueCount,
                translatedCount = TranslatedCount,
                subFrontier = SubFrontier,
                zhFrontier = ZhFrontier,
                duration = _durationSec,
                revision = Revision,
                pending = ShowingZhPending(),
                reusedCache = _reusedCache,
                desktopPaused = _pausedDesktopPreview,
                compat = LanMediaCompat.Classify(_mediaPath),
                waiting,
                waitTitle = waiting ? Loc.Get("Main.WaitZh.Title") : "",
                waitDetail = waiting ? BuildWaitZhDetail(target) : "",
                waitHint = waiting ? Loc.Get("Main.WaitZh.Hint") : "",
                waitSkip = waiting ? Loc.Get("Main.WaitZh.SkipButton") : "",
                waitTarget = target,
            };
        }
    }

    public object BuildCuesDto()
    {
        lock (_cueLock)
        {
            var mode = _mode;
            var list = _cues.Select(c =>
            {
                var text = SubtitleFile.FormatCueBody(c, mode);
                var pending = mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual
                              && string.IsNullOrWhiteSpace(c.Zh)
                              && !string.IsNullOrWhiteSpace(c.Text);
                return new
                {
                    start = c.Start,
                    end = c.End,
                    text,
                    pending,
                };
            }).Where(x => !string.IsNullOrWhiteSpace(x.text) || x.pending).ToList();
            return new { revision = Revision, mode = SubtitleDisplayModeUtil.ToSetting(mode), cues = list };
        }
    }

    public async Task StopAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
        SetStatus(Loc.Get("Lan.Status.Idle"));
        NotifyChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { StopWatcher(); } catch { /* ignore */ }
        try { _llama?.Dispose(); } catch { /* ignore */ }
        _llama = null;
        try { _engine?.Dispose(); } catch { /* ignore */ }
        _engine = null;
        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
    }

    private async Task StopCoreAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        Interlocked.Increment(ref _reloadGen);
        StopWatcher();
        var translate = _translateLoop;
        _translateLoop = null;
        if (translate is not null)
        {
            try { await translate.ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        if (_engine is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await _engine.CancelJobAsync(cts.Token).ConfigureAwait(false);
                await _engine.WaitUntilIdleAsync(cts.Token).ConfigureAwait(false);
                await _engine.ReleaseGpuAsync(cts.Token).ConfigureAwait(false);
            }
            catch { /* ignore */ }

            try { _engine.Dispose(); } catch { /* ignore */ }
            _engine = null;
        }

        if (_llama is not null)
        {
            try { _llama.Dispose(); } catch { /* ignore */ }
            _llama = null;
        }

        try { _cts?.Dispose(); } catch { /* ignore */ }
        _cts = null;
        _mediaPath = null;
        _sourceSrt = null;
        _durationSec = 0;
        _reusedCache = false;
        _pausedDesktopPreview = false;
        lock (_cueLock) _cues.Clear();
        CueCount = 0;
        TranslatedCount = 0;
        SubFrontier = 0;
        ZhFrontier = 0;
    }

    private void WatchSource()
    {
        StopWatcher();
        if (_sourceSrt is null) return;
        var dir = Path.GetDirectoryName(_sourceSrt);
        var name = Path.GetFileName(_sourceSrt);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name)) return;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => OnSourceFileChanged();
        _watcher.Created += (_, _) => OnSourceFileChanged();
    }

    private void StopWatcher()
    {
        if (_watcher is null) return;
        try { _watcher.EnableRaisingEvents = false; } catch { /* ignore */ }
        try { _watcher.Dispose(); } catch { /* ignore */ }
        _watcher = null;
    }

    private void OnSourceFileChanged()
    {
        // FSW often fires 2–3 times per write; coalesce before parse + SSE broadcast.
        // Use a generation counter — disposing a CTS mid-Delay caused ObjectDisposedException
        // and skipped ReloadCuesFromDisk, so MT never saw source lines.
        var gen = Interlocked.Increment(ref _reloadGen);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180).ConfigureAwait(false);
                if (gen != Volatile.Read(ref _reloadGen)) return;
                ReloadCuesFromDisk();
                NotifyChanged();
            }
            catch (Exception ex)
            {
                _log("LAN cue reload: " + ex.Message);
            }
        });
    }

    private void ReloadCuesFromDisk()
    {
        if (_sourceSrt is null || _mediaPath is null) return;
        var incoming = SubtitleFile.ParseSrt(_sourceSrt);
        PreviewTextSanitize.CleanAsrCues(incoming, _settings, _scene.ContentProfile);
        PlaylistPrefetch.ApplyTranslationCache(incoming, _mediaPath, _settings.TranslateTarget);
        lock (_cueLock)
        {
            SubtitleFile.MergePreserveZh(_cues, incoming);
            CueCount = _cues.Count;
            TranslatedCount = _cues.Count(c => !string.IsNullOrWhiteSpace(c.Zh));
            SubFrontier = SubtitleFile.Frontier(_cues);
            ZhFrontier = SubtitleFile.TranslatedFrontier(_cues);
        }
    }

    /// <summary>Load a finished sidecar into the cue list (no ASR / MT).</summary>
    private bool TryLoadExternalSidecar(string subPath)
    {
        List<Cue> incoming;
        try { incoming = SubtitleFile.ParseSrt(subPath); }
        catch (Exception ex)
        {
            _log("LAN external sub parse: " + ex.Message);
            return false;
        }

        if (incoming.Count == 0)
        {
            _log("LAN external sub empty · " + subPath);
            return false;
        }

        // Body in Text: Zh/dual/source all fall back to source via FormatCueBody (no MT).
        lock (_cueLock)
        {
            _cues.Clear();
            _cues.AddRange(incoming);
            CueCount = _cues.Count;
            TranslatedCount = 0;
            SubFrontier = SubtitleFile.Frontier(_cues);
            ZhFrontier = SubFrontier;
        }

        return true;
    }

    private async Task TranslateLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1200, ct).ConfigureAwait(false);
                if (!WantsMt()) continue;
                await TryTranslatePendingAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log("LAN MT: " + ex.Message);
            }
        }
    }

    private async Task DrainTranslateAsync(CancellationToken ct)
    {
        for (var i = 0; i < 40; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pending = CountPendingMt();
            if (pending == 0) break;
            await TryTranslatePendingAsync(ct).ConfigureAwait(false);
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        PersistTranslation();
    }

    private int CountPendingMt()
    {
        lock (_cueLock)
        {
            return _cues.Count(c =>
                !string.IsNullOrWhiteSpace(c.Text)
                && string.IsNullOrWhiteSpace(c.Zh)
                && !PreviewTextSanitize.IsPlaceholderText(c.Text));
        }
    }

    private async Task TryTranslatePendingAsync(CancellationToken ct)
    {
        if (!WantsMt()) return;
        List<Cue> work;
        lock (_cueLock)
        {
            work = _cues
                .Where(c => !string.IsNullOrWhiteSpace(c.Text)
                            && string.IsNullOrWhiteSpace(c.Zh)
                            && !PreviewTextSanitize.IsPlaceholderText(c.Text))
                .Take(8)
                .ToList();
        }

        if (work.Count == 0) return;

        try
        {
            await EnsureTranslateAsync(ct).ConfigureAwait(false);
            var route = MtRoute.Resolve(_scene.Language, _settings.TranslateTarget, _scene.ContentProfile);
            using var translate = TranslateClient.ForUrl(_settings.TranslateUrl);
            var raw = await translate.TranslateBatchAsync(
                work.Select(c => c.Text).ToList(),
                ct,
                route,
                _scene.ContentProfile,
                _settings.TranslateModelId).ConfigureAwait(false);
            var map = TranslateClient.ParseNumbered(raw, work.Count);
            if (map.Count == 0)
            {
                _mtFailStreak++;
                if (_mtFailStreak <= 3 || _mtFailStreak % 10 == 0)
                    _log("LAN MT empty/unparsed · " + raw.Replace('\n', ' ')[..Math.Min(120, raw.Length)]);
                return;
            }

            var changed = false;
            lock (_cueLock)
            {
                for (var i = 0; i < work.Count; i++)
                {
                    if (!map.TryGetValue(i + 1, out var zh) || string.IsNullOrWhiteSpace(zh)) continue;
                    zh = PreviewTextSanitize.SanitizeMt(zh, work[i].Text, _settings, _scene.ContentProfile);
                    if (PreviewTextSanitize.IsPlaceholderText(zh)
                        || PreviewTextSanitize.LooksLikeWrongTargetScript(zh, _settings.TranslateTarget))
                        continue;
                    var live = _cues.FirstOrDefault(c =>
                        Math.Abs(c.Start - work[i].Start) < 0.02
                        && string.Equals(c.Text, work[i].Text, StringComparison.Ordinal));
                    if (live is null) continue;
                    live.Zh = zh;
                    changed = true;
                }

                if (changed)
                {
                    TranslatedCount = _cues.Count(c => !string.IsNullOrWhiteSpace(c.Zh));
                    ZhFrontier = SubtitleFile.TranslatedFrontier(_cues);
                }
            }

            if (changed)
            {
                _mtFailStreak = 0;
                PersistTranslation();
                NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _mtFailStreak++;
            _log("LAN MT: " + ex.Message);
            if (_mtFailStreak is 1 or 3 or 10)
                SetStatus(Loc.Format("Lan.Status.MtFailed", ex.Message));
        }
    }

    private void PersistTranslation()
    {
        if (_mediaPath is null) return;
        List<Cue> snap;
        lock (_cueLock) snap = _cues.Select(CloneCue).ToList();
        try
        {
            SubtitleFile.WriteDisplaySrt(
                PreviewPaths.TranslatedPreviewSrt(_mediaPath, _settings.TranslateTarget),
                snap,
                SubtitleDisplayMode.Zh);
            SubtitleFile.WriteDisplaySrt(PreviewPaths.DualSrt(_mediaPath), snap, SubtitleDisplayMode.Dual);
            PlaylistPrefetch.SaveTranslationCache(_mediaPath, _settings.TranslateTarget, snap);
        }
        catch (Exception ex)
        {
            _log("LAN persist MT: " + ex.Message);
        }
    }

    private bool WantsMt()
    {
        if (!_settings.TranslateEnabled) return false;
        var route = MtRoute.Resolve(_scene.Language, _settings.TranslateTarget, _scene.ContentProfile);
        return MtRoute.WantsTranslation(route)
               && TranslateModels.SupportsRoute(_settings.TranslateModelId, route);
    }

    private bool ShowingZhPending()
        => WantsMt()
           && _mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual
           && TranslatedCount < CueCount;

    /// <summary>Same gate as desktop <c>ShouldWaitForFirstZh</c> (settings-driven).</summary>
    private bool ShouldWaitForZhPolicy()
        => _settings.AutoStartPreview
           && _settings.TranslateEnabled
           && WantsMt()
           && _mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual
           && !SceneProfiles.IsEnglishSource(_scene.Language)
           && !_settings.PlayImmediatelyOnOpen
           && !_waitSkipped;

    private double WaitZhTargetSeconds()
    {
        var minutes = _settings.WaitForFirstZhBeforePlay
            ? Math.Clamp(_settings.WaitForZhMinutes, 0, 30)
            : 0;
        var baseTarget = minutes <= 0 ? 0.05 : minutes * 60.0;
        if (_waitPlayheadSec > 1.5)
            return Math.Max(baseTarget, _waitPlayheadSec);
        return baseTarget;
    }

    private bool IsWaitingForZh()
    {
        if (!ShouldWaitForZhPolicy()) return false;
        return ZhFrontier + 0.05 < WaitZhTargetSeconds();
    }

    private string BuildWaitZhDetail(double target)
    {
        var now = ZhFrontier;
        return target <= 0.1
            ? Loc.Format("Main.WaitZh.ProgressFirst", FormatTime(now))
            : Loc.Format("Main.WaitZh.Progress", FormatTime(now), FormatTime(target));
    }

    private async Task EnsureTranslateAsync(CancellationToken ct)
    {
        if (!WantsMt()) return;
        var url = string.IsNullOrWhiteSpace(_settings.TranslateUrl)
            ? LlamaServerProcess.DefaultBaseUrl
            : _settings.TranslateUrl.Trim().TrimEnd('/');
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await probe.GetAsync(url + "/health", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode) return;
        }
        catch
        {
            // need local spawn
        }

        _llama ??= new LlamaServerProcess();
        await _llama.EnsureRunningAsync(_settings, _log, ct, preferCpu: true).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_llama.BaseUrl))
            _settings.TranslateUrl = _llama.BaseUrl;
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        try { _status(text); } catch { /* ignore */ }
    }

    private void NotifyChanged()
    {
        Interlocked.Increment(ref _sseEpoch);
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    private static Cue CloneCue(Cue c) => new()
    {
        Index = c.Index,
        Start = c.Start,
        End = c.End,
        Text = c.Text,
        Zh = c.Zh,
    };

    private static string FormatTime(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
