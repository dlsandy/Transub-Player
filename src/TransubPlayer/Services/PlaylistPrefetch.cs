using System.IO;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// Sequentially pre-runs ASR (and optional MT) for upcoming playlist items when the live job is idle.
/// Shares the same engine session as live preview — never overlaps a live job.
/// </summary>
internal sealed class PlaylistPrefetch
{
    private const int MaxQueue = 24;

    private readonly AppSettings _settings;
    private readonly AsrPipeline _engine;
    private readonly Action<string> _status;
    private readonly Action<string> _log;
    private readonly Func<string, SceneProfile> _resolveScene;
    private readonly Func<CancellationToken, Task> _ensureTranslate;
    private readonly Func<bool> _translateWanted;
    private readonly Func<string?> _liveMediaPath;
    private readonly object _gate = new();
    private readonly Queue<string> _queue = new();
    private CancellationTokenSource? _cts;
    private bool _pumping;
    private volatile bool _liveBusy;
    private int _liveBusyEpoch;
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private string? _runningPath;

    public event Action<PrefetchUiState>? Changed;

    public PlaylistPrefetch(
        AppSettings settings,
        AsrPipeline engine,
        Action<string> status,
        Action<string> log,
        Func<string, SceneProfile> resolveScene,
        Func<CancellationToken, Task> ensureTranslate,
        Func<bool> translateWanted,
        Func<string?> liveMediaPath)
    {
        _settings = settings;
        _engine = engine;
        _status = status;
        _log = log;
        _resolveScene = resolveScene;
        _ensureTranslate = ensureTranslate;
        _translateWanted = translateWanted;
        _liveMediaPath = liveMediaPath;
    }

    public bool IsLiveBusy => _liveBusy;

