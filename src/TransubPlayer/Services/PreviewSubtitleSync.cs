using System.Collections.Concurrent;
using System.IO;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// Watches ASR output, merges cues, refreshes display SRT, and drains preview MT queue.
/// </summary>
internal sealed class PreviewSubtitleSync : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _status;
    private readonly Action<string> _log;
    private readonly Action _stateChanged;
    private readonly Func<string> _getContentProfile;
    private readonly Func<MtRoute> _getMtRoute;
    private readonly Func<bool> _wantsPreviewMt;
    private readonly Func<bool?> _getTranslateReady;
    private readonly Action<bool?> _setTranslateReady;
    private readonly Func<CancellationToken, Task> _ensureTranslate;
    private readonly Func<SubtitleDisplayMode> _getDisplayMode;
    private readonly Func<double> _getPosition;
    /// <summary>path, reloadIfSame — see <see cref="MpvPlayer.SetSubtitle"/>.</summary>
    private readonly Action<string, bool> _applySub;
    private readonly Action _reloadSub;
    private readonly Func<string> _buildStatusLine;
    private readonly Action? _onZhFrontierProgress;
    private readonly Action? _onZhTranslationFailed;

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounce;
    private System.Timers.Timer? _deferredReload;
    private CancellationTokenSource _mtCts = new();
    private volatile bool _disposed;
    private readonly ConcurrentDictionary<string, byte> _translated = new();
    /// <summary>Lines we will not re-queue (MT refused / sanitized empty / punct-only source).</summary>
    private readonly ConcurrentDictionary<string, byte> _mtSkip = new();
    private readonly List<Cue> _cues = [];
    private readonly object _cueLock = new();
    private int _translateBusy; // 0 = idle, 1 = busy (Interlocked)
    private bool _usingExistingSub;
    private string? _lastDisplayFp;
    private string? _loadedDisplayPath;
    private List<Cue> _syncedCues = [];
    private bool _reloadPending;

    private string? _sourceSrt;
    private string? _zhSrt;
    private string? _dualSrt;
    private string? _displaySrt;

    public int CueCount { get; private set; }
    public int TranslatedCount { get; private set; }
    public double SubFrontier { get; private set; }
    public double ZhFrontier { get; private set; }

    public PreviewSubtitleSync(
        AppSettings settings,
        Action<string> status,
        Action<string> log,
        Action stateChanged,
        Func<string> getContentProfile,
        Func<MtRoute> getMtRoute,
        Func<bool> wantsPreviewMt,
        Func<bool?> getTranslateReady,
        Action<bool?> setTranslateReady,
        Func<CancellationToken, Task> ensureTranslate,
        Func<SubtitleDisplayMode> getDisplayMode,
        Func<double> getPosition,
        Action<string, bool> applySub,
        Action reloadSub,
        Func<string> buildStatusLine,
        Action? onZhFrontierProgress = null,
        Action? onZhTranslationFailed = null)
    {
        _settings = settings;
        _status = status;
        _log = log;
        _stateChanged = stateChanged;
        _getContentProfile = getContentProfile;
        _getMtRoute = getMtRoute;
        _wantsPreviewMt = wantsPreviewMt;
        _getTranslateReady = getTranslateReady;
        _setTranslateReady = setTranslateReady;
        _ensureTranslate = ensureTranslate;
        _getDisplayMode = getDisplayMode;
        _getPosition = getPosition;
        _applySub = applySub;
        _reloadSub = reloadSub;
        _buildStatusLine = buildStatusLine;
        _onZhFrontierProgress = onZhFrontierProgress;
        _onZhTranslationFailed = onZhTranslationFailed;
    }

    public void Reset()
    {
        ClearCueState();
        _usingExistingSub = false;
        _sourceSrt = null;
        _zhSrt = null;
        _dualSrt = null;
        _displaySrt = null;
    }

    /// <summary>
    /// Drop in-memory cues / frontiers and display-apply state for a new ASR run,
    /// keeping output paths. Call before deleting preview SRTs so the seek bar does not
    /// keep showing stale coverage while mpv's track points at a removed file.
    /// </summary>
    public void PrepareFreshRun()
    {
        ClearCueState();
    }

    private void ClearCueState()
    {
        StopWatching();
        CancelDeferredReload();
        ReplaceMtCts();
        Interlocked.Exchange(ref _translateBusy, 0);
        lock (_cueLock) _cues.Clear();
        _translated.Clear();
        _mtSkip.Clear();
        CueCount = 0;
        TranslatedCount = 0;
        SubFrontier = 0;
        ZhFrontier = 0;
        _lastDisplayFp = null;
        _loadedDisplayPath = null;
        _syncedCues = [];
        _reloadPending = false;
    }

    public void SetUsingExistingSub(bool value) => _usingExistingSub = value;

    public void SetOutputPaths(string source, string zh, string dual, string display)
    {
        _sourceSrt = source;
        _zhSrt = zh;
        _dualSrt = dual;
        _displaySrt = display;
    }

    public void WatchSource()
    {
        if (_sourceSrt is not null)
            Watch(_sourceSrt);
    }

    public void OnSourceChanged()
    {
        if (_sourceSrt is not null)
            OnSrtChanged(_sourceSrt);
    }

    public void RefreshDisplaySub()
    {
        if (_usingExistingSub || _displaySrt is null) return;
        List<Cue> snapshot;
        lock (_cueLock)
            snapshot = _cues.Select(c => new Cue { Index = c.Index, Start = c.Start, End = c.End, Text = c.Text, Zh = c.Zh }).ToList();
        if (snapshot.Count == 0) return;

        var mode = _getDisplayMode();
        var fp = Fingerprint(snapshot, mode);
        if (string.Equals(fp, _lastDisplayFp, StringComparison.Ordinal))
            return;
        _lastDisplayFp = fp;

        // Always refresh the file mpv is showing; write zh/dual only when needed for that mode or cache.
        SubtitleFile.WriteDisplaySrt(_displaySrt, snapshot, mode);
        if (mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
        {
            if (_zhSrt is not null)
                SubtitleFile.WriteDisplaySrt(_zhSrt, snapshot, SubtitleDisplayMode.Zh);
        }

        if (mode == SubtitleDisplayMode.Dual && _dualSrt is not null)
            SubtitleFile.WriteDisplaySrt(_dualSrt, snapshot, SubtitleDisplayMode.Dual);

        ApplyDisplaySubAvoidingFlicker(snapshot, mode);
    }

    /// <summary>Seek / mode change: apply any file written while reload was deferred.</summary>
    public void FlushPendingSubReload()
    {
        if (!_reloadPending) return;
        CancelDeferredReload();
        _reloadPending = false;
        if (_displaySrt is null || _usingExistingSub) return;
        _applySub(_displaySrt, true);
        _loadedDisplayPath = _displaySrt;
        MarkSyncedFromLastWrite();
    }

    /// <summary>Persist zh + dual sidecars after MT progress (handoff / cache), without rewriting display twice.</summary>
    public void PersistTranslatedSidecars()
    {
        if (_usingExistingSub) return;
        List<Cue> snapshot;
        lock (_cueLock)
            snapshot = _cues.Select(c => new Cue { Index = c.Index, Start = c.Start, End = c.End, Text = c.Text, Zh = c.Zh }).ToList();
        if (snapshot.Count == 0 || snapshot.All(c => string.IsNullOrWhiteSpace(c.Zh))) return;
        if (_zhSrt is not null)
            SubtitleFile.WriteDisplaySrt(_zhSrt, snapshot, SubtitleDisplayMode.Zh);
        if (_dualSrt is not null)
            SubtitleFile.WriteDisplaySrt(_dualSrt, snapshot, SubtitleDisplayMode.Dual);
    }

    public void MergeZhCache(string mediaPath)
    {
        if (_disposed) return;
        lock (_cueLock)
        {
            PlaylistPrefetch.ApplyTranslationCache(_cues, mediaPath, _settings.TranslateTarget);
            RebuildTranslatedKeys();
            TranslatedCount = CountRealTranslations(_cues);
            RecalcFrontiers();
        }

        RefreshDisplaySub();
        _stateChanged();
    }

    /// <summary>Snapshot cues for partial ASR restart seeding.</summary>
    public IReadOnlyList<Cue> SnapshotCues()
    {
        lock (_cueLock)
        {
            return _cues.Select(c => new Cue
            {
                Index = c.Index,
                Start = c.Start,
                End = c.End,
                Text = c.Text,
                Zh = c.Zh,
            }).ToList();
        }
    }

    /// <summary>Drop cues at/after the playhead and rewrite source SRT. Returns kept cue count.</summary>
    public int TruncateAfter(double seconds, string? sourceSrtPath)
    {
        List<Cue> kept;
        lock (_cueLock)
        {
            kept = _cues.Where(c => c.End <= seconds + 0.05).ToList();
            _cues.Clear();
            for (var i = 0; i < kept.Count; i++)
            {
                kept[i].Index = i + 1;
                _cues.Add(kept[i]);
            }

            RebuildTranslatedKeys();
            CueCount = _cues.Count;
            TranslatedCount = CountRealTranslations(_cues);
            RecalcFrontiers();
        }

        if (sourceSrtPath is not null)
        {
            if (kept.Count > 0)
                SubtitleFile.WriteSrt(sourceSrtPath, kept, chinese: false);
            else
            {
                try
                {
                    if (File.Exists(sourceSrtPath))
                        File.Delete(sourceSrtPath);
                }
                catch { /* ignore */ }
            }
        }

        _syncedCues = [];
        _lastDisplayFp = null;
        RefreshDisplaySub();
        _stateChanged();
        return kept.Count;
    }

    /// <summary>Drop MT lines and cancel in-flight batches (e.g. preview target language changed).</summary>
    public void ClearTranslations()
    {
        ReplaceMtCts();
        Interlocked.Exchange(ref _translateBusy, 0);
        CancelDeferredReload();
        _reloadPending = false;
        lock (_cueLock)
        {
            foreach (var c in _cues)
                c.Zh = null;
            _translated.Clear();
            _mtSkip.Clear();
            TranslatedCount = 0;
            RecalcFrontiers();
        }

        // Force mpv to re-read display.srt — stale synced cues can block reload via flicker guard.
        _syncedCues = [];
        _lastDisplayFp = null;
        _loadedDisplayPath = null;
        RefreshDisplaySub();
        ForceMpvReloadDisplay();
        _stateChanged();
    }

    /// <summary>Guess ASR language from cue text when settings/scene are still <c>auto</c>.</summary>
    public string? InferDominantSourceLanguage()
    {
        lock (_cueLock)
        {
            if (_cues.Count == 0) return null;
            var ja = 0;
            var zh = 0;
            var ko = 0;
            var en = 0;
            foreach (var c in _cues)
            {
                var t = c.Text ?? "";
                if (t.Length == 0) continue;
                if (ContainsScript(t, ScriptKind.HiraganaKatakana)) ja++;
                else if (ContainsScript(t, ScriptKind.Hangul)) ko++;
                else if (ContainsScript(t, ScriptKind.Han)) zh++;
                else if (ContainsScript(t, ScriptKind.Latin)) en++;
            }

            var max = Math.Max(Math.Max(ja, zh), Math.Max(ko, en));
            if (max == 0) return null;
            if (ja == max) return SourceLanguages.Ja;
            if (ko == max) return SourceLanguages.Ko;
            if (zh == max) return SourceLanguages.Zh;
            if (en == max) return SourceLanguages.En;
            return null;
        }
    }

    private enum ScriptKind { HiraganaKatakana, Hangul, Han, Latin }

    private static bool ContainsScript(string text, ScriptKind kind)
    {
        foreach (var ch in text)
        {
            switch (kind)
            {
                case ScriptKind.HiraganaKatakana:
                    if (ch is (>= '\u3040' and <= '\u30ff')) return true;
                    break;
                case ScriptKind.Hangul:
                    if (ch is (>= '\uAC00' and <= '\uD7AF')) return true;
                    break;
                case ScriptKind.Han:
                    if (ch is (>= '\u4e00' and <= '\u9fff')) return true;
                    break;
                case ScriptKind.Latin:
                    if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')) return true;
                    break;
            }
        }

        return false;
    }

    /// <summary>After target change: drain MT queue until every cue is retried or skipped.</summary>
    public async Task RetranslateDrainAsync(CancellationToken ct)
    {
        if (_disposed || !_wantsPreviewMt()) return;
        const int maxRounds = 512;
        for (var round = 0; round < maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed || !_wantsPreviewMt()) return;

            await TryTranslatePendingAsync().ConfigureAwait(false);
            for (var w = 0; w < 240 && Volatile.Read(ref _translateBusy) != 0; w++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            lock (_cueLock)
            {
                if (SelectPendingForMt(1).Count == 0)
                    break;
            }
        }
    }

    private void ForceMpvReloadDisplay()
    {
        if (_usingExistingSub || _displaySrt is null) return;
        CancelDeferredReload();
        _reloadPending = false;
        _applySub(_displaySrt, true);
        _loadedDisplayPath = _displaySrt;
        MarkSyncedFromLastWrite();
    }

    /// <summary>
    /// Prefer translating cues near the playhead (then chronological fill), so seeks into
    /// already-ASRed regions get Zh/Dual without waiting on the early backlog.
    /// </summary>
    public async Task TryTranslatePendingAsync()
    {
        if (_disposed || !_wantsPreviewMt()) return;
        if (Interlocked.CompareExchange(ref _translateBusy, 1, 0) != 0) return;
        var mtCts = _mtCts;
        var mtCt = mtCts.Token;
        List<Cue> pending;
        lock (_cueLock)
        {
            pending = SelectPendingForMt(batchSize: 8);
        }

        if (pending.Count == 0)
        {
            Interlocked.Exchange(ref _translateBusy, 0);
            return;
        }

        var drainMore = false;
        try
        {
            if (_getTranslateReady() != true)
                await _ensureTranslate(mtCt).ConfigureAwait(false);
            if (_disposed || mtCt.IsCancellationRequested || !ReferenceEquals(mtCts, _mtCts)) return;
            if (_getTranslateReady() != true)
            {
                _status(_buildStatusLine());
                if (TranslatedCount == 0)
                    _onZhTranslationFailed?.Invoke();
                return;
            }

            using var translate = TranslateClient.ForUrl(_settings.TranslateUrl);
            var route = _getMtRoute();
            var profile = _getContentProfile();
            var raw = await translate.TranslateBatchAsync(
                pending.Select(c => c.Text).ToList(), mtCt, route, profile, _settings.TranslateModelId).ConfigureAwait(false);
            if (_disposed || mtCt.IsCancellationRequested || !ReferenceEquals(mtCts, _mtCts)) return;
            var map = TranslateClient.ParseNumbered(raw, pending.Count);
            if (map.Count == 0)
                _log("翻译模型返回无法解析：" + raw.Replace('\n', ' ')[..Math.Min(180, raw.Length)]);
            var applied = 0;
            lock (_cueLock)
            {
                for (var i = 0; i < pending.Count; i++)
                {
                    if (!map.TryGetValue(i + 1, out var zh) || string.IsNullOrWhiteSpace(zh)) continue;
                    zh = PreviewTextSanitize.SanitizeMt(zh, pending[i].Text, _settings, profile);
                    if (string.IsNullOrWhiteSpace(zh))
                    {
                        // Model refused / sanitized to placeholder — mark skip, keep source on screen.
                        _mtSkip[Key(pending[i])] = 1;
                        continue;
                    }

                    if (PreviewTextSanitize.LooksLikeWrongTargetScript(zh, _settings.TranslateTarget))
                    {
                        _log("翻译模型返回与目标语言不符的文本，已跳过");
                        _mtSkip[Key(pending[i])] = 1;
                        continue;
                    }

                    pending[i].Zh = zh;
                    _translated[Key(pending[i])] = 1;
                    _mtSkip.TryRemove(Key(pending[i]), out _);
                    var live = _cues.FirstOrDefault(c => Math.Abs(c.Start - pending[i].Start) < 0.05
                        && string.Equals(c.Text, pending[i].Text, StringComparison.Ordinal));
                    if (live is not null) live.Zh = zh;
                    applied++;
                }

                PreviewTextSanitize.UnstickCrossCue(_cues, _settings);
                RebuildTranslatedKeys();
                TranslatedCount = CountRealTranslations(_cues);
                RecalcFrontiers();
            }

            RefreshDisplaySub();
            PersistTranslatedSidecars();
            _status(_buildStatusLine());
            var frontier = Loc.Format(
                "Main.Status.MtFrontier",
                MediaTimeFormat.Format(SubFrontier),
                ZhFrontier > 0 ? MediaTimeFormat.Format(ZhFrontier) : "—",
                TranslatedCount,
                CueCount);
            PlayerLog.WriteEngine(frontier);
            _stateChanged();

            if (TranslatedCount > 0)
                _onZhFrontierProgress?.Invoke();

            // Only keep draining when this batch made progress; otherwise we would spin forever.
            drainMore = applied > 0;
        }
        catch (OperationCanceledException)
        {
            // reset or shutdown
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            _log("翻译模型：" + ex.Message);
            _setTranslateReady(false);
            _status("暂显原文 · 翻译失败（可切显示方式）");
            if (TranslatedCount == 0)
                _onZhTranslationFailed?.Invoke();
        }
        finally
        {
            // Only clear busy if we still own this MT session (Reset replaces _mtCts).
            if (ReferenceEquals(mtCts, _mtCts))
                Interlocked.Exchange(ref _translateBusy, 0);
        }

        // Continue after clearing busy — calling while busy made the chain a no-op.
        if (drainMore && !_disposed && !mtCt.IsCancellationRequested && ReferenceEquals(mtCts, _mtCts))
            _ = TryTranslatePendingAsync();
    }

    public void StopWatching()
    {
        if (_watcher is not null)
        {
            try { _watcher.EnableRaisingEvents = false; } catch { /* ignore */ }
            _watcher.Dispose();
            _watcher = null;
        }

        if (_debounce is not null)
        {
            _debounce.Stop();
            _debounce.Dispose();
            _debounce = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _mtCts.Cancel(); } catch { /* ignore */ }
        CancelDeferredReload();
        StopWatching();
        _mtCts.Dispose();
    }

    private void ApplyDisplaySubAvoidingFlicker(List<Cue> snapshot, SubtitleDisplayMode mode)
    {
        var pos = _getPosition();
        var active = CueAt(snapshot, pos);
        var prevText = DisplayTextAt(_syncedCues, pos, mode);
        var nextText = active is null ? null : SubtitleFile.FormatCueBody(active, mode);
        // Both null (gap) or equal on-screen text → safe to defer sub-reload.
        var onScreenUnchanged = string.Equals(prevText, nextText, StringComparison.Ordinal);
        var pathReady = string.Equals(_loadedDisplayPath, _displaySrt, StringComparison.OrdinalIgnoreCase);

        if (!onScreenUnchanged || !pathReady)
        {
            CancelDeferredReload();
            _reloadPending = false;
            _applySub(_displaySrt!, true);
            _loadedDisplayPath = _displaySrt;
            _syncedCues = snapshot;
            return;
        }

        // On-screen text is stable — keep current frame, reload before mpv needs unknown cues.
        _applySub(_displaySrt!, false);
        var deadline = active?.End ?? pos + 30;
        foreach (var c in snapshot)
        {
            if (c.Start <= pos + 0.02) continue;
            if (CueKnownSynced(c, mode)) continue;
            if (c.Start < deadline)
                deadline = c.Start;
            break;
        }

        var delaySec = Math.Max(0.05, deadline - pos - 0.08);
        ScheduleDeferredReload(delaySec, snapshot);
    }

    private void ScheduleDeferredReload(double delaySec, List<Cue> snapshotForSync)
    {
        _reloadPending = true;
        CancelDeferredReload(disposeOnly: true);
        var ms = Math.Clamp(delaySec * 1000.0, 50, 120_000);
        var timer = new System.Timers.Timer(ms) { AutoReset = false };
        _deferredReload = timer;
        timer.Elapsed += (_, _) =>
        {
            try
            {
                if (_disposed || !_reloadPending) return;
                _reloadPending = false;
                _reloadSub();
                _loadedDisplayPath = _displaySrt;
                _syncedCues = snapshotForSync;
            }
            catch (Exception ex)
            {
                _log(ex.Message);
            }
            finally
            {
                try { timer.Dispose(); } catch { /* ignore */ }
                if (ReferenceEquals(_deferredReload, timer))
                    _deferredReload = null;
            }
        };
        timer.Start();
    }

    private void CancelDeferredReload(bool disposeOnly = false)
    {
        var timer = _deferredReload;
        _deferredReload = null;
        if (!disposeOnly)
            _reloadPending = false;
        if (timer is null) return;
        try { timer.Stop(); } catch { /* ignore */ }
        try { timer.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Pick next MT batch: cues around the playhead first, then earlier untranslated cues.
    /// Must be called under <see cref="_cueLock"/>.
    /// </summary>
    private List<Cue> SelectPendingForMt(int batchSize)
    {
        // Skip punct/ellipsis-only ASR debris — translating「…」just yields more「…」.
        var candidates = new List<Cue>();
        foreach (var c in _cues)
        {
            var key = Key(c);
            if (_translated.ContainsKey(key) || _mtSkip.ContainsKey(key)) continue;
            if (string.IsNullOrWhiteSpace(c.Text)) continue;
            if (PreviewTextSanitize.IsPlaceholderText(c.Text))
            {
                _mtSkip[key] = 1;
                continue;
            }

            // Soft-voice ASR spam must not enter the MT queue (would paint 哈哈… fullscreen).
            if (PreviewTextSanitize.LooksLikeAsrHallucination(c.Text))
            {
                _mtSkip[key] = 1;
                continue;
            }

            candidates.Add(c);
        }

        if (candidates.Count == 0) return candidates;

        var pos = _getPosition();
        const double padBefore = 3.0;
        const double lookahead = 60.0;
        var windowStart = pos - padBefore;
        var windowEnd = pos + lookahead;

        var near = candidates
            .Where(c => c.Start >= windowStart && c.Start <= windowEnd)
            .OrderBy(c => c.Start)
            .Take(batchSize)
            .ToList();
        if (near.Count >= batchSize) return near;

        var taken = new HashSet<string>(near.Select(Key), StringComparer.Ordinal);
        foreach (var c in candidates.OrderBy(c => c.Start))
        {
            if (!taken.Add(Key(c))) continue;
            near.Add(c);
            if (near.Count >= batchSize) break;
        }

        return near;
    }

    private void MarkSyncedFromLastWrite()
    {
        lock (_cueLock)
        {
            _syncedCues = _cues
                .Select(c => new Cue { Index = c.Index, Start = c.Start, End = c.End, Text = c.Text, Zh = c.Zh })
                .ToList();
        }
    }

    private bool CueKnownSynced(Cue cue, SubtitleDisplayMode mode)
    {
        var body = SubtitleFile.FormatCueBody(cue, mode);
        foreach (var s in _syncedCues)
        {
            if (Math.Abs(s.Start - cue.Start) > 0.05) continue;
            if (Math.Abs(s.End - cue.End) > 0.05) continue;
            if (string.Equals(SubtitleFile.FormatCueBody(s, mode), body, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Cue? CueAt(IReadOnlyList<Cue> cues, double pos)
    {
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (pos >= c.Start && pos < c.End)
                return c;
        }

        return null;
    }

    private static string? DisplayTextAt(IReadOnlyList<Cue> cues, double pos, SubtitleDisplayMode mode)
    {
        var c = CueAt(cues, pos);
        return c is null ? null : SubtitleFile.FormatCueBody(c, mode);
    }

    private static string Fingerprint(IReadOnlyList<Cue> cues, SubtitleDisplayMode mode)
    {
        // Cheap stability key for ASR append / MT fill — avoids SHA-256 over every cue body each tick.
        unchecked
        {
            var h = (uint)mode * 397u + (uint)cues.Count;
            for (var i = 0; i < cues.Count; i++)
            {
                var c = cues[i];
                h = h * 31u + (uint)(c.Start * 1000.0);
                h = h * 31u + (uint)(c.End * 1000.0);
                h = h * 31u + (uint)(c.Text?.GetHashCode(StringComparison.Ordinal) ?? 0);
                h = h * 31u + (uint)(c.Zh?.GetHashCode(StringComparison.Ordinal) ?? 0);
            }

            return h.ToString("X8");
        }
    }

    private void ReplaceMtCts()
    {
        var next = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _mtCts, next);
        try { old.Cancel(); } catch { /* ignore */ }
        // Do not Dispose immediately — in-flight TranslateBatchAsync still registers on this token.
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 100 && Volatile.Read(ref _translateBusy) != 0; i++)
                    await Task.Delay(100).ConfigureAwait(false);
                await Task.Delay(200).ConfigureAwait(false);
                old.Dispose();
            }
            catch { /* ignore */ }
        });
    }

    private void Watch(string srtPath)
    {
        StopWatching();
        var dir = Path.GetDirectoryName(srtPath)!;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(srtPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _debounce = new System.Timers.Timer(450) { AutoReset = false };
        var timer = _debounce;
        void bounce()
        {
            try
            {
                timer.Stop();
                timer.Start();
            }
            catch (ObjectDisposedException)
            {
                // shutdown
            }
        }

        timer.Elapsed += (_, _) =>
        {
            try { OnSrtChanged(srtPath); }
            catch (Exception ex) { _log(ex.Message); }
        };
        _watcher.Changed += (_, _) => bounce();
        _watcher.Created += (_, _) => bounce();
        _watcher.EnableRaisingEvents = true;
        if (File.Exists(srtPath))
            OnSrtChanged(srtPath);
    }

    private void OnSrtChanged(string srtPath)
    {
        if (_disposed) return;
        var incoming = SubtitleFile.ParseSrt(srtPath);
        if (incoming.Count == 0) return;
        var profile = _getContentProfile();
        var route = _getMtRoute();
        PreviewTextSanitize.CleanAsrCues(incoming, _settings, profile);
        if (incoming.Count == 0) return;
        lock (_cueLock)
        {
            SubtitleFile.MergePreserveZh(_cues, incoming);
            RebuildTranslatedKeys();
            CueCount = _cues.Count;
            if (route.IsOff)
            {
                foreach (var c in _cues)
                {
                    if (string.IsNullOrWhiteSpace(c.Zh))
                        c.Zh = c.Text;
                }

                RebuildTranslatedKeys();
            }

            TranslatedCount = CountRealTranslations(_cues);
            RecalcFrontiers();
        }

        RefreshDisplaySub();
        _status(_buildStatusLine());
        _log($"前沿 · 原文 {MediaTimeFormat.Format(SubFrontier)}"
            + (ZhFrontier > 0 ? $" · 译文 {MediaTimeFormat.Format(ZhFrontier)}" : ""));
        _stateChanged();
        if (_wantsPreviewMt())
            _ = TryTranslatePendingAsync();
    }

    private void RecalcFrontiers()
    {
        SubFrontier = SubtitleFile.Frontier(_cues);
        ZhFrontier = SubtitleFile.TranslatedFrontier(_cues);
    }

    private void RebuildTranslatedKeys()
    {
        _translated.Clear();
        foreach (var c in _cues)
        {
            if (PreviewTextSanitize.IsPlaceholderText(c.Zh))
            {
                // Legacy「…」painted as ZH — drop so display falls back to source; allow retry.
                if (!string.IsNullOrWhiteSpace(c.Zh))
                    c.Zh = null;
                continue;
            }

            _translated[Key(c)] = 1;
            _mtSkip.TryRemove(Key(c), out _);
        }
    }

    private static int CountRealTranslations(IEnumerable<Cue> cues)
        => cues.Count(c => !PreviewTextSanitize.IsPlaceholderText(c.Zh));

    private static string Key(Cue c) => $"{c.Start:0.00}|{c.Text}";
}
