using System.Text.Json;
using TransubPlayer.Localization;
using Whisper.net;
using Whisper.net.Ggml;

namespace TransubPlayer.Services;

/// <summary>
/// In-process ASR via Whisper.net (whisper.cpp). Replaces the former Python engine-lite HTTP session.
/// </summary>
internal sealed class AsrPipeline : IDisposable
{
    private const double ChunkSec = 90;
    private const double ChunkOverlapSec = 2;

    private readonly AppSettings _settings;
    private readonly Action<string> _status;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _jobGate = new(1, 1);
    private readonly SemaphoreSlim _factoryGate = new(1, 1);

    private WhisperFactory? _factory;
    private string? _loadedModelPath;
    private CancellationTokenSource? _jobCts;
    private Task? _runTask;
    private string? _jobId;
    private string _modelsRoot = "";

    public string EngineLabel => Loc.Get("Settings.Engine.Embedded");
    public string ModelsRoot => _modelsRoot;
    public EngineCapabilities? Capabilities { get; private set; }
    public string? JobId => _jobId;
    public bool IsConnected { get; private set; }
    /// <summary>
    /// True while a job is booked or its run task is still winding down after cancel.
    /// Must stay true until native Whisper releases the factory — otherwise ReleaseGpu /
    /// a second job can dispose Vulkan mid-<c>ProcessAsync</c> and freeze the GPU driver.
    /// </summary>
    public bool HasActiveJob => _jobId is not null || IsRunTaskAlive;

    private bool IsRunTaskAlive
    {
        get
        {
            var t = _runTask;
            return t is { IsCompleted: false };
        }
    }

    public AsrPipeline(AppSettings settings, Action<string> status, Action<string> log)
    {
        _settings = settings;
        _status = status;
        _log = log;
    }

    public Task EnsureReadyAsync(CancellationToken ct)
    {
        _modelsRoot = AsrModelStore.ResolveModelsRoot(_settings);
        IsConnected = true;
        Capabilities = AsrRuntime.EnrichCapabilities(
            EngineCapabilities.ForEmbedded(_modelsRoot), _settings);
        return Task.CompletedTask;
    }

    public Task RefreshCapabilitiesAsync(CancellationToken ct)
    {
        Capabilities = AsrRuntime.EnrichCapabilities(
            EngineCapabilities.ForEmbedded(_modelsRoot), _settings);
        return Task.CompletedTask;
    }

    public Task RebindAfterSettingsAsync(CancellationToken ct)
    {
        _modelsRoot = AsrModelStore.ResolveModelsRoot(_settings);
        // Never dispose the factory between ASR chunks — ProcessAsync may still need it.
        if (!HasActiveJob && _factoryGate.Wait(0))
        {
            try { ReleaseModel(); }
            finally { _factoryGate.Release(); }
        }

        Capabilities = AsrRuntime.EnrichCapabilities(
            EngineCapabilities.ForEmbedded(_modelsRoot), _settings);
        return Task.CompletedTask;
    }

    public async Task<RuntimePacks> EnsureAsrModelAsync(CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var preferred = ModelPicker.InstallTarget(_settings.AsrModel);
        var packs = RuntimePacks.FromDisk(_modelsRoot);
        if (AsrQualities.IsUsable(preferred, packs) || AsrModelStore.IsInstalled(_modelsRoot, preferred))
            return RuntimePacks.FromDisk(_modelsRoot);

        await AsrModelStore.EnsureTurboAsync(_modelsRoot, _settings.HfEndpoint, _status, _log, ct).ConfigureAwait(false);
        return RuntimePacks.FromDisk(_modelsRoot);
    }

    public Task<RuntimePacks> ProbePacksAsync(CancellationToken ct)
        => Task.FromResult(RuntimePacks.FromDisk(_modelsRoot));

    public Task DownloadModelsAsync(IReadOnlyList<string> modelIds, CancellationToken ct)
    {
        foreach (var id in modelIds)
        {
            ct.ThrowIfCancellationRequested();
            if (string.Equals(ModelPicker.InstallTarget(id), ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase))
                return AsrModelStore.EnsureTurboAsync(_modelsRoot, _settings.HfEndpoint, _status, _log, ct);
        }

        return Task.CompletedTask;
    }

    public Task EnsureGpuAsync(CancellationToken ct) => Task.CompletedTask;

