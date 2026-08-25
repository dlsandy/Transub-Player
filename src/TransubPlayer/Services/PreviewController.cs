using System.IO;
using TransubPlayer.Controls;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal sealed class PreviewController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _status;
    private readonly Action<string> _log;
    private readonly MpvHost _host;
    private readonly MpvPlayer _mpv = new();
    private readonly PreviewSubtitleSync _subs;
    private readonly PreviewEngineSession _engine;
    private LlamaServerProcess? _llama;
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private bool? _translateReady;
    private SubtitleDisplayMode _displayMode;
    private SubtitleDisplayMode _lastContentMode = SubtitleDisplayMode.Zh;
    private readonly HashSet<string> _skipGapPrompts = new(StringComparer.OrdinalIgnoreCase);
    private bool _depsCheckedForStart;
    private bool _waitingForFirstZh;
    private CancellationTokenSource? _waitZhCts;
    private PlaylistPrefetch? _prefetch;
    private int _disposed;
    private bool _previewRetryAvailable;
    private bool _manualPreviewNeeded;
    private bool _mtDisabledForSession;
    private PresetGapReport? _pendingGapReport;
    private int _mediaGeneration;
    private int _onlineSubtitleBusy;
    private CancellationTokenSource? _previewRunCts;
    private ExternalSubOrigin _externalOrigin = ExternalSubOrigin.None;
    private bool _bootstrapActive;
    private BootstrapPhase _bootstrapPhase = BootstrapPhase.None;
    private string _bootstrapPhaseTitle = "";
    private string _bootstrapPhaseDetail = "";

    private enum BootstrapPhase
    {
        None,
        ConnectingEngine,
        CheckingModels,
        DownloadingModel,
        PreparingTranslate,
        LoadingCache,
        StartingAsr,
        GeneratingSource,
        GeneratingZh,
    }

    public string? MediaPath { get; private set; }
    public bool UsingExistingSub { get; private set; }
    public string? ActiveSubPath { get; private set; }
    /// <summary>Control-bar source: Off / Online / Local / Live (realtime ASR).</summary>
    public SubtitleSourceKind ActiveSource
        => string.IsNullOrWhiteSpace(MediaPath) ? SubtitleSourceKind.Off : SourcePreference;

    private SubtitleSourceKind SourcePreference
        => Enum.TryParse<SubtitleSourceKind>(_settings.SubtitleSource, ignoreCase: true, out var kind)
            ? kind
            : SubtitleSourceKind.Live;

    private void SaveSubtitleSource(SubtitleSourceKind kind)
    {
        _settings.SubtitleSource = kind.ToString();
        _settings.SaveSoon();
    }

    public bool HasLocalSubtitle
        => !string.IsNullOrWhiteSpace(MediaPath)
           && SubtitleFile.FindExistingSubtitle(MediaPath) is not null;
  /// <summary>字幕来源为「本地字幕」：不连识别引擎，控制栏不显示预设/版式芯片。</summary>
    public bool IsLocalSubtitleSource => ActiveSource == SubtitleSourceKind.Local;
    /// <summary>走 ASR/翻译转写路径（非成片外挂、非仅本地字幕来源）。</summary>
    public bool ShowPreviewChrome
        => !string.IsNullOrWhiteSpace(MediaPath)
           && !UsingExistingSub
           && !IsLocalSubtitleSource;
    public double Duration { get; private set; }
    public double Position { get; private set; }
    public bool Paused { get; private set; } = true;
    public string AsrModel { get; private set; } = "";
    public string EngineDetail { get; private set; } = "引擎未连接";
    public PlaybackPreset ActivePreset { get; private set; } = PlaybackPresets.Get(PlaybackPresets.AutoSpeed);
    public PlaybackPreset? MatchedPreset { get; private set; }
    public bool IsEnglishSource => PlaybackPresets.IsEnglishSource(ActivePreset);
    public int CueCount => _subs.CueCount;
    public int TranslatedCount => _subs.TranslatedCount;
    public double SubFrontier => _subs.SubFrontier;
    public double ZhFrontier => _subs.ZhFrontier;
    public bool WaitingForZh => _waitingForFirstZh;
    public string WaitZhOverlayTitle { get; private set; } = "";
    public string WaitZhOverlayDetail { get; private set; } = "";
    public bool ShowOpeningBootstrap => _bootstrapActive && !_waitingForFirstZh;
    public string OpeningBootstrapTitle => _bootstrapPhaseTitle;
    public string OpeningBootstrapDetail => _bootstrapPhaseDetail;
    public SubtitleDisplayMode DisplayMode => _displayMode;
    /// <summary>Last 译文/原文/双语 choice (used when display is Off so UI can keep the chip selection).</summary>
    public SubtitleDisplayMode LastContentMode => _lastContentMode;
    public bool TranslateEnabled => _settings.TranslateEnabled;
    public bool? TranslateReady => _translateReady;
    public bool PreviewRetryAvailable => _previewRetryAvailable;
    public bool ShowStartPreviewAction =>
        ShowPreviewChrome
        && _manualPreviewNeeded
        && !_bootstrapActive;
    public bool ShowSwitchToPreviewAction =>
        UsingExistingSub
        && !IsLocalSubtitleSource
        && !string.IsNullOrWhiteSpace(MediaPath);
    public bool PresetInstallAvailable => _pendingGapReport?.HasGaps == true;
    public PresetGapReport? PendingGapReport => _pendingGapReport;
    /// <summary>User chose 译文/双语 but no translated cues yet (screen shows source via FormatCueBody).</summary>
    public bool ShowingZhPending
        => !string.IsNullOrWhiteSpace(MediaPath)
           && !UsingExistingSub
           && WantsPreviewMt
           && (_displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
           && TranslatedCount == 0;

    public int Volume => _mpv.Volume;
    public bool Muted => _mpv.Muted;
    public double Speed => _mpv.Speed;
    public bool SubVisible => _mpv.SubVisible;
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    public event Action? StateChanged;
    public event Action? MediaEnded;
    public event Action<int, int>? VideoSizeChanged;
    /// <summary>Fired after engine model probe so UI can refresh preset「需安装」badges.</summary>
    public event Action? PacksChanged;

    /// <summary>UI hook: ask user how to resolve missing preset deps. Runs on UI thread via caller.</summary>
    public Func<PresetGapReport, Task<PresetSetupChoice>>? OfferPresetSetupAsync { get; set; }
    public Func<SubtitleCatPickRequest, Task<SubtitleCatResult?>>? OfferSubtitleCatPickAsync { get; set; }
    public Func<Task<bool>>? OfferOnlineSubtitlePromptAsync { get; set; }
    /// <summary>English UI + English source: true=install MT, false=ASR only, null=dismiss.</summary>
    public Func<Task<bool?>>? OfferEnglishSourceChoiceAsync { get; set; }

    public PreviewController(AppSettings settings, MpvHost host, Action<string> status, Action<string> log)
    {
        _settings = settings;
        _host = host;
        _status = status;
        _log = log;
        _displayMode = SubtitleDisplayModeUtil.Parse(settings.SubtitleMode);
        if (SubtitleDisplayModeUtil.IsContentMode(_displayMode))
            _lastContentMode = _displayMode;
        _engine = new PreviewEngineSession(settings, status, PlayerLog.WriteEngine);
        _subs = new PreviewSubtitleSync(
            settings,
            status,
            log,
            OnSubtitleStateChanged,
            () => ActivePreset,
            () => WantsPreviewMt,
            () => _translateReady,
            v => _translateReady = v,
            EnsureTranslateAsync,
            () => _displayMode,
            () => Position,
            ApplySub,
            () => _mpv.ReloadSubtitle(),
            () => BuildStatusLine(),
            OnZhFrontierProgress,
            OnZhTranslationFailed);
        _mpv.Log += log;
        _mpv.TimeChanged += t => Position = t;
        _mpv.DurationChanged += d =>
        {
            Duration = d;
            StateChanged?.Invoke();
        };
        _mpv.PauseChanged += p =>
        {
            Paused = p;
            if (!p && _waitingForFirstZh)
                CancelWaitForFirstZh(userOverride: true);
            else if (!p && ShowPreviewChrome)
                PublishStatus(BuildStatusLine());
            StateChanged?.Invoke();
        };
        _mpv.VolumeChanged += _ => StateChanged?.Invoke();
        _mpv.MuteChanged += _ => StateChanged?.Invoke();
        _mpv.SpeedChanged += _ => StateChanged?.Invoke();
        _mpv.EofReached += ended =>
        {
            if (ended)
                MediaEnded?.Invoke();
        };
        _mpv.VideoSizeChanged += (w, h) =>
        {
            VideoWidth = w;
            VideoHeight = h;
            VideoSizeChanged?.Invoke(w, h);
        };
        _prefetch = new PlaylistPrefetch(
            settings,
            _engine,
            status,
            log,
            path =>
            {
                var preset = PlaybackPresets.Resolve(settings.PresetId, path, out _);
                return PlaybackPresets.WithTranslateTarget(preset, settings.TranslateTarget);
            },
            EnsureTranslateAsync,
            () => _settings.TranslateEnabled,
            () => MediaPath);
        _prefetch.Changed += state => PrefetchChanged?.Invoke(state);
    }

    /// <summary>UI hook for playlist badges.</summary>
    public event Action<PrefetchUiState>? PrefetchChanged;

    /// <summary>After current media ends, queue ASR (and MT) for remaining playlist items.</summary>
    public void EnqueuePlaylistPrefetch(IEnumerable<string> paths)
        => _prefetch?.Enqueue(paths);

    public void CancelPlaylistPrefetch()
        => _prefetch?.Cancel();

    public bool IsPrefetchRunning(string path)
        => _prefetch?.IsRunning(path) == true;

    public bool IsPrefetchQueued(string path)
        => _prefetch?.IsQueued(path) == true;

    public bool IsPrefetchFailed(string path)
        => _prefetch?.IsFailed(path) == true;

    public void SkipWaitForFirstZh() => CancelWaitForFirstZh(userOverride: true);

    public async Task EnsurePlayerAsync()
    {
        if (_mpv.IsRunning) return;
        var mpv = MpvLocator.Find()
            ?? throw new MpvMissingException();
        _host.EnsureHandle();
        await _host.Dispatcher.InvokeAsync(() => { });
        await _mpv.StartAsync(mpv, _host.Hwnd, _settings).ConfigureAwait(false);
        await _host.Dispatcher.InvokeAsync(() => _host.HookEmbeddedChildren());
        _mpv.SetVolume(_settings.Volume);
        _mpv.SetSpeed(_settings.Speed <= 0 ? 1.0 : _settings.Speed);
        _mpv.ApplySubtitleSettings(_settings);
        ApplyDisplayVisibility();
        _log($"mpv · {mpv}");
    }

    public async Task OpenMediaAsync(string path, CancellationToken ct)
    {
        var gen = Interlocked.Increment(ref _mediaGeneration);
        // Block prefetch before cancelling the old job so onFinished cannot start a pump mid-open.
        var liveBusyEpoch = _prefetch?.EnterLiveBusy() ?? 0;
        var handOffToPreview = false;
        try
        {
            CancelPreviewRun();
            CancelWaitForFirstZh();
            SavePlaybackPosition();

            // Fast switch: update player and clear old subtitles before waiting on engine cancel.
            MediaPath = path;
            UsingExistingSub = false;
            ActiveSubPath = null;
            _externalOrigin = ExternalSubOrigin.None;
            VideoWidth = 0;
            VideoHeight = 0;
            Position = 0;
            Duration = 0;
            _translateReady = null;
            _previewRetryAvailable = false;
            _manualPreviewNeeded = false;
            _mtDisabledForSession = false;
            _pendingGapReport = null;
            _subs.Reset();
            _mpv.ClearSubtitle();
            ResolvePreset();

            await EnsurePlayerAsync().ConfigureAwait(false);
            ThrowIfStaleMedia(gen, ct);

            if (MediaSourceHelper.IsNonLocalMedia(path))
            {
                var streamAutoPlay = _settings.AutoPlayOnOpen;
                _mpv.LoadFile(path, streamAutoPlay);
                Paused = !streamAutoPlay;
                StateChanged?.Invoke();
                _ = CancelStaleEngineJobAsync();
                EndBootstrap();
                PublishStatus(Loc.Get(MediaSourceHelper.IsScreenCapture(path)
                    ? "Main.Status.ScreenCapture"
                    : "Main.Status.StreamPlaybackOnly"));
                return;
            }

            var pref = SourcePreference;

            string? existing = null;
            if (pref == SubtitleSourceKind.Local)
                existing = SubtitleFile.FindExistingSubtitle(path);
            else if (pref == SubtitleSourceKind.Live && _settings.PreferExternalSubtitle)
                existing = SubtitleFile.FindExistingSubtitle(path);

            var waitZh = pref == SubtitleSourceKind.Live && ShouldWaitForFirstZh() && existing is null;
            var autoPlay = _settings.AutoPlayOnOpen && !waitZh;
            ThrowIfStaleMedia(gen, ct);
            _mpv.LoadFile(path, autoPlay);
            Paused = !autoPlay;
            if (waitZh)
                BeginWaitForFirstZh();

            StateChanged?.Invoke();

            // Tear down the previous ASR job without blocking the new file on HTTP/poll teardown.
            _ = CancelStaleEngineJobAsync();

            if (_settings.RememberPlaybackPosition)
            {
                var resumeAt = PlaybackPositionStore.Load(path);
                if (resumeAt > 1)
                {
                    _mpv.Seek(resumeAt);
                    _log($"续播 {MediaTimeFormat.Format(resumeAt)}");
                    ShowOsd(Loc.Format("Main.Osd.Resumed", MediaTimeFormat.Format(resumeAt)), 2200);
                }
            }

            if (pref == SubtitleSourceKind.Off)
            {
                if (_displayMode != SubtitleDisplayMode.Off)
                    SetDisplayMode(SubtitleDisplayMode.Off);
                EndBootstrap();
                PublishStatus(Loc.Get("Main.SubSource.OffOpen"));
                return;
            }

            string? onlineFetched = null;
            if (existing is null && pref == SubtitleSourceKind.Online)
                onlineFetched = await TryFetchSubtitleCatAsync(path, gen, ct, announceAsrFallback: false).ConfigureAwait(false);
            if (existing is null && pref == SubtitleSourceKind.Live)
            {
                if (!_settings.FetchSubtitleFromSubtitleCat
                    && OfferOnlineSubtitlePromptAsync is not null
                    && UserTips.ShouldShow(_settings, UserTips.OfferOnlineSub))
                {
                    UserTips.Dismiss(_settings, UserTips.OfferOnlineSub);
                    if (await OfferOnlineSubtitlePromptAsync().ConfigureAwait(false))
                    {
                        _settings.FetchSubtitleFromSubtitleCat = true;
                        _settings.SaveSoon();
                    }
                }

                if (_settings.FetchSubtitleFromSubtitleCat)
                    onlineFetched = await TryFetchSubtitleCatAsync(path, gen, ct, announceAsrFallback: true).ConfigureAwait(false);
            }
            if (onlineFetched is not null)
                existing = onlineFetched;

            ThrowIfStaleMedia(gen, ct);

            if (existing is not null)
            {
                CancelWaitForFirstZh();
                UsingExistingSub = true;
                _subs.SetUsingExistingSub(true);
                _externalOrigin = onlineFetched is not null
                    ? ExternalSubOrigin.Online
                    : ExternalSubOrigin.Local;
                ActiveSubPath = existing;
                _mpv.SetSubtitle(existing);
                EndBootstrap();
                if (onlineFetched is not null)
                {
                    PublishStatus(Loc.Get("SubtitleCat.Loaded") + Loc.Get("Main.Status.ExternalSubHint"), $"外挂字幕 {existing}");
                    StartPlaybackAfterSubtitleReady();
                }
                else
                {
                    PublishStatus(Loc.Get("Main.Status.ExternalSub") + Loc.Get("Main.Status.ExternalSubHint"), $"使用现有字幕 {existing}");
                    ShowOsd(Loc.Get("Main.Osd.ExternalSub"), 2000);
                    MaybeOfferExternalSubHint();
                }
                StateChanged?.Invoke();
                return;
            }

            if (pref == SubtitleSourceKind.Local)
            {
                EndBootstrap();
                PublishStatus(Loc.Get("Main.SubSource.LocalOnlyNone"));
                return;
            }

            if (pref == SubtitleSourceKind.Online)
            {
                EndBootstrap();
                PublishStatus(Loc.Get("Main.SubSource.OnlineNone"));
                return;
            }

            if (!_settings.AutoStartPreview)
            {
                EndBootstrap();
                _manualPreviewNeeded = true;
                PublishStatus(Loc.Get("Main.Status.PreviewNotAuto"));
                StateChanged?.Invoke();
                return;
            }

            _manualPreviewNeeded = false;

            await MaybeOfferEnglishSourceChoiceAsync(gen, ct).ConfigureAwait(false);
            ThrowIfStaleMedia(gen, ct);

            // Preview start takes a new live-busy epoch; keep open-phase busy until then
            // (do not Release here — that would briefly free the engine for prefetch).
            _manualPreviewNeeded = false;
        handOffToPreview = true;
            _ = StartPreviewInBackgroundAsync(gen, ct);
        }
        finally
        {
            if (!handOffToPreview)
                _prefetch?.ReleaseLiveBusy(liveBusyEpoch);
        }
    }

    /// <summary>Manual online subtitle search from the context menu (ignores auto-fetch setting).</summary>
    public async Task FindOnlineSubtitlesAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");
        if (Interlocked.CompareExchange(ref _onlineSubtitleBusy, 1, 0) != 0)
            return;

        var gen = Volatile.Read(ref _mediaGeneration);
        var path = MediaPath!;
        try
        {
            var saved = await TryFetchSubtitleCatAsync(path, gen, ct, announceAsrFallback: false)
                .ConfigureAwait(false);
            if (saved is null || !IsCurrentMedia(gen, path))
                return;

            CancelPreviewRun();
            CancelWaitForFirstZh();
            _subs.Reset();
            await CancelStaleEngineJobAsync().ConfigureAwait(false);
            ThrowIfStaleMedia(gen, ct);

            UsingExistingSub = true;
            _subs.SetUsingExistingSub(true);
            _externalOrigin = ExternalSubOrigin.Online;
            ApplySub(saved);
            EndBootstrap();
            _prefetch?.SetLiveBusy(false);
            if (_displayMode == SubtitleDisplayMode.Off)
                SetDisplayMode(_lastContentMode);
            PublishStatus(Loc.Get("SubtitleCat.Loaded") + Loc.Get("Main.Status.ExternalSubHint"), $"外挂字幕 {saved}");
            StartPlaybackAfterSubtitleReady();
            StateChanged?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _onlineSubtitleBusy, 0);
        }
    }

    /// <summary>Switch control-bar subtitle source: Off / Online / Local / Live.</summary>
    public async Task SelectSubtitleSourceAsync(SubtitleSourceKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");

        SaveSubtitleSource(kind);

        switch (kind)
        {
            case SubtitleSourceKind.Off:
                SetDisplayMode(SubtitleDisplayMode.Off);
                return;
            case SubtitleSourceKind.Online:
                await FindOnlineSubtitlesAsync(ct).ConfigureAwait(false);
                return;
            case SubtitleSourceKind.Local:
                await UseLocalSubtitleAsync(ct).ConfigureAwait(false);
                return;
            case SubtitleSourceKind.Live:
                await UseLiveSubtitleAsync(ct).ConfigureAwait(false);
                return;
            default:
                return;
        }
    }

    public async Task UseLocalSubtitleAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");

        var local = SubtitleFile.FindExistingSubtitle(MediaPath);

        var gen = Volatile.Read(ref _mediaGeneration);
        var path = MediaPath!;
        CancelPreviewRun();
        CancelWaitForFirstZh();
        _subs.Reset();
        await StopPreviewEngineIfNeededAsync().ConfigureAwait(false);
        if (!IsCurrentMedia(gen, path))
            return;

        if (local is null)
        {
            UsingExistingSub = false;
            _subs.SetUsingExistingSub(false);
            _externalOrigin = ExternalSubOrigin.None;
            ActiveSubPath = null;
            _mpv.ClearSubtitle();
            EndBootstrap();
            _prefetch?.SetLiveBusy(false);
            PublishStatus(Loc.Get("Main.SubSource.LocalOnlyNone"));
            ShowOsd(Loc.Get("Main.SubSource.LocalNone"), 2000);
            StateChanged?.Invoke();
            return;
        }

        UsingExistingSub = true;
        _subs.SetUsingExistingSub(true);
        _externalOrigin = ExternalSubOrigin.Local;
        ApplySub(local);
        EndBootstrap();
        _prefetch?.SetLiveBusy(false);
        if (_displayMode == SubtitleDisplayMode.Off)
            SetDisplayMode(_lastContentMode);
        PublishStatus(Loc.Get("Main.Status.ExternalSub") + Loc.Get("Main.Status.ExternalSubHint"), $"使用现有字幕 {local}");
        ShowOsd(Loc.Get("Main.Osd.ExternalSub"), 2000);
        MaybeOfferExternalSubHint();
        StateChanged?.Invoke();
    }

    private async Task UseLiveSubtitleAsync(CancellationToken ct)
    {
        if (UsingExistingSub)
        {
            await StartPreviewIgnoringExternalAsync(ct).ConfigureAwait(false);
        }
        else if (_previewRunCts is null && CueCount == 0)
        {
            await StartPreviewAsync(ct).ConfigureAwait(false);
        }

        if (_displayMode == SubtitleDisplayMode.Off)
            SetDisplayMode(_lastContentMode);
        else
            StateChanged?.Invoke();
    }

    private async Task<string?> TryFetchSubtitleCatAsync(
        string mediaPath,
        int gen,
        CancellationToken ct,
        bool announceAsrFallback)
    {
        string FallbackSuffix()
            => announceAsrFallback ? " · " + Loc.Get("SubtitleCat.FallbackAsr") : "";

        try
        {
            var query = MediaSearchQuery.BuildFromPath(mediaPath);
            if (string.IsNullOrWhiteSpace(query.Primary))
                return null;

            PublishStatus(Loc.Format("SubtitleCat.Searching", query.Primary));

            SubtitleCatResult? picked = null;
            if (OfferSubtitleCatPickAsync is not null)
            {
                // Open picker immediately; search runs inside the window so UI does not freeze.
                picked = await OfferSubtitleCatPickAsync(new SubtitleCatPickRequest(
                        mediaPath,
                        query,
                        Results: [],
                        InitialProvider: OnlineSubtitleProvider.SubtitleCat,
                        SearchOnOpen: true))
                    .ConfigureAwait(false);
            }

            ThrowIfStaleMedia(gen, ct);
            if (picked is null)
            {
                PublishStatus(Loc.Get("SubtitleCat.Skipped") + FallbackSuffix());
                return null;
            }

            PublishStatus(Loc.Get("SubtitleCat.Downloading"));
            var saved = await SubtitleCatClient.DownloadAndSaveAsync(picked, mediaPath, ct).ConfigureAwait(false);
            _log($"{picked.Source} · {picked.Title} → {saved}");
            PublishStatus(Loc.Format("SubtitleCat.LoadedFrom", picked.Source));
            ShowOsd(Loc.Format("SubtitleCat.LoadedFrom", picked.Source), 2000);
            return saved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = Loc.Format("SubtitleCat.Failed", ex.Message);
            _log(msg);
            PublishStatus(msg + FallbackSuffix());
            ShowOsd(Loc.Get("SubtitleCat.FailedShort"), 2800);
            return null;
        }
    }

    private void CancelPreviewRun()
    {
        try { _previewRunCts?.Cancel(); } catch { /* ignore */ }
    }

    /// <summary>Single-flight preview start: a newer run cancels the previous one.</summary>
    private async Task StartPreviewInBackgroundAsync(int gen, CancellationToken ct)
    {
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prev = Interlocked.Exchange(ref _previewRunCts, runCts);
        try { prev?.Cancel(); } catch { /* ignore */ }

        var mediaPath = MediaPath;
        var busyEpoch = 0;
        try
        {
            busyEpoch = _prefetch?.EnterLiveBusy() ?? 0;
            await StartPreviewAsync(gen, runCts.Token, busyEpoch).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // StartPreviewAsync finally / job onFinished own ReleaseLiveBusy.
        }
        catch (Exception ex)
        {
            if (mediaPath is null || !IsCurrentMedia(gen, mediaPath))
                return;
            MarkPreviewFailed(ex.Message);
            // If ASR job already started, onFinished will release; otherwise finally did.
            if (_waitingForFirstZh)
                FinishWaitForFirstZh(play: true, "转写启动失败，已开始播放");
        }
        finally
        {
            Interlocked.CompareExchange(ref _previewRunCts, null, runCts);
            try { runCts.Dispose(); } catch { /* ignore */ }
        }
    }

    public async Task StartPreviewIgnoringExternalAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");

        SaveSubtitleSource(SubtitleSourceKind.Live);
        UsingExistingSub = false;
        _subs.SetUsingExistingSub(false);
        ActiveSubPath = null;
        _externalOrigin = ExternalSubOrigin.None;
        _previewRetryAvailable = false;
        _manualPreviewNeeded = false;
        _mpv.ClearSubtitle();
        PreviewPaths.InvalidatePreviewOutputs(MediaPath);
        if (_displayMode == SubtitleDisplayMode.Off)
            SetDisplayMode(_lastContentMode);
        ShowOsd(Loc.Get("Main.Osd.StartPreview"));
        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
    }

    public Task RetryPreviewAsync(CancellationToken ct)
        => StartPreviewIgnoringExternalAsync(ct);

    public Task StartPreviewAsync(CancellationToken ct)
        => StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct);

    private async Task StartPreviewAsync(int gen, CancellationToken ct, int liveBusyEpoch)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");

        ResolvePreset();
        var jobStarted = false;
        try
        {
            await CancelPriorEngineJobAsync(ct).ConfigureAwait(false);
            ThrowIfStaleMedia(gen);

            SetBootstrapPhase(BootstrapPhase.ConnectingEngine);
            await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            ThrowIfStaleMedia(gen);

            SetBootstrapPhase(BootstrapPhase.CheckingModels);
            _status(Loc.Get("Main.Status.CheckingAsr"));
            try
            {
                var prePacks = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
                if (!prePacks.TinyInstalled)
                    SetBootstrapPhase(BootstrapPhase.DownloadingModel);
            }
            catch
            {
                // probe optional; EnsureTinyModel will retry
            }

            var packs = await _engine.EnsureTinyModelAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            ThrowIfStaleMedia(gen);

            // Opening a file: never block on a modal gap dialog — fall back and start preview.
            if (!_depsCheckedForStart)
            {
                if (!await EnsurePresetDependenciesAsync(packs, ct, promptUi: false).ConfigureAwait(false))
                {
                    ThrowIfStaleMedia(gen);
                    EndBootstrap();
                    _status(Loc.Get("Main.Status.PresetCancelled"));
                    if (_waitingForFirstZh)
                        FinishWaitForFirstZh(play: true, "转写未启动，已开始播放");
                    StateChanged?.Invoke();
                    return;
                }
            }
            else
            {
                _depsCheckedForStart = false;
            }

            ThrowIfStaleMedia(gen);
            packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            ThrowIfStaleMedia(gen);
            AsrModel = PlaybackPresets.PickAsr(ActivePreset, packs);
            var label = _engine.EngineLabel;
            EngineDetail = $"{label} · {AsrModel}";
            var preferredAsr = ActivePreset.AsrChain.FirstOrDefault();
            if (!string.Equals(preferredAsr, AsrModel, StringComparison.OrdinalIgnoreCase))
            {
                _log($"ASR 回退 {preferredAsr} → {AsrModel}（专科模型未安装或 GPU 未就绪）");
                ShowOsd(Loc.Format("Main.Osd.UsingFastPreview", AsrDisplayName(AsrModel)), 2200);
            }

            if (MatchedPreset is not null && PlaybackPresets.Get(_settings.PresetId).IsAuto)
            {
                _log($"文件名匹配 · {MatchedPreset.Name}");
                ShowOsd(Loc.Format("Main.Osd.PresetMatched", MatchedPreset.Name), 2000);
            }

            var mediaPath = MediaPath!;
            var outDir = PreviewPaths.OutDir(mediaPath);
            Directory.CreateDirectory(outDir);
            _subs.SetOutputPaths(
                PreviewPaths.SourceSrt(mediaPath),
                PreviewPaths.TranslatedPreviewSrt(mediaPath, _settings.TranslateTarget),
                PreviewPaths.DualSrt(mediaPath),
                PreviewPaths.DisplaySrt(mediaPath));

            if (PreviewPaths.HasReadyAsr(mediaPath))
            {
                SetBootstrapPhase(BootstrapPhase.LoadingCache);
                // Cached ASR: still need MT for wait-for-zh, but do not block UI on llama for long.
                if (WantsPreviewMt)
                {
                    SetBootstrapPhase(BootstrapPhase.PreparingTranslate);
                    PublishStatus(Loc.Get("Main.Status.PreparingMt"));
                    await EnsureTranslateAsync(ct).ConfigureAwait(false);
                    ThrowIfStaleMedia(gen);
                }

                await LoadCachedPreviewAsync(ct).ConfigureAwait(false);
                if (_waitingForFirstZh)
                    SetBootstrapPhase(BootstrapPhase.GeneratingZh);
                else
                    EndBootstrap();
                return;
            }

            ThrowIfStaleMedia(gen);
            // Drop stale SRT so WatchSource does not flash previous-run cues.
            PreviewPaths.InvalidatePreviewOutputs(mediaPath);
            _subs.WatchSource();

            // Start ASR before llama so status leaves「连接中」and source lines can appear.
            SetBootstrapPhase(BootstrapPhase.StartingAsr);
            PublishStatus(WantsPreviewMt ? Loc.Get("Main.Status.StartingAsrMt") : Loc.Get("Main.Status.StartingAsr"));

            var enableVad = _engine.SupportsSileroVad;
            if (!enableVad)
            _log("精简引擎无 onnxruntime，转写关闭 Silero VAD");
            var body = PlaybackPresets.BuildJob(mediaPath, outDir, AsrModel, ActivePreset, enableVad);
            await _engine.StartJobAsync(
                body,
                ct,
                async () =>
                {
                    if (!IsCurrentMedia(gen, mediaPath)) return;
                    _subs.OnSourceChanged();
                    await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
                    if (IsCurrentMedia(gen, mediaPath))
                        PreviewPaths.MarkAsrDone(mediaPath);
                    _previewRetryAvailable = false;
                    PublishStatus(BuildStatusLine() + Loc.Get("Main.Status.AsrDone"));
                    MaybeOfferQualityHandoffTip();
                },
                terminal =>
                {
                    if (!IsCurrentMedia(gen, mediaPath))
                        return;
                    if (terminal == "error")
                        _previewRetryAvailable = true;
                    else if (terminal == "cancelled")
                        PublishStatus(Loc.Get("Main.Status.PreviewCancelled"));
                    _prefetch?.ReleaseLiveBusy(liveBusyEpoch);
                    StateChanged?.Invoke();
                }).ConfigureAwait(false);
            jobStarted = true;

            SetBootstrapPhase(BootstrapPhase.GeneratingSource);
            if (!_waitingForFirstZh)
                EndBootstrap();

            PublishStatus(BuildStatusLine(), $"任务 {_engine.JobId} · 预设 {PresetLogName} · {AsrModel} · {label}");
            MaybeOfferLagMentalModelTip();
            _log(PreviewTextSanitize.DescribeReady(_settings));
            if (_settings.TextSanitizeEnabled && JaAsrDomainLexicon.LoadedFromPath is { } lexPath)
                _log("域词库 · " + lexPath);
            StateChanged?.Invoke();

            if (WantsPreviewMt)
            {
                try
                {
                    PublishStatus(Loc.Get("Main.Status.AsrStartedMt"));
                    await EnsureTranslateAsync(ct).ConfigureAwait(false);
                    ThrowIfStaleMedia(gen);
                    await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
                    if (IsCurrentMedia(gen, mediaPath))
                        PublishStatus(BuildStatusLine());
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log("翻译准备：" + ex.Message);
                }
            }
        }
        finally
        {
            if (!jobStarted)
            {
                _prefetch?.ReleaseLiveBusy(liveBusyEpoch);
                if (!_waitingForFirstZh)
                    EndBootstrap();
            }
        }
    }

    private void ThrowIfStaleMedia(int gen, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed != 0 || gen != Volatile.Read(ref _mediaGeneration))
            throw new OperationCanceledException();
    }

    /// <summary>Cancel prior ASR job without cancelling the active preview run token.</summary>
    private async Task CancelPriorEngineJobAsync(CancellationToken ct)
    {
        _subs.StopWatching();
        await _engine.CancelJobAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Engine-only cancel for media switch — does not touch subtitle watcher or preview run.</summary>
    private async Task CancelStaleEngineJobAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(ChildProcessLifetime.HttpBudget);
            await _engine.CancelJobAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("切换取消引擎任务：" + ex.Message);
        }
    }

    /// <summary>Cancel in-flight preview and release GPU when leaving the preview path (e.g. local subtitles).</summary>
    private async Task StopPreviewEngineIfNeededAsync()
    {
        if (!_engine.HasActiveJob && !_engine.IsConnected)
            return;

        try
        {
            using var cts = new CancellationTokenSource(ChildProcessLifetime.HttpBudget);
            await _engine.CancelJobAsync(cts.Token).ConfigureAwait(false);
            await _engine.ReleaseGpuAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("停止识别引擎：" + ex.Message);
        }
    }

    private bool IsCurrentMedia(int gen, string mediaPath)
        => _disposed == 0
           && gen == Volatile.Read(ref _mediaGeneration)
           && string.Equals(MediaPath, mediaPath, StringComparison.OrdinalIgnoreCase);

    private async Task LoadCachedPreviewAsync(CancellationToken ct)
    {
        _subs.WatchSource();
        _subs.OnSourceChanged();
        _subs.MergeZhCache(MediaPath!);
        if (WantsPreviewMt)
            await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
        _previewRetryAvailable = false;
        PublishStatus(BuildStatusLine() + Loc.Get("Main.Status.PrefetchApplied"));
        ShowOsd(Loc.Get("Main.Osd.PrefetchLoaded"), 1800);
        _log($"使用预生成字幕 · {Path.GetFileName(MediaPath)}");
        MaybeOfferQualityHandoffTip();
        StateChanged?.Invoke();
    }

    public void SetPreset(string presetId)
        => _ = SetPresetAsync(presetId, CancellationToken.None);

    public async Task SetPresetAsync(string presetId, CancellationToken ct)
    {
        var prevMt = ActivePreset.Mt;
        var prevAsr = AsrModel;

        _settings.PresetId = string.IsNullOrWhiteSpace(presetId) ? PlaybackPresets.AutoSpeed : presetId.Trim();
        _settings.Save();
        ResolvePreset();
        StateChanged?.Invoke();

        if (IsLocalSubtitleSource || UsingExistingSub || string.IsNullOrWhiteSpace(MediaPath))
            return;

        RuntimePacks? packs = null;
        var depsOk = true;
        try
        {
            await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            packs = await _engine.EnsureTinyModelAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            depsOk = await EnsurePresetDependenciesAsync(packs, ct).ConfigureAwait(false);
            if (depsOk)
                _depsCheckedForStart = true;
        }
        catch (Exception ex)
        {
            _log("检查预设依赖：" + ex.Message);
            _depsCheckedForStart = false;
        }

        if (!depsOk)
        {
            _status("已取消按此预设启动转写 · 影片仍可播放");
            _depsCheckedForStart = false;
            if (_waitingForFirstZh)
                FinishWaitForFirstZh(play: true, "转写未启动，已开始播放");
            return;
        }

        if (!ShowPreviewChrome || !_settings.AutoStartPreview)
            return;

        var newAsr = packs is not null
            ? PlaybackPresets.PickAsr(ActivePreset, packs)
            : AsrModel;
        var mtChanged = prevMt != ActivePreset.Mt;
        var asrSame = !string.IsNullOrWhiteSpace(prevAsr)
                      && string.Equals(prevAsr, newAsr, StringComparison.OrdinalIgnoreCase);

        if (mtChanged && asrSame && CueCount > 0)
        {
            await RetranslateLivePreviewAsync(
                ct,
                "Main.Osd.PresetRetranslating",
                "Main.Osd.PresetMtOff").ConfigureAwait(false);
            return;
        }

        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
    }

    public async Task<PresetGapReport?> ProbePresetGapsAsync(PlaybackPreset preset, CancellationToken ct)
    {
        try
        {
            await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            var packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
            if (!packs.TinyInstalled)
                packs = await _engine.EnsureTinyModelAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            return BuildGapReport(preset, packs);
        }
        catch (Exception ex)
        {
            _log("探测预设依赖失败：" + ex.Message);
            return null;
        }
    }

    public void TogglePause() => _mpv.TogglePause();

    public void Seek(double seconds)
    {
        if (Duration > 0)
            seconds = Math.Clamp(seconds, 0, Duration);
        else if (seconds < 0)
            seconds = 0;
        Position = seconds;
        _mpv.Seek(seconds);
        _subs.FlushPendingSubReload();
        StateChanged?.Invoke();
    }

    public void SeekRelative(double delta)
    {
        var target = Position + delta;
        if (Duration > 0)
            target = Math.Clamp(target, 0, Duration);
        Position = Math.Max(0, target);
        _mpv.SeekRelative(delta);
        _subs.FlushPendingSubReload();
        StateChanged?.Invoke();
    }

    public void SeekPercent(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (Duration > 0)
            Position = Duration * (percent / 100.0);
        _mpv.SeekPercent(percent);
        _subs.FlushPendingSubReload();
        StateChanged?.Invoke();
    }

    /// <summary>Frontier the current display mode can follow (same rule as lag OSD).</summary>
    public double EffectiveReadyFrontier()
    {
        if (UsingExistingSub || _displayMode == SubtitleDisplayMode.Off) return Duration;
        return _displayMode == SubtitleDisplayMode.Source
            ? SubFrontier
            : ZhFrontier > 0 ? ZhFrontier : SubFrontier;
    }

    public void FrameStep(bool forward = true) => _mpv.FrameStep(forward);
    public void SetVolume(int volume)
    {
        _mpv.SetVolume(volume);
        _settings.Volume = _mpv.Volume;
        _settings.SaveSoon();
    }
    public void AdjustVolume(int delta) => SetVolume(_mpv.Volume + delta);
    public void ToggleMute() => _mpv.ToggleMute();
    public void CycleSpeed()
    {
        _mpv.CycleSpeed();
        _settings.Speed = _mpv.Speed;
        _settings.SaveSoon();
    }
    public void ResetSpeed()
    {
        _mpv.ResetSpeed();
        _settings.Speed = 1.0;
        _settings.SaveSoon();
    }
    public void SetSpeed(double speed)
    {
        _mpv.SetSpeed(speed);
        _settings.Speed = _mpv.Speed;
        _settings.SaveSoon();
    }
    public void ToggleSubVisible()
    {
        if (_displayMode == SubtitleDisplayMode.Off || !_mpv.SubVisible)
            SetDisplayMode(_lastContentMode);
        else
            SetDisplayMode(SubtitleDisplayMode.Off);
    }
    public string? Screenshot() => _mpv.Screenshot(_settings.ResolveScreenshotDir());

    public void NudgeSubDelay(double deltaSeconds)
    {
        _settings.SubDelaySec = Math.Clamp(Math.Round(_settings.SubDelaySec + deltaSeconds, 1), -30, 30);
        _settings.SaveSoon();
        _mpv.SetSubDelay(_settings.SubDelaySec);
        var sign = _settings.SubDelaySec >= 0 ? "+" : "";
        ShowOsd(Loc.Format("Main.Osd.SubSync", sign, _settings.SubDelaySec), 1800);
    }

    public void ApplyPlayerSettings()
    {
        _mpv.ApplySubtitleSettings(_settings);
        _mpv.ApplyPlaybackSettings(_settings);
        ApplyDisplayVisibility();
    }

    /// <summary>
    /// After Settings dialog save: rebind engine if install/URL/models changed, and align translate with menu toggle.
    /// </summary>
    public async Task ApplySettingsSideEffectsAsync(
        string prevEngineSource,
        string prevEnginePath,
        string prevEngineUrl,
        string prevModelsPath,
        string prevTranslateUrl,
        bool prevTranslateEnabled,
        string prevTranslateTarget,
        CancellationToken ct = default)
    {
        var engineChanged =
            !string.Equals(prevEngineSource, _settings.EngineSource, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(prevEnginePath, _settings.EngineInstallPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(prevEngineUrl, _settings.EngineUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(prevModelsPath, _settings.ModelsPath, StringComparison.OrdinalIgnoreCase);

        if (engineChanged)
        {
            PresetReadiness.ClearLivePacks();
            try
            {
                await _engine.RebindAfterSettingsAsync(ct).ConfigureAwait(false);
                EngineDetail = _engine.EngineLabel;
                _log("已按新设置重新绑定引擎");
                var packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
                PublishPacks(packs);
            }
            catch (Exception ex)
            {
                _log("重新绑定引擎：" + ex.Message);
                PublishStatus(Loc.Get("Main.Status.EngineSaved"), ex.Message);
            }
        }

        var translateUrlChanged = !string.Equals(
            prevTranslateUrl.Trim().TrimEnd('/'),
            (_settings.TranslateUrl ?? "").Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

        if (prevTranslateEnabled != _settings.TranslateEnabled || translateUrlChanged)
            await ApplyTranslateEnabledAsync(ct).ConfigureAwait(false);

        var targetChanged = !string.Equals(
            TranslateTargets.Normalize(prevTranslateTarget),
            TranslateTargets.Normalize(_settings.TranslateTarget),
            StringComparison.OrdinalIgnoreCase);
        if (targetChanged)
            await ApplyTranslateTargetChangeAsync(ct).ConfigureAwait(false);

        StateChanged?.Invoke();
    }

    private async Task ApplyTranslateTargetChangeAsync(CancellationToken ct)
    {
        ResolvePreset();
        PacksChanged?.Invoke();
        await RetranslateLivePreviewAsync(
            ct,
            "Main.Osd.TranslateTargetRetranslating",
            "Main.Osd.TranslateTargetSourceOnly",
            refreshOutputPaths: true).ConfigureAwait(false);
    }

    private async Task RetranslateLivePreviewAsync(
        CancellationToken ct,
        string osdRetranslatingKey,
        string osdSourceOnlyKey,
        bool refreshOutputPaths = false)
    {
        if (string.IsNullOrWhiteSpace(MediaPath) || UsingExistingSub)
            return;

        if (refreshOutputPaths)
            RefreshTranslatedOutputPaths();

        ResetTranslateStack();
        _subs.ClearTranslations();
        _subs.MergeZhCache(MediaPath!);

        if (!WantsPreviewMt)
        {
            _subs.RefreshDisplaySub();
            ShowOsd(Loc.Get(osdSourceOnlyKey), 2200);
            StateChanged?.Invoke();
            return;
        }

        PublishStatus(Loc.Get("Main.Status.Retranslating"));
        ShowOsd(Loc.Get(osdRetranslatingKey), 2800);
        MaybeOfferTranslateTargetEnTip();
        await EnsureTranslateAsync(ct).ConfigureAwait(false);
        await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    private void MaybeOfferTranslateTargetEnTip()
    {
        if (!TranslateTargets.IsEnglish(_settings)) return;
        if (!UserTips.ShouldShow(_settings, UserTips.TranslateTargetEn)) return;
        UserTips.Dismiss(_settings, UserTips.TranslateTargetEn);
        ShowOsd(Loc.Get("Main.Osd.TranslateTargetEnTip"), 4500);
    }

    private void ResetTranslateStack()
    {
        _translateReady = null;
        try { _llama?.Dispose(); } catch { /* ignore */ }
        _llama = null;
    }

    public async Task ApplyTranslateEnabledAsync(CancellationToken ct = default)
    {
        if (_settings.TranslateEnabled && WantsPreviewMt)
        {
            await EnsureTranslateAsync(ct).ConfigureAwait(false);
            await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
        }
        else
        {
            _translateReady = null;
            _subs.RefreshDisplaySub();
        }

        StateChanged?.Invoke();
    }

    public void SavePlaybackPosition()
    {
        if (!_settings.RememberPlaybackPosition || string.IsNullOrWhiteSpace(MediaPath)) return;
        PlaybackPositionStore.Save(MediaPath, Position);
    }

    public void ClearPlaybackPosition()
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;
        PlaybackPositionStore.Save(MediaPath, 0);
    }
    public void ShowOsd(string text, int durationMs = 1200) => _mpv.ShowOsd(text, durationMs);

    public void SetDisplayMode(SubtitleDisplayMode mode)
    {
        if (mode == SubtitleDisplayMode.Off)
        {
            _displayMode = SubtitleDisplayMode.Off;
            _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(mode);
            _settings.SubVisibleOnStart = false;
            _settings.SaveSoon();
            ApplyDisplayVisibility();
            StateChanged?.Invoke();
            return;
        }

        if (UsingExistingSub)
        {
            // 成片外挂只有显隐，无译文/原文/双语版式。
            if (!_mpv.SubVisible)
                _mpv.SetSubVisible(true);
            _displayMode = mode;
            if (SubtitleDisplayModeUtil.IsContentMode(mode))
                _lastContentMode = mode;
            _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(mode);
            _settings.SubVisibleOnStart = true;
            _settings.SaveSoon();
            StateChanged?.Invoke();
            return;
        }

        _displayMode = mode;
        if (SubtitleDisplayModeUtil.IsContentMode(mode))
            _lastContentMode = mode;
        _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(mode);
        _settings.SubVisibleOnStart = true;
        _settings.SaveSoon();
        ApplyDisplayVisibility();
        _subs.RefreshDisplaySub();
        StateChanged?.Invoke();
    }

    private void ApplyDisplayVisibility()
    {
        var wantVisible = _displayMode != SubtitleDisplayMode.Off;
        if (_mpv.SubVisible != wantVisible)
            _mpv.SetSubVisible(wantVisible);
    }

    public async Task ToggleTranslateAsync()
    {
        _settings.TranslateEnabled = !_settings.TranslateEnabled;
        _settings.Save();
        await ApplyTranslateEnabledAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public async Task CancelJobAsync()
    {
        CancelPreviewRun();
        _subs.StopWatching();
        await _engine.CancelJobAsync().ConfigureAwait(false);
    }

    /// <summary>Stop playback, unload media, cancel preview/ASR, and return to idle player state.</summary>
    public async Task CloseMediaAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;

        Interlocked.Increment(ref _mediaGeneration);
        _prefetch?.SetLiveBusy(true);
        CancelPreviewRun();
        CancelWaitForFirstZh();
        SavePlaybackPosition();
        CancelPlaylistPrefetch();

        try { _mpv.StopPlayback(); } catch { /* ignore */ }
        _mpv.ClearSubtitle();

        _subs.Reset();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ChildProcessLifetime.HttpBudget);
        try
        {
            await _engine.CancelJobAsync(cts.Token).ConfigureAwait(false);
            await _engine.ReleaseGpuAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("停止释放引擎：" + ex.Message);
        }

        _prefetch?.SetLiveBusy(false);

        MediaPath = null;
        UsingExistingSub = false;
        ActiveSubPath = null;
        _externalOrigin = ExternalSubOrigin.None;
        VideoWidth = 0;
        VideoHeight = 0;
        Position = 0;
        Duration = 0;
        Paused = true;
        _translateReady = null;
        _previewRetryAvailable = false;

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Stop audio/video immediately (e.g. before hiding the window on close).
    /// Safe if player is not ready; <see cref="ShutdownAsync"/> still stops again.
    /// </summary>
    public void StopPlaybackImmediate()
    {
        try { _mpv.StopPlayback(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Stop playback first, then release ASR/MT GPU and processes we spawned. Safe to call more than once.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { SavePlaybackPosition(); } catch { /* ignore */ }
        // Silence video immediately before GPU/cache teardown (may take up to HttpBudget).
        StopPlaybackImmediate();
        CancelPreviewRun();
        CancelWaitForFirstZh();
        try
        {
            if (_prefetch is not null)
                await _prefetch.StopAsync().ConfigureAwait(false);
        }
        catch { /* ignore */ }
        _prefetch = null;
        _subs.Dispose();
        using var cts = new CancellationTokenSource(ChildProcessLifetime.HttpBudget);
        try
        {
            await _engine.PrepareShutdownAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("退出清理引擎：" + ex.Message);
        }

        try { _llama?.Dispose(); } catch { /* ignore */ }
        _llama = null;
        try { _translateGate.Dispose(); } catch { /* ignore */ }
        try { _engine.Dispose(); } catch { /* ignore */ }
        try { _mpv.Dispose(); } catch { /* ignore */ }
        try { _settings.Save(); } catch { /* flush debounced */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { SavePlaybackPosition(); } catch { /* ignore */ }
        CancelWaitForFirstZh();
        try { _prefetch?.Dispose(); } catch { /* ignore */ }
        _prefetch = null;
        try { _subs.Dispose(); } catch { /* ignore */ }
        try { _llama?.Dispose(); } catch { /* ignore */ }
        _llama = null;
        try { _translateGate.Dispose(); } catch { /* ignore */ }
        try { _engine.Dispose(); } catch { /* ignore */ }
        try { _mpv.Dispose(); } catch { /* ignore */ }
        try { _settings.Save(); } catch { /* flush debounced */ }
    }

    private bool WantsPreviewMt =>
        ActivePreset.Mt != MtPromptKind.Off && _settings.TranslateEnabled && !_mtDisabledForSession;

    private bool ShouldWaitForFirstZh()
        => _settings.WaitForFirstZhBeforePlay
           && _settings.AutoPlayOnOpen
           && _settings.AutoStartPreview
           && WantsPreviewMt
           && !IsEnglishSource;

    private double WaitZhTargetSeconds
    {
        get
        {
            var minutes = Math.Clamp(_settings.WaitForZhMinutes, 0, 30);
            return minutes <= 0 ? 0.05 : minutes * 60.0;
        }
    }

    private void BeginWaitForFirstZh()
    {
        _waitingForFirstZh = true;
        if (_settings.AutoStartPreview && !_bootstrapActive)
            SetBootstrapPhase(BootstrapPhase.ConnectingEngine);
        RefreshWaitZhOverlay();
        _status($"等待译文至 {MediaTimeFormat.Format(WaitZhTargetSeconds)}… 影片已暂停");
        ShowOsd($"等待译文至 {MediaTimeFormat.Format(WaitZhTargetSeconds)}", 2200);
        StartWaitZhTimeout();
        StateChanged?.Invoke();
    }

    private void OnSubtitleStateChanged()
    {
        RefreshBootstrapProgress();
        StateChanged?.Invoke();
    }

    private void SetBootstrapPhase(BootstrapPhase phase, string? detail = null)
    {
        _bootstrapActive = true;
        _bootstrapPhase = phase;
        _bootstrapPhaseTitle = BootstrapPhaseTitle(phase);
        if (detail is not null)
            _bootstrapPhaseDetail = detail;
        else
            RefreshBootstrapDetail();
        RefreshWaitZhOverlay();
        StateChanged?.Invoke();
    }

    private void EndBootstrap()
    {
        if (!_bootstrapActive && _bootstrapPhase == BootstrapPhase.None) return;
        _bootstrapActive = false;
        _bootstrapPhase = BootstrapPhase.None;
        _bootstrapPhaseTitle = "";
        _bootstrapPhaseDetail = "";
        RefreshWaitZhOverlay();
        StateChanged?.Invoke();
    }

    private void RefreshBootstrapProgress()
    {
        if (!_bootstrapActive) return;
        if (_bootstrapPhase == BootstrapPhase.GeneratingSource
            && _waitingForFirstZh
            && WantsPreviewMt
            && (CueCount > 0 || SubFrontier > 2))
        {
            _bootstrapPhase = BootstrapPhase.GeneratingZh;
            _bootstrapPhaseTitle = BootstrapPhaseTitle(_bootstrapPhase);
        }

        RefreshBootstrapDetail();
        RefreshWaitZhOverlay();
    }

    private void RefreshBootstrapDetail()
    {
        _bootstrapPhaseDetail = _bootstrapPhase switch
        {
            BootstrapPhase.ConnectingEngine => _engine.IsConnected
                ? EngineDetail
                : Loc.Get("Main.Bootstrap.PleaseWait"),
            BootstrapPhase.CheckingModels => string.IsNullOrWhiteSpace(AsrModel)
                ? Loc.Get("Main.Bootstrap.PleaseWait")
                : AsrDisplayName(AsrModel),
            BootstrapPhase.DownloadingModel => Loc.Get("Main.Bootstrap.PleaseWait"),
            BootstrapPhase.PreparingTranslate => Loc.Get("Main.Bootstrap.PleaseWait"),
            BootstrapPhase.LoadingCache => Loc.Get("Main.Bootstrap.PleaseWait"),
            BootstrapPhase.StartingAsr => string.IsNullOrWhiteSpace(AsrModel)
                ? Loc.Get("Main.Bootstrap.PleaseWait")
                : AsrDisplayName(AsrModel),
            BootstrapPhase.GeneratingSource => Loc.Format(
                "Main.Bootstrap.SourceProgress",
                MediaTimeFormat.Format(SubFrontier)),
            BootstrapPhase.GeneratingZh => BuildWaitZhDetail(),
            _ => _bootstrapPhaseDetail,
        };
    }

    private static string BootstrapPhaseTitle(BootstrapPhase phase)
        => phase switch
        {
            BootstrapPhase.ConnectingEngine => Loc.Get("Main.Bootstrap.ConnectingEngine"),
            BootstrapPhase.CheckingModels => Loc.Get("Main.Bootstrap.CheckingModels"),
            BootstrapPhase.DownloadingModel => Loc.Get("Main.Bootstrap.DownloadingModel"),
            BootstrapPhase.PreparingTranslate => Loc.Get("Main.Bootstrap.PreparingTranslate"),
            BootstrapPhase.LoadingCache => Loc.Get("Main.Bootstrap.LoadingCache"),
            BootstrapPhase.StartingAsr => Loc.Get("Main.Bootstrap.StartingAsr"),
            BootstrapPhase.GeneratingSource => Loc.Get("Main.Bootstrap.GeneratingSource"),
            BootstrapPhase.GeneratingZh => Loc.Get("Main.WaitZh.Title"),
            _ => "",
        };

    private string BuildWaitZhDetail()
    {
        var need = WaitZhTargetSeconds;
        var now = ZhFrontier;
        return need <= 0.1
            ? Loc.Format("Main.WaitZh.ProgressFirst", MediaTimeFormat.Format(now))
            : Loc.Format(
                "Main.WaitZh.Progress",
                MediaTimeFormat.Format(now),
                MediaTimeFormat.Format(need));
    }

    private void OnZhFrontierProgress()
    {
        if (!_waitingForFirstZh) return;
        RefreshBootstrapProgress();
        StateChanged?.Invoke();
        if (IsZhWaitSatisfied())
            FinishWaitForFirstZh(play: true, "译文已就绪，开始播放");
    }

    private bool IsZhWaitSatisfied()
    {
        var frontier = ZhFrontier;
        var need = WaitZhTargetSeconds;
        if (frontier + 0.05 >= need) return true;
        if (Duration > 1 && frontier + 0.5 >= Duration) return true;
        return false;
    }

    private void RefreshWaitZhOverlay()
    {
        if (!_waitingForFirstZh)
        {
            WaitZhOverlayTitle = "";
            WaitZhOverlayDetail = "";
            return;
        }

        if (_bootstrapActive)
        {
            WaitZhOverlayTitle = _bootstrapPhaseTitle;
            WaitZhOverlayDetail = _bootstrapPhaseDetail;
            return;
        }

        WaitZhOverlayTitle = Loc.Get("Main.WaitZh.Title");
        WaitZhOverlayDetail = BuildWaitZhDetail();
    }

    private void OnZhTranslationFailed()
    {
        if (!_waitingForFirstZh) return;
        FinishWaitForFirstZh(play: true, "翻译模型未就绪，已开始播放");
    }

    private void CancelWaitForFirstZh(bool userOverride = false)
    {
        if (!_waitingForFirstZh) return;
        _waitingForFirstZh = false;
        EndBootstrap();
        DisposeWaitZhCts();
        if (userOverride)
        {
            _log("用户手动播放 · 不再等待译文覆盖");
            PublishStatus(BuildStatusLine());
        }
        StateChanged?.Invoke();
    }

    private void FinishWaitForFirstZh(bool play, string? osdMessage = null)
    {
        if (!_waitingForFirstZh) return;
        _waitingForFirstZh = false;
        EndBootstrap();
        DisposeWaitZhCts();
        if (play)
            StartPlaybackAfterSubtitleReady();
        if (!string.IsNullOrWhiteSpace(osdMessage))
            ShowOsd(osdMessage, 1800);
        _status(BuildStatusLine());
        StateChanged?.Invoke();
    }

    private void StartPlaybackAfterSubtitleReady()
    {
        if (!Paused) return;
        _mpv.SetPause(false);
    }

    private void StartWaitZhTimeout()
    {
        DisposeWaitZhCts();
        _waitZhCts = new CancellationTokenSource();
        var ct = _waitZhCts.Token;
        var timeoutSec = Math.Clamp(WaitZhTargetSeconds * 2.5 + 60, 120, 900);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(timeoutSec), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !_waitingForFirstZh) return;
                FinishWaitForFirstZh(play: true, "等待译文超时，已开始播放");
            }
            catch (OperationCanceledException)
            {
                // replaced or released
            }
        }, ct);
    }

    private void DisposeWaitZhCts()
    {
        var cts = _waitZhCts;
        _waitZhCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        cts.Dispose();
    }

    private string PresetLogName
    {
        get
        {
            var selected = PlaybackPresets.Get(_settings.PresetId);
            if (selected.IsAuto && MatchedPreset is not null)
                return $"{MatchedPreset.Name}（自动）";
            return selected.Name;
        }
    }

    private void ResolvePreset()
    {
        ActivePreset = PlaybackPresets.Resolve(_settings.PresetId, MediaPath, out var matched);
        MatchedPreset = matched;
        ActivePreset = PlaybackPresets.WithTranslateTarget(ActivePreset, _settings.TranslateTarget);
    }

    private void RefreshTranslatedOutputPaths()
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;
        var mediaPath = MediaPath;
        _subs.SetOutputPaths(
            PreviewPaths.SourceSrt(mediaPath),
            PreviewPaths.TranslatedPreviewSrt(mediaPath, _settings.TranslateTarget),
            PreviewPaths.DualSrt(mediaPath),
            PreviewPaths.DisplaySrt(mediaPath));
    }

    private void PublishPacks(RuntimePacks packs)
    {
        PresetReadiness.UpdateLivePacks(packs, _engine.EngineKind);
        PacksChanged?.Invoke();
    }

    private async Task<bool> EnsurePresetDependenciesAsync(
        RuntimePacks packs,
        CancellationToken ct,
        bool promptUi = true)
    {
        var report = BuildGapReport(ActivePreset, packs);
        if (!report.HasGaps)
        {
            _pendingGapReport = null;
            return true;
        }

        var skipKey = GapSkipKey(ActivePreset);
        if (_skipGapPrompts.Contains(skipKey))
        {
            _log("跳过预设依赖提示 · " + report.SummaryLine());
            return true;
        }

        // Open-media path: never block on a modal. Prefer available models and keep playing.
        if (!promptUi)
        {
            _pendingGapReport = report;
            _skipGapPrompts.Add(skipKey);
            _log("缺依赖，先用可用模型转写 · " + report.SummaryLine());
            if (PlaybackPresets.IsEnglishSource(ActivePreset)
                && report.Gaps.Any(g => g.Kind is PresetGapKind.GgufModel or PresetGapKind.LlamaRuntime))
            {
                PublishStatus(Loc.Format("Main.Status.PresetDowngrade.EnMt", ActivePreset.Name));
                ShowOsd(Loc.Format("Main.Osd.PresetDowngrade.EnMt", ActivePreset.Name), 3500);
            }
            else
            {
                PublishStatus(Loc.Format("Main.Status.PresetDowngrade", ActivePreset.Name, report.SummaryLine()));
                ShowOsd(Loc.Format("Main.Osd.PresetDowngrade", ActivePreset.Name), 3500);
            }
            PacksChanged?.Invoke();
            return true;
        }

        _pendingGapReport = null;

        if (OfferPresetSetupAsync is null)
        {
            _log("预设缺依赖（无 UI 回调）· " + report.SummaryLine());
            return true;
        }

        _status("等待确认预设组件…");
        var choice = await OfferPresetSetupAsync(report).ConfigureAwait(false);
        switch (choice)
        {
            case PresetSetupChoice.Cancel:
                return false;
            case PresetSetupChoice.UseFallback:
                _skipGapPrompts.Add(skipKey);
                _status("先用极速转写 · " + AsrModelCatalog.DisplayName(report.FallbackAsr));
                return true;
            case PresetSetupChoice.ManualInstall:
            {
                packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
                PublishPacks(packs);
                report = BuildGapReport(ActivePreset, packs);
                if (report.HasGaps)
                {
                    _skipGapPrompts.Add(skipKey);
                    _status("未检测到全部依赖 · 先用极速转写");
                }
                else
                {
                    _status("依赖已检测到，按预设继续");
                }

                return true;
            }
            case PresetSetupChoice.AutoInstall:
            {
                var installer = new PresetDependencyInstaller(_settings, _status, _log);
                try
                {
                    await installer.InstallAsync(report, _engine, ActivePreset, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log("自动安装失败：" + ex.Message);
                    _status("自动安装未完成：" + ex.Message);
                    _skipGapPrompts.Add(skipKey);
                    return true;
                }

                packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
                PublishPacks(packs);
                report = BuildGapReport(ActivePreset, packs);
                if (report.HasGaps)
                {
                    _log("安装后仍缺：" + report.SummaryLine());
                    _status("部分依赖仍不可用 · 将尽量按可用模型转写");
                    _skipGapPrompts.Add(skipKey);
                }
                else
                {
                    _pendingGapReport = null;
                    ShowOsd(Loc.Format("Settings.Presets.InstallDone", ActivePreset.Name), 1800);
                }

                return true;
            }
            default:
                return true;
        }
    }

    private PresetGapReport BuildGapReport(PlaybackPreset preset, RuntimePacks packs)
    {
        var wantsMt = preset.Mt != MtPromptKind.Off && _settings.TranslateEnabled;
        var llamaOk = ManagedLlmInstaller.HasLlamaRuntime(_settings);
        var ggufOk = ManagedLlmInstaller.HasPreferredGguf(preset.Mt, _settings);
        // If llama is already healthy on configured URL, treat MT runtime as ready.
        var translateProbeReady = _translateReady == true
            || (wantsMt && llamaOk && ggufOk);
        return PresetReadiness.Analyze(
            preset,
            _engine.ModelsRoot,
            packs,
            _engine.EngineKind,
            wantsMt,
            translateReady: translateProbeReady,
            llamaRuntimePresent: llamaOk,
            preferredGgufPresent: ggufOk);
    }

    private static string GapSkipKey(PlaybackPreset preset)
        => preset.Id + "|" + string.Join(",", preset.AsrChain) + "|" + preset.Mt;

    private IReadOnlyList<string> PreferredGgufs => ActivePreset.Mt switch
    {
        MtPromptKind.JaZh => LlamaServerProcess.SakuraJaModels,
        MtPromptKind.Off => LlamaServerProcess.SakuraJaModels,
        _ => LlamaServerProcess.QwenInstructModels,
    };

    private async Task EnsureTranslateAsync(CancellationToken ct)
    {
        await _translateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var url = string.IsNullOrWhiteSpace(_settings.TranslateUrl)
                ? LlamaServerProcess.DefaultBaseUrl
                : _settings.TranslateUrl.Trim().TrimEnd('/');
            _settings.TranslateUrl = url;

            try
            {
                if (_llama is not null && await LlamaServerProcess.IsHealthyAsync(url, ct).ConfigureAwait(false))
                {
                    _settings.TranslateUrl = _llama.BaseUrl;
                    _translateReady = true;
                    return;
                }

                // Do not unload ASR GPU while a job is still running (MT reconnect mid-preview).
                var asrHoldsGpu = _engine.HasActiveJob;
                if (!asrHoldsGpu)
                    await _engine.ReleaseGpuAsync(ct).ConfigureAwait(false);

                _llama ??= new LlamaServerProcess();
                _status(asrHoldsGpu ? "正在启动翻译模型（识别占用 GPU，可能较慢）…" : "正在启动本机翻译模型…");
                await _llama.EnsureRunningAsync(
                        _settings,
                        _log,
                        ct,
                        PreferredGgufs,
                        preferCpu: asrHoldsGpu)
                    .ConfigureAwait(false);
                _settings.TranslateUrl = _llama.BaseUrl;
                _translateReady = true;
                _log($"翻译模型就绪 · {_llama.BaseUrl}" + (_llama.ModelPath is null ? "" : " · " + Path.GetFileName(_llama.ModelPath)));
                MaybeOfferTranslateTargetEnTip();
            }
            catch (Exception ex)
            {
                _translateReady = false;
                PublishStatus(Loc.Get("Main.Status.MtStartFailed"), "翻译模型启动失败：" + ex.Message);
            }
        }
        finally
        {
            _translateGate.Release();
        }
    }

    private void PublishStatus(string user, string? detail = null)
    {
        _status(user);
        if (!string.IsNullOrWhiteSpace(detail))
            _log(detail);
    }

    private void MarkPreviewFailed(string? message)
    {
        _previewRetryAvailable = true;
        EndBootstrap();
        var user = string.IsNullOrWhiteSpace(message)
            ? Loc.Get("Main.Status.PreviewFailed")
            : Loc.Format("Main.Status.PreviewFailedDetail", message);
        PublishStatus(user, message is null ? null : "转写启动失败：" + message);
        StateChanged?.Invoke();
    }

    private void MaybeOfferLagMentalModelTip()
    {
        if (!UserTips.ShouldShow(_settings, UserTips.LagMentalModel)) return;
        if (!ShowPreviewChrome) return;
        UserTips.Dismiss(_settings, UserTips.LagMentalModel);
        var key = TranslateTargetUi.SubProgressLegendKey(_settings, IsEnglishSource);
        PublishStatus(Loc.Get(key == "Main.SubProgressLegend.En"
            ? "Main.Status.LagMentalModel.En"
            : key == "Main.SubProgressLegend.ToEn"
                ? "Main.Status.LagMentalModel.ToEn"
                : "Main.Status.LagMentalModel"));
    }

    private async Task MaybeOfferEnglishSourceChoiceAsync(int gen, CancellationToken ct)
    {
        if (!IsEnglishSource) return;
        if (!TranslateTargets.IsChinese(_settings)) return;
        if (!string.Equals(Loc.CurrentTag, "en", StringComparison.OrdinalIgnoreCase)) return;
        if (!UserTips.ShouldShow(_settings, UserTips.OfferEnglishSource)) return;
        if (OfferEnglishSourceChoiceAsync is null) return;

        UserTips.Dismiss(_settings, UserTips.OfferEnglishSource);
        var choice = await OfferEnglishSourceChoiceAsync().ConfigureAwait(false);
        if (!IsCurrentMedia(gen, MediaPath!)) return;
        if (choice is null) return;

        if (choice == false)
        {
            _mtDisabledForSession = true;
            SetDisplayMode(SubtitleDisplayMode.Source);
            ShowOsd(Loc.Get("Main.EnSource.SourceOnly"), 2800);
            return;
        }

        var report = PresetReadiness.AnalyzeDisk(ActivePreset, _settings);
        if (!report.HasGaps)
        {
            ShowOsd(Loc.Get("Main.EnSource.InstallLater"), 2200);
            return;
        }

        if (OfferPresetSetupAsync is null) return;
        _ = ct;
        var setup = await OfferPresetSetupAsync(report).ConfigureAwait(false);
        if (setup is PresetSetupChoice.Cancel) return;
        var after = PresetReadiness.AnalyzeDisk(ActivePreset, _settings);
        _pendingGapReport = after.HasGaps ? after : null;
        PacksChanged?.Invoke();
    }

    private void MaybeOfferQualityHandoffTip()
    {
        if (!UserTips.ShouldShow(_settings, UserTips.QualityHandoff)) return;
        if (!ShowPreviewChrome) return;
        UserTips.Dismiss(_settings, UserTips.QualityHandoff);
        PublishStatus(Loc.Get("Main.Status.QualityHandoff"));
    }

    private string BuildStatusLine(string? engineLabel = null)
    {
        _ = engineLabel;
        var mode = SubtitleDisplayModeUtil.Label(_displayMode);
        if (_displayMode == SubtitleDisplayMode.Off)
            return UsingExistingSub
                ? Loc.Get("Main.Status.Build.OffExisting")
                : Loc.Get("Main.Status.Build.OffPreview");
        if (UsingExistingSub || IsLocalSubtitleSource)
            return UsingExistingSub
                ? Loc.Get("Main.Status.ExternalSub") + Loc.Get("Main.Status.ExternalSubHint")
                : Loc.Get("Main.SubSource.LocalOnlyNone");
        if (ActivePreset.Mt == MtPromptKind.Off)
            return Loc.Format("Main.Status.Build.PreviewJa", mode);
        if (WantsPreviewMt && _translateReady == false)
            return Loc.Format("Main.Status.Build.MtNotReady", mode);
        if (WantsPreviewMt && TranslatedCount == 0)
            return Loc.Get("Main.Status.Build.Generating");
        if (WantsPreviewMt && _translateReady == true)
            return Loc.Format("Main.Status.Build.PreviewMt", mode, TranslatedCount, CueCount);
        if (WantsPreviewMt)
            return Loc.Get("Main.Status.Build.StartingMt");
        return Loc.Format("Main.Status.Build.SourceOnly", mode);
    }

    public string ModeTip(SubtitleDisplayMode mode)
    {
        var pending = ShowingZhPending && mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual;
        if (!pending)
        {
            return mode switch
            {
                SubtitleDisplayMode.Zh => TranslateTargetUi.ModeTranslationTip(_settings, IsEnglishSource),
                SubtitleDisplayMode.Dual => TranslateTargetUi.ModeDualTip(_settings, IsEnglishSource),
                _ => TranslateTargetUi.ModeSourceTip(_settings, IsEnglishSource),
            };
        }

        if (_translateReady == false)
        {
            return mode == SubtitleDisplayMode.Dual
                ? Loc.Get("Main.Mode.Dual.Pending.MtMissing")
                : Loc.Get("Main.Mode.Zh.Pending.MtMissing");
        }

        if (_translateReady != true)
        {
            return mode == SubtitleDisplayMode.Dual
                ? Loc.Get("Main.Mode.Dual.Pending.MtStarting")
                : Loc.Get("Main.Mode.Zh.Pending.MtStarting");
        }

        return mode == SubtitleDisplayMode.Dual
            ? Loc.Get("Main.Mode.Dual.Pending.Queue")
            : Loc.Get("Main.Mode.Zh.Pending.Queue");
    }

    private void MaybeOfferExternalSubHint()
    {
        if (!UserTips.ShouldShow(_settings, UserTips.ExternalSubHint)) return;
        UserTips.Dismiss(_settings, UserTips.ExternalSubHint);
        ShowOsd(Loc.Get("Main.Osd.ExternalSubTip"), 2800);
    }

    private void ApplySub(string path, bool reloadIfSame = true)
    {
        ActiveSubPath = path;
        _mpv.SetSubtitle(path, reloadIfSame);
        ApplyDisplayVisibility();
    }

    private static string AsrDisplayName(string model)
        => AsrModelCatalog.DisplayName(model);
}