    public bool IsRunning(string path)
    {
        var running = _runningPath;
        return running is not null
               && string.Equals(running, path, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsQueued(string path)
    {
        lock (_gate)
            return _queue.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsFailed(string path)
    {
        lock (_gate)
            return _failed.Contains(path);
    }

    private void Notify(PrefetchUiKind kind, string? path = null)
    {
        int count;
        lock (_gate) count = _queue.Count;
        try { Changed?.Invoke(new PrefetchUiState(kind, path, count)); }
        catch { /* UI */ }
    }

    /// <summary>Mark live preview as owning the engine. Returns an epoch for <see cref="ReleaseLiveBusy"/>.</summary>
    public int EnterLiveBusy()
    {
        var epoch = Interlocked.Increment(ref _liveBusyEpoch);
        _liveBusy = true;
        try { _cts?.Cancel(); } catch { /* stop in-flight prefetch */ }
        return epoch;
    }

    /// <summary>Clear live busy only if this epoch is still current (ignores stale onFinished).</summary>
    public void ReleaseLiveBusy(int epoch)
    {
        if (Volatile.Read(ref _liveBusyEpoch) != epoch)
            return;
        _liveBusy = false;
        _ = PumpAsync();
    }

    /// <summary>Unconditional busy flag (prefer <see cref="EnterLiveBusy"/> / <see cref="ReleaseLiveBusy"/>).</summary>
    public void SetLiveBusy(bool busy)
    {
        if (busy)
            EnterLiveBusy();
        else
            ReleaseLiveBusy(Volatile.Read(ref _liveBusyEpoch));
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        if (!_settings.PrefetchPlaylistSubtitles) return;
        // Align with open-path: no auto transcription → no background ASR.
        if (!_settings.AutoStartPreview) return;
        var added = 0;
        lock (_gate)
        {
            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string path;
                try { path = Path.GetFullPath(raw); }
                catch { continue; }

                if (!File.Exists(path)) continue;
                if (PreviewPaths.HasReadyAsr(path)) continue;
                if (_settings.PreferExternalSubtitle
                    && SubtitleFile.FindExistingSubtitle(path) is not null)
                    continue;
                if (_queue.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) continue;
                var live = _liveMediaPath();
                if (live is not null && string.Equals(live, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                while (_queue.Count >= MaxQueue)
                    _queue.Dequeue();

                _queue.Enqueue(path);
                added++;
            }
        }

        if (added > 0)
        {
            int count;
            lock (_gate) count = _queue.Count;
            _log($"列表预生成排队 +{added} · 待 {count}");
            Notify(PrefetchUiKind.Queued);
            _ = PumpAsync();
        }
    }

    public void Cancel()
    {
        lock (_gate) _queue.Clear();
        _runningPath = null;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        Notify(PrefetchUiKind.Idle);
    }

    public async Task StopAsync(int waitMs = 2000)
    {
        Cancel();
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Clamp(waitMs, 200, 10_000));
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (!_pumping) return;
            }

            await Task.Delay(40).ConfigureAwait(false);
        }
    }

    public void Dispose() => Cancel();

    private async Task PumpAsync()
    {
        if (!_settings.PrefetchPlaylistSubtitles) return;
        lock (_gate)
        {
            if (_pumping || _liveBusy) return;
            _pumping = true;
        }

        var prev = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        try { prev?.Cancel(); } catch { /* ignore */ }
        // Dispose previous only after it was cancelled and this pump owns the slot (!_pumping was false).
        try { prev?.Dispose(); } catch { /* ignore */ }
        var ct = _cts!.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_liveBusy || !_settings.PrefetchPlaylistSubtitles)
                    break;

                string? next = null;
                lock (_gate)
                {
                    while (_queue.Count > 0)
                    {
                        var candidate = _queue.Dequeue();
                        var live = _liveMediaPath();
                        if (live is not null && string.Equals(live, candidate, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (PreviewPaths.HasReadyAsr(candidate))
                            continue;
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    Notify(PrefetchUiKind.Idle);
                    break;
                }

                try
                {
                    _runningPath = next;
                    Notify(PrefetchUiKind.Running, next);
                    await PrefetchOneAsync(next, ct).ConfigureAwait(false);
                    _runningPath = null;
                    if (PreviewPaths.HasReadyAsr(next))
                        Notify(PrefetchUiKind.Ready, next);
                }
                catch (OperationCanceledException)
                {
                    _runningPath = null;
                    // Do not re-queue when live took the engine — avoids cancel thrash on rapid next/prev.
                    if (!_liveBusy)
                    {
                        lock (_gate)
                        {
                            if (!PreviewPaths.HasReadyAsr(next)
                                && !_queue.Any(p => string.Equals(p, next, StringComparison.OrdinalIgnoreCase)))
                                _queue.Enqueue(next);
                        }
                    }

                    break;
                }
                catch (Exception ex)
                {
                    _runningPath = null;
                    lock (_gate) _failed.Add(next);
                    Notify(PrefetchUiKind.Failed, next);
                    _log($"列表预生成失败 · {Path.GetFileName(next)} · {ex.Message}");
                }
            }
        }
        finally
        {
            lock (_gate) _pumping = false;
        }
    }

    private async Task PrefetchOneAsync(string mediaPath, CancellationToken ct)
    {
        var name = Path.GetFileName(mediaPath);
        if (!_settings.AutoStartPreview)
        {
            _log($"列表预生成跳过 · 未开自动原文提取 · {name}");
            return;
        }

        if (_settings.PreferExternalSubtitle
            && SubtitleFile.FindExistingSubtitle(mediaPath) is not null)
        {
            _log($"列表预生成跳过 · 已有外挂字幕 · {name}");
            return;
        }

        _status($"列表预生成 · {name}");
        _log($"列表预生成开始 · {name}");

        await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        var packs = await _engine.EnsureAsrModelAsync(ct).ConfigureAwait(false);
        var scene = _resolveScene(mediaPath);
        var asr = SceneProfiles.PickAsr(_settings.AsrModel, packs, scene.Language);
        var outDir = PreviewPaths.OutDir(mediaPath);
        Directory.CreateDirectory(outDir);
        var body = new AsrJobRequest(mediaPath, outDir, scene.Language, asr, scene.ContentProfile);
        var ok = await _engine.RunJobAndWaitAsync(body, ct, shouldAbort: () => _liveBusy).ConfigureAwait(false);
        if (!ok)
        {
            lock (_gate) _failed.Add(mediaPath);
            Notify(PrefetchUiKind.Failed, mediaPath);
            _log($"列表预生成未完成 · {name}");
            return;
        }

        lock (_gate) _failed.Remove(mediaPath);

        PreviewPaths.MarkAsrDone(mediaPath);

        var route = MtRoute.Resolve(scene.Language, _settings.TranslateTarget, scene.ContentProfile);
        if (_translateWanted() && MtRoute.WantsTranslation(route) && _settings.TranslateEnabled)
        {
            try
            {
                await PrefetchTranslateAsync(mediaPath, scene, route, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"列表预生成翻译跳过 · {name} · {ex.Message}");
            }
        }

        _status($"列表预生成完成 · {name}");
        _log($"列表预生成完成 · {name}");
    }

    private async Task PrefetchTranslateAsync(string mediaPath, SceneProfile scene, MtRoute route, CancellationToken ct)
    {
        var source = PreviewPaths.SourceSrt(mediaPath);
        var cues = SubtitleFile.ParseSrt(source);
        PreviewTextSanitize.CleanAsrCues(cues, _settings, scene.ContentProfile);
        if (cues.Count == 0) return;

        await _ensureTranslate(ct).ConfigureAwait(false);
        using var translate = TranslateClient.ForUrl(_settings.TranslateUrl);
        const int batch = 8;
        var work = cues.Where(c => !PreviewTextSanitize.IsPlaceholderText(c.Text)).ToList();
        for (var i = 0; i < work.Count; i += batch)
        {
            ct.ThrowIfCancellationRequested();
            var slice = work.Skip(i).Take(batch).ToList();
            if (slice.Count == 0) continue;
            var raw = await translate.TranslateBatchAsync(
                slice.Select(c => c.Text).ToList(), ct, route, scene.ContentProfile, _settings.TranslateModelId)
                .ConfigureAwait(false);
            var map = TranslateClient.ParseNumbered(raw, slice.Count);
            for (var j = 0; j < slice.Count; j++)
            {
                if (!map.TryGetValue(j + 1, out var zh) || string.IsNullOrWhiteSpace(zh)) continue;
                zh = PreviewTextSanitize.SanitizeMt(zh, slice[j].Text, _settings, scene.ContentProfile);
                if (!PreviewTextSanitize.IsPlaceholderText(zh)
                    && !PreviewTextSanitize.LooksLikeWrongTargetScript(zh, _settings.TranslateTarget))
                    slice[j].Zh = zh;
            }
        }

        PreviewTextSanitize.UnstickCrossCue(cues, _settings);
        foreach (var c in cues)
        {
            if (PreviewTextSanitize.IsPlaceholderText(c.Zh) && !string.IsNullOrWhiteSpace(c.Zh))
                c.Zh = null;
        }

        SubtitleFile.WriteDisplaySrt(PreviewPaths.TranslatedPreviewSrt(mediaPath, _settings.TranslateTarget), cues, SubtitleDisplayMode.Zh);
        SubtitleFile.WriteDisplaySrt(PreviewPaths.DualSrt(mediaPath), cues, SubtitleDisplayMode.Dual);
        SubtitleFile.WriteDisplaySrt(PreviewPaths.DisplaySrt(mediaPath), cues, SubtitleDisplayMode.Zh);
        WriteTranslationCache(mediaPath, cues);
    }

    private void WriteTranslationCache(string mediaPath, List<Cue> cues)
    {
        try
        {
            var path = PreviewPaths.TranslationCachePath(mediaPath, _settings.TranslateTarget);
            var lines = cues
                .Where(c => !string.IsNullOrWhiteSpace(c.Zh))
                .Select(c => $"{c.Start:0.00}\t{c.Text.Replace('\t', ' ')}\t{c.Zh!.Replace('\t', ' ')}");
            File.WriteAllLines(path, lines);
        }
        catch
        {
            // ignore
        }
    }

    public static void ApplyTranslationCache(List<Cue> cues, string mediaPath, string? translateTarget)
    {
        try
        {
            var path = PreviewPaths.TranslationCachePath(mediaPath, translateTarget);
            if (!File.Exists(path)) return;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                map[$"{parts[0]}|{parts[1]}"] = parts[2];
            }

            foreach (var c in cues)
            {
                if (map.TryGetValue($"{c.Start:0.00}|{c.Text}", out var zh))
                    c.Zh = zh;
            }
        }
        catch
        {
            // ignore
        }
    }
}