    public Task ReleaseGpuAsync(CancellationToken ct)
    {
        // Only release when idle — mid-job ReleaseModel would dispose under ProcessAsync
        // (factory gate is free between chunks during WAV extract).
        if (HasActiveJob) return Task.CompletedTask;
        if (!_factoryGate.Wait(0))
            return Task.CompletedTask;
        try
        {
            if (HasActiveJob) return Task.CompletedTask;
            ReleaseModel();
        }
        finally
        {
            _factoryGate.Release();
        }

        return Task.CompletedTask;
    }

    public async Task StartJobAsync(
        AsrJobRequest job,
        CancellationToken outerCt,
        Func<Task> onDone,
        Action<string> onFinished,
        Func<Task>? onSourceFlushed = null)
    {
        await _jobGate.WaitAsync(outerCt).ConfigureAwait(false);
        try
        {
            await CancelJobCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForRunTaskIdleAsync(outerCt).ConfigureAwait(false);
            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            var token = _jobCts.Token;
            _jobId = Guid.NewGuid().ToString("N")[..8];
            var id = _jobId;
            _log(Loc.Format("Main.Status.AsrJobCreated", id));
            _runTask = RunJobAsync(job, id, token, onDone, onFinished, onSourceFlushed);
        }
        finally
        {
            _jobGate.Release();
        }
    }

    public async Task<bool> RunJobAndWaitAsync(
        AsrJobRequest job,
        CancellationToken ct,
        Func<bool>? shouldAbort = null)
    {
        // Hold the gate only for bookkeeping (same as StartJobAsync) so CancelJobAsync
        // can interrupt a long prefetch without waiting for the whole job to finish.
        Task run;
        await _jobGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (shouldAbort?.Invoke() == true)
                throw new OperationCanceledException(ct);

            await CancelJobCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitForRunTaskIdleAsync(ct).ConfigureAwait(false);
            if (shouldAbort?.Invoke() == true)
                throw new OperationCanceledException(ct);

            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _jobCts.Token;
            _jobId = Guid.NewGuid().ToString("N")[..8];
            var id = _jobId;
            _log(Loc.Format("Main.Status.AsrPrefetchJobCreated", id));
            _runTask = RunJobAsync(job, id, token, onDone: () => Task.CompletedTask, _ => { }, onSourceFlushed: null, shouldAbort);
            run = _runTask;
        }
        finally
        {
            _jobGate.Release();
        }

