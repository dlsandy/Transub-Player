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
    public bool HasActiveJob => _jobId is not null;

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
        if (_factoryGate.Wait(0))
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
        // Only release when idle — mid-job ReleaseModel would dispose under ProcessAsync.
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
        Action<string> onFinished)
    {
        await _jobGate.WaitAsync(outerCt).ConfigureAwait(false);
        try
        {
            await CancelJobCoreAsync(CancellationToken.None).ConfigureAwait(false);
            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            var token = _jobCts.Token;
            _jobId = Guid.NewGuid().ToString("N")[..8];
            var id = _jobId;
            _log(Loc.Format("Main.Status.AsrJobCreated", id));
            _runTask = RunJobAsync(job, id, token, onDone, onFinished);
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
            if (shouldAbort?.Invoke() == true)
                throw new OperationCanceledException(ct);

            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _jobCts.Token;
            _jobId = Guid.NewGuid().ToString("N")[..8];
            var id = _jobId;
            _log(Loc.Format("Main.Status.AsrPrefetchJobCreated", id));
            _runTask = RunJobAsync(job, id, token, onDone: () => Task.CompletedTask, _ => { }, shouldAbort);
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
                await using var processor = _factory!.CreateBuilder().WithLanguage("auto").Build();
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
        var poll = _runTask;
        if (poll is not null)
        {
            try { await poll.WaitAsync(TimeSpan.FromSeconds(8), CancellationToken.None).ConfigureAwait(false); }
            catch { /* abandon after best-effort wait */ }
        }

        try
        {
            await _factoryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { ReleaseModel(); }
            finally { _factoryGate.Release(); }
        }
        catch
        {
            ReleaseModel();
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

            var duration = await AsrAudioExtract.ProbeDurationAsync(job.MediaPath, ct).ConfigureAwait(false);
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
                var cleanFrom = cues.Count;
                try
                {
                    wav = await AsrAudioExtract.ExtractWavAsync(job.MediaPath, start, len, ct).ConfigureAwait(false);
                    await _factoryGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await using var stream = File.OpenRead(wav);
                        await using var processor = string.Equals(whisperLang, "auto", StringComparison.OrdinalIgnoreCase)
                            ? _factory!.CreateBuilder().Build()
                            : _factory!.CreateBuilder().WithLanguage(whisperLang).Build();
                        await foreach (var seg in processor.ProcessAsync(stream, ct).ConfigureAwait(false))
                        {
                            var text = (seg.Text ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            var cueStart = start + seg.Start.TotalSeconds;
                            var cueEnd = start + seg.End.TotalSeconds;
                            if (cueEnd <= cueStart + 0.04) cueEnd = cueStart + 0.2;
                            cues.Add(new Cue
                            {
                                Index = cues.Count + 1,
                                Start = cueStart,
                                End = cueEnd,
                                Text = text,
                            });
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

                // Sanitize only new cues per chunk; full pass once at the end.
                PreviewTextSanitize.CleanAsrCues(cues, _settings, job.ContentProfile, cleanFrom);
                SubtitleFile.WriteSrt(sourceSrt, cues, chinese: false);

                if ((DateTime.UtcNow - lastFlush).TotalMilliseconds > 800)
                {
                    lastFlush = DateTime.UtcNow;
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }

            PreviewTextSanitize.CleanAsrCues(cues, _settings, job.ContentProfile);
            SubtitleFile.WriteSrt(sourceSrt, cues, chinese: false);

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

    private void ReleaseModel()
    {
        try { _factory?.Dispose(); } catch { /* ignore */ }
        _factory = null;
        _loadedModelPath = null;
    }

    private async Task CancelJobCoreAsync(CancellationToken ct)
    {
        var jobCts = _jobCts;
        var poll = _runTask;
        try { jobCts?.Cancel(); } catch { /* ignore */ }

        if (poll is not null)
        {
            try { await poll.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch { /* ignore */ }
        }

        if (ReferenceEquals(_runTask, poll))
            _runTask = null;
        _jobId = null;
        if (ReferenceEquals(_jobCts, jobCts))
        {
            _jobCts = null;
            try { jobCts?.Dispose(); } catch { /* ignore */ }
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