        try
        {
            await run.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<JsonElement> DetectLanguageAsync(
        string mediaPath,
        string? asrModel,
        double durationSec,
        double startSec,
        CancellationToken ct)
    {
        // Never overlap another Whisper ProcessAsync on the shared factory (Vulkan).
        await WaitUntilIdleAsync(ct).ConfigureAwait(false);
        await EnsureAsrModelAsync(ct).ConfigureAwait(false);
        var modelPath = AsrModelStore.TurboPath(_modelsRoot);
        // Reuse the shared factory — a second FromPath can double VRAM and OOM on Vulkan.
        string? wav = null;
        try
        {
            wav = await AsrAudioExtract.ExtractWavAsync(mediaPath, startSec, durationSec, ct).ConfigureAwait(false);
            await _factoryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureFactory(modelPath);
                await using var stream = File.OpenRead(wav);
                await using var processor = BuildProcessor("auto");
                var detected = "";
                await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(seg.Text))
                    {
                        detected = seg.Language ?? "";
                        if (!string.IsNullOrWhiteSpace(detected))
                            break;
                    }
                }

                var lang = MapWhisperLanguage(detected);
                var payload = new
                {
                    ok = !SourceLanguages.IsAuto(lang),
                    language = lang,
                    confidence = SourceLanguages.IsAuto(lang) ? 0.0 : 0.85,
                };
                return JsonSerializer.SerializeToElement(payload);
            }
            finally
            {
                _factoryGate.Release();
            }
        }
        finally
        {
            AsrAudioExtract.TryDeleteTemp(wav);
        }
    }

    public async Task CancelJobAsync(CancellationToken ct)
    {
        await _jobGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CancelJobCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _jobGate.Release();
        }
    }

    public Task CancelJobAsync() => CancelJobAsync(CancellationToken.None);

    /// <summary>
    /// After <see cref="CancelJobAsync"/>, wait until native Whisper has fully left the GPU.
    /// Safe to call when already idle. Does not hold the job gate for the whole wait so
    /// concurrent cancel can still run.
    /// </summary>
    public async Task WaitUntilIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? poll;
            await _jobGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                poll = _runTask;
                if (poll is null || poll.IsCompleted)
                {
                    await WaitForRunTaskIdleAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
            finally
            {
                _jobGate.Release();
            }

            try
            {
                await poll.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still winding down — loop and re-check under the gate.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Faulted — loop to clear bookkeeping.
            }
        }
    }

    public async Task PrepareShutdownAsync(CancellationToken ct)
    {
        try
        {
            await CancelJobAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Budgeted cancel may time out while the gate is contended — still wait below.
        }

        // Never dispose WhisperFactory while ProcessAsync may still touch it.
        // Honor the same shutdown budget — unbounded None can hang Window_Closing forever.
        try
        {
            await WaitUntilIdleAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            var poll = _runTask;
            if (poll is not null)
            {
                try { await poll.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch { /* best-effort within budget */ }
            }
        }

        try
        {
            await _factoryGate.WaitAsync(ct).ConfigureAwait(false);
            try { ReleaseModel(); }
            finally { _factoryGate.Release(); }
        }
        catch
        {
            // Timed out or cancelled — only release if nothing is still in ProcessAsync.
            if (!IsRunTaskAlive && _factoryGate.Wait(0))
            {
                try { ReleaseModel(); }
                finally { _factoryGate.Release(); }
            }
            else
                _log("ASR shutdown: factory kept (run still active or gate busy)");
        }
    }

    public void Detach() => ReleaseModel();

    public void Dispose()
    {
        try { _jobCts?.Cancel(); } catch { /* ignore */ }
        try { _runTask?.Wait(TimeSpan.FromSeconds(8)); } catch { /* ignore */ }
        _jobCts?.Dispose();
        try
        {
            if (_factoryGate.Wait(TimeSpan.FromSeconds(2)))
            {
                try { ReleaseModel(); }
                finally { _factoryGate.Release(); }
            }
            else
            {
                ReleaseModel();
            }
        }
        catch
        {
            ReleaseModel();
        }

        try { _jobGate.Dispose(); } catch { /* ignore */ }
        try { _factoryGate.Dispose(); } catch { /* ignore */ }
    }

    private async Task RunJobAsync(
        AsrJobRequest job,
        string jobId,
        CancellationToken ct,
        Func<Task> onDone,
        Action<string> onFinished,
        Func<Task>? onSourceFlushed = null,
        Func<bool>? shouldAbort = null)
    {
        var finished = false;
        void FinishOnce(string status)
        {
            if (finished) return;
            finished = true;
            try { onFinished(status); } catch { /* UI */ }
        }

        var sourceSrt = PreviewPaths.SourceSrt(job.MediaPath);
        Directory.CreateDirectory(job.OutputDir);
        var cues = job.SeedCues?.Select(c => new Cue
        {
            Index = c.Index,
            Start = c.Start,
            End = c.End,
            Text = c.Text,
            Zh = c.Zh,
        }).ToList() ?? [];
        var lastFlush = DateTime.UtcNow;
        var lastIncrementalFlush = DateTime.UtcNow;
        var incrementalSanitizeFrom = 0;
        var cleanFrom = 0;

        async Task FlushSourceAsync(bool incremental)
        {
            if (incremental)
            {
                PreviewTextSanitize.CleanAsrCues(cues, _settings, job.ContentProfile, incrementalSanitizeFrom);
                incrementalSanitizeFrom = cues.Count;
            }
            else
            {
                PreviewTextSanitize.CleanAsrCues(cues, _settings, job.ContentProfile, cleanFrom);
                cleanFrom = cues.Count;
                incrementalSanitizeFrom = cues.Count;
            }

            SubtitleFile.WriteSrt(sourceSrt, cues, chinese: false);
            if (onSourceFlushed is not null)
            {
                try { await onSourceFlushed().ConfigureAwait(false); }
                catch { /* UI / MT kick */ }
            }
        }

        try
        {
            await EnsureAsrModelAsync(ct).ConfigureAwait(false);
            var modelPath = AsrModelStore.TurboPath(_modelsRoot);
            await _factoryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureFactory(modelPath);
            }
            finally
            {
                _factoryGate.Release();
            }

            // Prefer playing mpv duration — skips a second mpv spawn and avoids the 3600s probe sentinel
            // overrunning short files (KnownDuration < 3600 would lose with a naive "longer wins" rule).
            double duration;
            if (job.KnownDurationSec > 1)
            {
                duration = job.KnownDurationSec;
                _log($"ASR duration: using player={duration:0.#}s");
            }
            else
            {
                duration = await AsrAudioExtract.ProbeDurationAsync(job.MediaPath, ct).ConfigureAwait(false);
                if (AsrAudioExtract.IsFallbackDuration(duration))
                {
                    // Prefetch / no player duration: do not burn ~1h of Whisper on a failed probe.
                    throw new InvalidOperationException(
                        Loc.Format("Main.Status.AsrFailed", $"duration probe failed (assumed {AsrAudioExtract.FallbackDurationSec:0}s)"));
                }
            }
            var whisperLang = MapWhisperLanguage(job.Language);
            var chunkStep = ChunkSec - ChunkOverlapSec;
            var totalChunks = Math.Max(1, (int)Math.Ceiling(duration / chunkStep));
            var firstChunk = job.StartFromSeconds > 0.5
                ? Math.Clamp((int)Math.Floor(job.StartFromSeconds / chunkStep), 0, Math.Max(0, totalChunks - 1))
                : 0;
            if (firstChunk > 0)
            {
                _log(Loc.Format("Main.Status.AsrFromPlayhead", MediaTimeFormat.Format(job.StartFromSeconds)));
                SubtitleFile.WriteSrt(sourceSrt, cues, chinese: false);
            }

            for (var chunk = firstChunk; chunk < totalChunks; chunk++)
            {
                ct.ThrowIfCancellationRequested();
                if (shouldAbort?.Invoke() == true)
                    throw new OperationCanceledException(ct);

                var start = chunk * chunkStep;
                var len = Math.Min(ChunkSec, Math.Max(0.5, duration - start));
                if (start >= duration - 0.25)
                    break;

                var pct = Math.Clamp((int)Math.Round(100.0 * (chunk + 1) / totalChunks), 1, 99);
                _status(Loc.Format("Main.Status.AsrProgress", pct, Loc.Get("Main.Status.AsrStageRecognizing")));
                _log(Loc.Format("Main.Status.AsrProgress", pct, $"{start:0}s"));

                string? wav = null;
                cleanFrom = cues.Count;
                incrementalSanitizeFrom = cleanFrom;
                try
                {
                    try
                    {
                        wav = await AsrAudioExtract.ExtractWavAsync(job.MediaPath, start, len, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (chunk > firstChunk || cues.Count > 0)
                    {
                        // Overestimated duration / EOF: keep cues already written instead of failing the job.
                        _log($"ASR extract ended early at {start:0.#}s · {ex.Message}");
                        break;
                    }

                    await _factoryGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await using var stream = File.OpenRead(wav);
                        await using var processor = BuildProcessor(whisperLang);
                        // Later chunks re-decode the overlap tail of the previous chunk — drop those cues.
                        var overlapFloor = chunk > firstChunk ? start + ChunkOverlapSec : double.NegativeInfinity;
                        await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
                        {
                            var text = (seg.Text ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            var cueStart = start + seg.Start.TotalSeconds;
                            if (cueStart < overlapFloor - 0.05) continue;
                            var cueEnd = start + seg.End.TotalSeconds;
                            if (cueEnd <= cueStart + 0.04) cueEnd = cueStart + 0.2;
                            cues.Add(new Cue
                            {
                                Index = cues.Count + 1,
                                Start = cueStart,
                                End = cueEnd,
                                Text = text,
                            });

                            var now = DateTime.UtcNow;
                            if ((now - lastIncrementalFlush).TotalSeconds >= 4)
                            {
                                lastIncrementalFlush = now;
                                await FlushSourceAsync(incremental: true).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        _factoryGate.Release();
                    }
                }
                finally
                {
                    AsrAudioExtract.TryDeleteTemp(wav);
                }

                await FlushSourceAsync(incremental: false).ConfigureAwait(false);

                if ((DateTime.UtcNow - lastFlush).TotalMilliseconds > 800)
                {
                    lastFlush = DateTime.UtcNow;
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }

            PreviewTextSanitize.CleanAsrCues(cues, _settings, job.ContentProfile);
            await FlushSourceAsync(incremental: false).ConfigureAwait(false);

            _log(Loc.Get("Main.Status.AsrJobDone"));
            await onDone().ConfigureAwait(false);
            FinishOnce("done");
        }
        catch (OperationCanceledException)
        {
            FinishOnce("cancelled");
        }
        catch (Exception ex)
        {
            _log(ex.Message);
            _status(Loc.Format("Main.Status.AsrFailed", ex.Message));
            FinishOnce("error");
        }
        finally
        {
            if (string.Equals(_jobId, jobId, StringComparison.Ordinal))
                _jobId = null;
        }
    }

    private void EnsureFactory(string modelPath)
    {
        AsrRuntime.Apply(_settings);
        if (_factory is not null
            && string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            return;

        ReleaseModel();
        _factory = WhisperFactory.FromPath(modelPath);
        _loadedModelPath = modelPath;
    }

    /// <summary>
    /// Build a Whisper processor. <see cref="WhisperProcessorBuilder.WithNoContext"/> turns off
    /// conditioning on previous text (whisper.cpp <c>-mc 0</c>), which otherwise causes
    /// forever-loop hallucinations (same cue every ~2s for minutes).
    /// </summary>
    private WhisperProcessor BuildProcessor(string whisperLang)
    {
        var builder = _factory!.CreateBuilder();
        if (string.Equals(whisperLang, "auto", StringComparison.OrdinalIgnoreCase))
            builder = builder.WithLanguageDetection();
        else
            builder = builder.WithLanguage(whisperLang);

        // Soft / music / silence often triggers stuck phrases; no-context is the standard fix.
        builder = builder.WithNoContext();
        return builder.Build();
    }

    private void ReleaseModel()
    {
        try { _factory?.Dispose(); } catch { /* ignore */ }
        _factory = null;
        _loadedModelPath = null;
    }

    /// <summary>
    /// Request cancel and wait for the run task. Never clears bookkeeping while native
    /// Whisper may still touch the factory — callers that need a free GPU must then
    /// <see cref="WaitForRunTaskIdleAsync"/>.
    /// </summary>
    private async Task CancelJobCoreAsync(CancellationToken ct)
    {
        var jobCts = _jobCts;
        var poll = _runTask;
        try { jobCts?.Cancel(); } catch { /* ignore */ }

        if (poll is not null)
        {
            // Prefer finishing cancel over abandoning: Vulkan whisper.cpp often needs
            // well beyond a few seconds to leave ProcessAsync after Cancel.
            try { await poll.WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Short outer budgets (media switch) may expire first — keep ownership.
            }
            catch
            {
                // Faulted / abandoned wait — still inspect IsCompleted below.
            }
        }

        if (poll is not null && !poll.IsCompleted)
        {
            _log("ASR 取消仍在收尾 · 保持 GPU 占用直至任务结束");
            return;
        }

        if (ReferenceEquals(_runTask, poll))
            _runTask = null;
        if (poll is not null || _jobId is not null)
            _jobId = null;
        if (ReferenceEquals(_jobCts, jobCts))
        {
            _jobCts = null;
            try { jobCts?.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>Block until any prior run task has fully exited (or throw if cancelled).</summary>
    private async Task WaitForRunTaskIdleAsync(CancellationToken ct)
    {
        var poll = _runTask;
        if (poll is null || poll.IsCompleted)
        {
            if (ReferenceEquals(_runTask, poll))
                _runTask = null;
            if (_jobId is not null && (poll is null || poll.IsCompleted))
                _jobId = null;
            return;
        }

        _log("等待上一识别任务释放 GPU…");
        try
        {
            await poll.WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("上一识别任务超时未释放 GPU，已拒绝启动新任务以免驱动卡死。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Faulted prior task — GPU should be free.
        }

        if (!poll.IsCompleted)
            throw new InvalidOperationException("上一识别任务仍未结束，已拒绝启动新任务以免驱动卡死。");

        if (ReferenceEquals(_runTask, poll))
            _runTask = null;
        _jobId = null;
        var leftover = _jobCts;
        if (leftover is not null)
        {
            _jobCts = null;
            try { leftover.Dispose(); } catch { /* ignore */ }
        }
    }

    private static string MapWhisperLanguage(string? language)
    {
        var norm = SourceLanguages.Normalize(language);
        if (SourceLanguages.IsAuto(norm))
            return "auto";
        return norm switch
        {
            SourceLanguages.Ja => "ja",
            SourceLanguages.Ko => "ko",
            SourceLanguages.Zh => "zh",
            SourceLanguages.En => "en",
            _ => "auto",
        };
    }
}
