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
    private readonly AsrPipeline _engine;
    private LlamaServerProcess? _llama;
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private bool? _translateReady;
    private bool _translateRunningOnCpu;
    private SubtitleDisplayMode _displayMode;
    private SubtitleDisplayMode _lastContentMode = SubtitleDisplayMode.Zh;
    private readonly HashSet<string> _skipGapPrompts = new(StringComparer.OrdinalIgnoreCase);
    private bool _depsCheckedForStart;
    private bool _waitingForFirstZh;
    private bool _waitingForModeReady;
    private bool _retranslatingActive;
    private double _nextPreviewStartFrom;
    private IReadOnlyList<Cue>? _nextPreviewSeedCues;
    private const double AsrChunkStepSec = 88; // AsrPipeline ChunkSec - ChunkOverlapSec
    private bool _resumeAfterModeWait;
    private CoverageWaitReason _coverageWaitReason = CoverageWaitReason.None;
    private CancellationTokenSource? _waitZhCts;
    private PlaylistPrefetch? _prefetch;
    private int _disposed;
    private bool _previewRetryAvailable;
    private bool _manualPreviewNeeded;
    private bool _mtDisabledForSession;
    private PresetGapReport? _pendingGapReport;
    private int _mediaGeneration;
    /// <summary>Sensed source lang for this media while settings stay auto; survives <see cref="ResolveScene"/>.</summary>
    private string? _sessionSensedLanguage;
    private int _onlineSubtitleBusy;
    private CancellationTokenSource? _previewRunCts;
    private CancellationTokenSource _lifetimeCts = new();
    private ExternalSubOrigin _externalOrigin = ExternalSubOrigin.None;
    private readonly FinishedSubtitleMonitor _finishedSub = new();
    /// <summary>Last-open position offered to the user; playback stays at 0 until accepted.</summary>
    private double _pendingResumeAt;
    private bool _bootstrapActive;
    private bool _announcedFirstSourceCue;
    private bool _announcedFirstZhCue;
    private BootstrapPhase _bootstrapPhase = BootstrapPhase.None;
    private string _bootstrapPhaseTitle = "";
    private string _bootstrapPhaseDetail = "";
    private IReadOnlyList<StreamQualityOption> _streamQualities = [];
    private string? _streamQualityId;
    private IReadOnlyDictionary<string, string>? _streamHeaders;
    private string? _streamDisplayName;
    private string? _mpvPlayUrl;
    private IReadOnlyDictionary<string, string>? _mpvPlayHeaders;
    private string? _mpvPlayOsd;
    private string? _liveMasterUrl;
    private string? _streamBaseStatus;
    private bool _streamBuffering;
    private int _streamBufferingPercent;
    private readonly SemaphoreSlim _mpvLoadGate = new(1, 1);
    private int _liveClockPollBusy;
    private readonly SubtitleCoverageTracker _coverage = new();
    private bool _fellBackToSourceThisMedia;

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

    private enum CoverageWaitReason
    {
        None,
        DisplayMode,
        LanguageSwitch,
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
    /// <summary>网络流 / 桌面采集：只播画面，不做字幕任务。</summary>
    public bool IsStreamPlayback
        => !string.IsNullOrWhiteSpace(MediaPath)
           && MediaSourceHelper.IsNonLocalMedia(MediaPath);
    /// <summary>走 ASR/翻译原文提取路径（非成片外挂、非仅本地字幕来源、非流媒体）。</summary>
    public bool ShowPreviewChrome
        => !string.IsNullOrWhiteSpace(MediaPath)
           && !UsingExistingSub
           && !IsLocalSubtitleSource
           && !IsStreamPlayback;
    public double Duration { get; private set; }
    public double Position { get; private set; }
    public bool Paused { get; private set; } = true;
    public string AsrModel { get; private set; } = "";
    public string EngineDetail { get; private set; } = "引擎未连接";
    public SceneProfile ActiveScene { get; private set; } = SceneProfiles.Default;
    public SceneProfile? MatchedScene { get; private set; }
    /// <summary>Concrete lang from short-window audio LID this session; null if skipped or not adopted.</summary>
    public string? SensedSourceLanguage { get; private set; }
    public MtRoute ActiveMtRoute =>
        MtRoute.Resolve(MtSourceLanguage(), _settings.TranslateTarget, ActiveScene.ContentProfile);

    /// <summary>Concrete source lang for MT — avoid <c>auto</c> when cues / sense / filename already imply one.</summary>
    private string MtSourceLanguage()
    {
        if (!SourceLanguages.IsAuto(ActiveScene.Language))
            return ActiveScene.Language;
        if (!string.IsNullOrWhiteSpace(SensedSourceLanguage)
            && !SourceLanguages.IsAuto(SensedSourceLanguage))
            return SensedSourceLanguage;
        if (!string.IsNullOrWhiteSpace(_sessionSensedLanguage)
            && !SourceLanguages.IsAuto(_sessionSensedLanguage))
            return _sessionSensedLanguage;
        if (MatchedScene is not null && !SourceLanguages.IsAuto(MatchedScene.Language))
            return MatchedScene.Language;
        return _subs.InferDominantSourceLanguage() ?? ActiveScene.Language;
    }
    public bool IsEnglishSource => SceneProfiles.IsEnglishSource(ActiveScene.Language);
    public int CueCount => _subs.CueCount;
    public int TranslatedCount => _subs.TranslatedCount;
    public double SubFrontier => _subs.SubFrontier;
    public double ZhFrontier => _subs.ZhFrontier;
    public bool WaitingForZh => _waitingForFirstZh || _waitingForModeReady;
    /// <summary>Mode/language coverage wait — show jump / switch-source actions on the wait overlay.</summary>
    public bool WaitShowsCoverageActions => _waitingForModeReady;
    public bool IsRetranslating => _retranslatingActive;
    public string WaitZhOverlayTitle { get; private set; } = "";
    public string WaitZhOverlayDetail { get; private set; } = "";
    /// <summary>Bootstrap dimmer on OpeningPopup — only while paused; playing uses status bar instead.</summary>
    public bool ShowOpeningBootstrap => _bootstrapActive && !_waitingForFirstZh && Paused;
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
    public bool PresetInstallAvailable => CurrentGapReport()?.HasGaps == true;
    public PresetGapReport? PendingGapReport => CurrentGapReport();

    /// <summary>
    /// Drop stale open-path gaps once disk matches settings (settings「已就绪」but main still showed install).
    /// Uses on-disk packs only — live engine GPU probe can stay false after weights are already present.
    /// </summary>
    private PresetGapReport? CurrentGapReport()
    {
        if (_pendingGapReport?.HasGaps != true)
            return null;

        var wantsMt = MtRoute.WantsTranslation(ActiveMtRoute) && _settings.TranslateEnabled;
        try
        {
            var modelsRoot = string.IsNullOrWhiteSpace(_engine.ModelsRoot)
                ? AsrModelStore.ResolveModelsRoot(_settings)
                : _engine.ModelsRoot;
            var packs = RuntimePacks.FromDisk(modelsRoot);
            var llamaOk = ManagedLlmInstaller.HasLlamaRuntime(_settings);
            var ggufOk = ManagedLlmInstaller.HasPreferredGguf(_settings);
            var disk = PresetReadiness.Analyze(
                _settings.AsrModel,
                modelsRoot,
                packs,
                wantsMt,
                translateReady: !wantsMt || (llamaOk && ggufOk),
                llamaRuntimePresent: llamaOk,
                preferredGgufPresent: ggufOk,
                translateModelId: _settings.TranslateModelId,
                mtModelsDir: AppPaths.ResolveAdvancedLlmModelsDir(_settings));
            if (!disk.HasGaps)
            {
                _pendingGapReport = null;
                return null;
            }

            _pendingGapReport = disk;
            return disk;
        }
        catch
        {
            return _pendingGapReport;
        }
    }
    /// <summary>
    /// 译文/双语模式下翻译已关：画面实际是原文，但模式芯片仍像「译文」。
    /// </summary>
    public bool ShowingSourceDueToTranslateOff
        => !string.IsNullOrWhiteSpace(MediaPath)
           && !UsingExistingSub
           && !IsLocalSubtitleSource
           && MtRoute.WantsTranslation(ActiveMtRoute)
           && !_settings.TranslateEnabled
           && (_displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual);

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
    /// <summary>When set, mode-switch OSD uses compact fullscreen templates.</summary>
    public Func<bool>? IsFullscreenProvider { get; set; }

    /// <summary>UI hook: ask user how to resolve missing preset deps. Runs on UI thread via caller.</summary>
    public Func<PresetGapReport, Task<PresetSetupChoice>>? OfferPresetSetupAsync { get; set; }
    public Func<SubtitleCatPickRequest, Task<SubtitleCatResult?>>? OfferSubtitleCatPickAsync { get; set; }
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
        _engine = new AsrPipeline(settings, PublishStatusWithCoverage, PlayerLog.WriteEngine);
        _subs = new PreviewSubtitleSync(
            settings,
            status,
            log,
            OnSubtitleStateChanged,
            () => ActiveScene.ContentProfile,
            () => ActiveMtRoute,
            () => WantsPreviewMt,
            () => _translateReady,
            v => _translateReady = v,
            ct => EnsureTranslateAsync(ct),
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
            _coverage.SetMediaDuration(d);
            StateChanged?.Invoke();
        };
        _mpv.PauseChanged += p =>
        {
            Paused = p;
            if (!p && _waitingForFirstZh)
                CancelWaitForFirstZh(userOverride: true);
            else if (!p && _waitingForModeReady)
                CancelWaitForModeReady(userOverride: true);
            else if (!p && ShowPreviewChrome)
                PublishStatus(BuildStatusLine());
            StateChanged?.Invoke();
        };
        _mpv.VolumeChanged += _ => StateChanged?.Invoke();
        _mpv.MuteChanged += _ => StateChanged?.Invoke();
        _mpv.SpeedChanged += _ => StateChanged?.Invoke();
        _mpv.EofReached += ended =>
        {
            if (!ended) return;
            // Live HLS may spuriously signal EOF; do not end the session.
            if (MediaSourceHelper.IsRemoteUrl(MediaPath) && !MediaSourceHelper.IsScreenCapture(MediaPath))
                return;
            MediaEnded?.Invoke();
        };
        _mpv.VideoSizeChanged += (w, h) =>
        {
            VideoWidth = w;
            VideoHeight = h;
            VideoSizeChanged?.Invoke(w, h);
        };
        _mpv.BufferingChanged += OnMpvBufferingChanged;
        _prefetch = new PlaylistPrefetch(
            settings,
            _engine,
            status,
            log,
            path => SceneProfiles.Resolve(settings.SourceLanguage, path, out _),
            ct => EnsureTranslateAsync(ct),
            () => _settings.TranslateEnabled,
            () => MediaPath);
        _prefetch.Changed += state => PrefetchChanged?.Invoke(state);
        _finishedSub.Changed += () => StateChanged?.Invoke();
    }

    /// <summary>True when a Transub-finished sidecar appeared after handoff and user has not dismissed.</summary>
    public bool HasFinishedSubtitleOffer => _finishedSub.HasPending;

    /// <summary>True when a remembered position is waiting for Jump / auto-dismiss.</summary>
    public bool HasResumeOffer => _pendingResumeAt > 1;

    public double PendingResumeAt => _pendingResumeAt;

    public void AcceptResumeOffer()
    {
        if (_pendingResumeAt <= 1) return;
        var at = _pendingResumeAt;
        _pendingResumeAt = 0;
        Seek(at);
        _log($"续播 {MediaTimeFormat.Format(at)}");
        ShowOsd(Loc.Format("Main.Osd.Resumed", MediaTimeFormat.Format(at)), 2200);
        StateChanged?.Invoke();
    }

    public void DismissResumeOffer()
    {
        if (_pendingResumeAt <= 0) return;
        _pendingResumeAt = 0;
        StateChanged?.Invoke();
    }

    public string? PendingFinishedSubtitlePath => _finishedSub.PendingPath;

    /// <summary>After opening Transub, watch the media folder for a rewritten finished sidecar.</summary>
    public void ArmFinishedSubtitleWatch()
    {
        if (string.IsNullOrWhiteSpace(MediaPath) || MediaSourceHelper.IsNonLocalMedia(MediaPath))
            return;
        _finishedSub.Arm(MediaPath);
        StateChanged?.Invoke();
    }

    public void ProbeFinishedSubtitleOffer() => _finishedSub.Probe();

    public void DismissFinishedSubtitleOffer() => _finishedSub.DismissOffer();

    public async Task AcceptFinishedSubtitleAsync(CancellationToken ct)
    {
        var path = _finishedSub.PendingPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _finishedSub.DismissOffer();
            return;
        }

        await LoadExternalSubtitleFileAsync(path, ExternalSubOrigin.Local, ct).ConfigureAwait(false);
        _finishedSub.MarkAccepted(path);
        PublishStatus(Loc.Get("Main.Status.FinishedSubLoaded"), $"成片字幕 {path}");
        ShowOsd(Loc.Get("Main.Osd.FinishedSubLoaded"), 2200);
        StateChanged?.Invoke();
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

    public void SkipWaitForFirstZh()
    {
        if (_waitingForModeReady)
        {
            FinishWaitForModeReady(play: true);
            return;
        }

        if (_waitingForFirstZh)
            FinishWaitForFirstZh(play: true, Loc.Get("Main.Osd.WaitZhSkipped"));
    }

    /// <summary>During coverage wait: show source and resume without waiting for translation.</summary>
    public void WaitSwitchToSourceAndResume()
    {
        if (!_waitingForModeReady) return;
        SetDisplayMode(SubtitleDisplayMode.Source, announce: false);
        FinishWaitForModeReady(play: true);
        PublishStatus(Loc.Get("Main.Status.SwitchToSource"));
        ShowOsd(Loc.Get("Main.Osd.FallbackSource"), 1800);
    }

    /// <summary>During coverage wait: seek to generated frontier and resume.</summary>
    public void WaitJumpToReadyAndResume()
    {
        if (!_waitingForModeReady) return;
        var ready = Math.Max(0, EffectiveReadyFrontier() - 0.5);
        Seek(ready);
        FinishWaitForModeReady(play: true);
        PublishStatus(Loc.Format("Main.Status.JumpReady", MediaTimeFormat.Format(ready)));
    }

    public async Task EnsurePlayerAsync(
        string? initialMedia = null,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        bool autoPlay = true)
    {
        if (string.IsNullOrWhiteSpace(initialMedia))
        {
            if (_mpv.IpcFailed)
                _mpv.RecoverIfUnresponsive();
            if (_mpv.IsRunning)
                return;
        }
        else
        {
            // Live HLS: always respawn with URL + Referer on argv (Covers Download parity).
            _mpv.Stop();
        }

        var mpv = MpvLocator.Find()
            ?? throw new MpvMissingException();
        _host.EnsureHandle();
        await _host.Dispatcher.InvokeAsync(() => { });
        await _mpv.StartAsync(mpv, _host.Hwnd, _settings, CancellationToken.None, initialMedia, httpHeaders, autoPlay)
            .ConfigureAwait(false);
        await _host.Dispatcher.InvokeAsync(() => _host.HookEmbeddedChildren());
        // Live spawn already has volume/sub fonts on the command line — do not IPC-touch demuxer mid-open.
        if (string.IsNullOrWhiteSpace(initialMedia))
        {
            _mpv.SetVolume(_settings.Volume);
            _mpv.SetSpeed(_settings.Speed <= 0 ? 1.0 : _settings.Speed);
            _mpv.ApplySubtitleSettings(_settings);
        }

        ApplyDisplayVisibility();
        _log($"mpv · {mpv}");
    }

    private bool LoadIntoMpv(
        string playPath,
        bool autoPlay,
        IReadOnlyDictionary<string, string>? headers = null,
        string? osdText = null)
    {
        if (_mpv.LoadFile(playPath, autoPlay, headers, osdText))
            return true;

        _log("mpv 加载失败，重启后重试");
        _mpv.Stop();
        return false;
    }

    private async Task<bool> EnsureLoadIntoMpvAsync(
        string playPath,
        bool autoPlay,
        IReadOnlyDictionary<string, string>? headers = null,
        string? osdText = null)
    {
        var needsSpawnWithUrl = MediaSourceHelper.IsRemoteUrl(playPath)
                                && !MediaSourceHelper.IsScreenCapture(playPath)
                                && (playPath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                                    || playPath.Contains("sacdnssedge", StringComparison.OrdinalIgnoreCase)
                                    || playPath.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase));

        if (needsSpawnWithUrl)
        {
            await EnsurePlayerAsync(playPath, headers, autoPlay).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(osdText))
                _mpv.ShowOsd(osdText);
            return _mpv.IsRunning;
        }

        // Live HLS spawn uses light IPC (no time-pos/duration observe, hr-seek=no).
        // Reusing that process for local files leaves the seek bar unable to control playback.
        if (_mpv.LiveLightIpc)
        {
            _log("离开直播模式，重启 mpv");
            _mpv.Stop();
        }

        if (_mpv.IpcFailed)
            _mpv.RecoverIfUnresponsive();
        if (!_mpv.IsRunning)
            await EnsurePlayerAsync().ConfigureAwait(false);

        if (LoadIntoMpv(playPath, autoPlay, headers, osdText))
            return true;

        _mpv.Stop();
        await EnsurePlayerAsync().ConfigureAwait(false);
        return LoadIntoMpv(playPath, autoPlay, headers, osdText);
    }

    public async Task OpenMediaAsync(string path, CancellationToken ct)
    {
        var gen = Interlocked.Increment(ref _mediaGeneration);
        await _mpvLoadGate.WaitAsync(ct).ConfigureAwait(false);
        // Block prefetch before cancelling the old job so onFinished cannot start a pump mid-open.
        var liveBusyEpoch = _prefetch?.EnterLiveBusy() ?? 0;
        var handOffToPreview = false;
        try
        {
            CancelPreviewRun();
            ResetSubtitleWaits();
            SavePlaybackPosition();
            await StopStreamRecordSilentlyAsync(ct).ConfigureAwait(false);

            // Fast switch: update player and clear old subtitles before waiting on engine cancel.
            MediaPath = path;
            _finishedSub.Disarm();
            UsingExistingSub = false;
            ActiveSubPath = null;
            _externalOrigin = ExternalSubOrigin.None;
            VideoWidth = 0;
            VideoHeight = 0;
            Position = 0;
            Duration = 0;
            _translateReady = null;
            _translateRunningOnCpu = false;
            _previewRetryAvailable = false;
            _manualPreviewNeeded = false;
            _mtDisabledForSession = false;
            _pendingGapReport = null;
            _announcedFirstSourceCue = false;
            _announcedFirstZhCue = false;
            _fellBackToSourceThisMedia = false;
            _coverage.Reset();
            _subs.Reset();
            _mpv.ClearSubtitle();
            ClearStreamQualities();
            _streamBaseStatus = null;
            _streamBuffering = false;
            _streamBufferingPercent = 0;
            _sessionSensedLanguage = null;
            _pendingResumeAt = 0;
            ResolveScene();

            // Restore how this file was last watched (mode / delay / source lang) before ASR starts.
            MediaSessionPrefs sessionPrefs;
            if (MediaSourceHelper.IsNonLocalMedia(path))
            {
                sessionPrefs = new MediaSessionPrefs();
            }
            else
            {
                sessionPrefs = PlaybackPositionStore.LoadPrefs(path);
                ApplyRememberedViewPrefs(sessionPrefs);
                HydrateSessionSense(sessionPrefs, path);
            }

            ResolveScene(); // re-apply filename + session sense after prefs

            var isRemoteOpen = MediaSourceHelper.IsRemoteUrl(path) && !MediaSourceHelper.IsScreenCapture(path);
            if (!isRemoteOpen)
                await EnsurePlayerAsync().ConfigureAwait(false);
            ThrowIfStaleMedia(gen, ct);

            ResolvedNetworkStream? streamMeta = null;
            var playPath = path;
            if (isRemoteOpen)
            {
                PublishStatus(Loc.Get(StreamMediaResolver.MayNeedResolve(path)
                    ? "Main.Status.ResolvingStream"
                    : "Main.Status.Opening"));
                var prepared = await StreamMediaResolver.PrepareAsync(path, ct).ConfigureAwait(false);
                ThrowIfStaleMedia(gen, ct);
                playPath = prepared.PlayUrl;
                streamMeta = prepared.Meta;
            }

            if (MediaSourceHelper.IsNonLocalMedia(path))
            {
                var streamAutoPlay = _settings.AutoPlayOnOpen;
                var osdName = streamMeta?.DisplayName ?? MediaSourceHelper.DisplayName(path);
                if (streamMeta is not null)
                {
                    _streamQualities = streamMeta.Qualities.Count > 0
                        ? streamMeta.Qualities
                        : [new StreamQualityOption("default", Loc.Get("Main.StreamQuality.Default"), playPath, "default", 0)];
                    _streamQualityId = streamMeta.SelectedQualityId;
                    if (string.IsNullOrWhiteSpace(_streamQualityId)
                        || _streamQualities.All(q => q.Id != _streamQualityId))
                        _streamQualityId = _streamQualities[0].Id;
                    _streamHeaders = streamMeta.Headers;
                    _streamDisplayName = streamMeta.DisplayName;
                    _liveMasterUrl = streamMeta.MasterPlaylistUrl;
                    var chosen = _streamQualities.FirstOrDefault(q => q.Id == _streamQualityId) ?? _streamQualities[0];
                    playPath = chosen.Url;
                    _streamHeaders = HeadersForStreamUrl(playPath, streamMeta.Headers);
                }

                if (StripchatLiveCdn.IsSacdnssedge(playPath))
                {
                    try
                    {
                        playPath = await StripchatHlsPlaylist.ResolveSacdnssedgeForMpvAsync(playPath, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log("sacdnssedge 解析：" + ex.Message);
                    }

                    _streamHeaders = HeadersForStreamUrl(playPath, _streamHeaders ?? streamMeta?.Headers);
                }

                _mpvPlayUrl = playPath;
                _mpvPlayHeaders = _streamHeaders;
                _mpvPlayOsd = osdName;
                _log("直播 URL → " + playPath);
                if (!await EnsureLoadIntoMpvAsync(playPath, streamAutoPlay, _streamHeaders, osdName).ConfigureAwait(false))
                    throw new InvalidOperationException(Loc.Get("Main.Status.MpvLoadFailed"));
                await _host.Dispatcher.InvokeAsync(() => _host.HookEmbeddedChildren());
                if (streamAutoPlay)
                    _mpv.SetPause(false);
                Paused = !streamAutoPlay;
                StateChanged?.Invoke();
                EndBootstrap();
                if (streamMeta is not null)
                    PublishStreamStatus(Loc.Format("Main.Status.StripchatPlaying", streamMeta.DisplayName));
                else
                    PublishStreamStatus(Loc.Get(MediaSourceHelper.IsScreenCapture(path)
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
            _mpvPlayUrl = path;
            _mpvPlayHeaders = null;
            _mpvPlayOsd = null;
            if (!await EnsureLoadIntoMpvAsync(path, autoPlay).ConfigureAwait(false))
                throw new InvalidOperationException(Loc.Get("Main.Status.MpvLoadFailed"));
            Paused = !autoPlay;
            if (sessionPrefs.HasViewPrefs)
            {
                try { _mpv.SetSubDelay(_settings.SubDelaySec); } catch { /* ignore */ }
                ApplyDisplayVisibility();
            }
            if (waitZh)
                BeginWaitForFirstZh();

            // Default: play from the start. Offer last position as a timed Jump prompt.
            if (_settings.RememberPlaybackPosition)
            {
                var resumeAt = sessionPrefs.Position > 1
                    ? sessionPrefs.Position
                    : PlaybackPositionStore.Load(path);
                if (resumeAt > 1)
                {
                    _pendingResumeAt = resumeAt;
                    _log($"发现上次进度 {MediaTimeFormat.Format(resumeAt)} · 默认从头");
                }
            }

            StateChanged?.Invoke();

            // Stale ASR cancel runs in finally (before ReleaseLiveBusy) so prefetch cannot overlap.

            if (sessionPrefs.HasViewPrefs)
                MaybeAnnounceSessionRestored(sessionPrefs);

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
            if (existing is null
                && pref == SubtitleSourceKind.Live
                && _settings.FetchSubtitleFromSubtitleCat)
            {
                onlineFetched = await TryFetchSubtitleCatAsync(path, gen, ct, announceAsrFallback: true).ConfigureAwait(false);
            }
            if (onlineFetched is not null)
                existing = onlineFetched;

            ThrowIfStaleMedia(gen, ct);

            if (existing is not null)
            {
                ResetSubtitleWaits();
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
            {
                // Finish engine cancel under live-busy so prefetch cannot start a second job mid-cancel.
                await CancelStaleEngineJobAsync().ConfigureAwait(false);
                _prefetch?.ReleaseLiveBusy(liveBusyEpoch);
            }
            _mpvLoadGate.Release();
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
            ResetSubtitleWaits();
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

        var previous = SourcePreference;
        var wasPlaying = !Paused;
        SaveSubtitleSource(kind);

        switch (kind)
        {
            case SubtitleSourceKind.Off:
                SetDisplayMode(SubtitleDisplayMode.Off, announce: true);
                return;
            case SubtitleSourceKind.Online:
                await FindOnlineSubtitlesAsync(ct).ConfigureAwait(false);
                return;
            case SubtitleSourceKind.Local:
                await UseLocalSubtitleAsync(ct).ConfigureAwait(false);
                return;
            case SubtitleSourceKind.Live:
                await UseLiveSubtitleAsync(
                        ct,
                        pauseForWait: previous != SubtitleSourceKind.Live && wasPlaying)
                    .ConfigureAwait(false);
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
        ResetSubtitleWaits();
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

        await ApplyExternalSubtitleCoreAsync(local, ExternalSubOrigin.Local, gen, path).ConfigureAwait(false);
        PublishStatus(Loc.Get("Main.Status.ExternalSub") + Loc.Get("Main.Status.ExternalSubHint"), $"使用现有字幕 {local}");
        ShowOsd(Loc.Get("Main.Osd.ExternalSub"), 2000);
        MaybeOfferExternalSubHint();
        StateChanged?.Invoke();
    }

    /// <summary>Load a specific sidecar (e.g. Transub-finished) and stop live ASR/MT for this media.</summary>
    public async Task LoadExternalSubtitleFileAsync(string subPath, ExternalSubOrigin origin, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");
        if (string.IsNullOrWhiteSpace(subPath) || !File.Exists(subPath))
            throw new FileNotFoundException("字幕文件不存在。", subPath);

        _ = ct;
        var gen = Volatile.Read(ref _mediaGeneration);
        var path = MediaPath!;
        CancelPreviewRun();
        ResetSubtitleWaits();
        _subs.Reset();
        await StopPreviewEngineIfNeededAsync().ConfigureAwait(false);
        if (!IsCurrentMedia(gen, path))
            return;

        await ApplyExternalSubtitleCoreAsync(subPath, origin, gen, path).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    private Task ApplyExternalSubtitleCoreAsync(
        string subPath,
        ExternalSubOrigin origin,
        int gen,
        string mediaPath)
    {
        if (!IsCurrentMedia(gen, mediaPath))
            return Task.CompletedTask;

        UsingExistingSub = true;
        _subs.SetUsingExistingSub(true);
        _externalOrigin = origin;
        ApplySub(subPath);
        EndBootstrap();
        _prefetch?.SetLiveBusy(false);
        if (_displayMode == SubtitleDisplayMode.Off)
            SetDisplayMode(_lastContentMode);
        return Task.CompletedTask;
    }

    private async Task UseLiveSubtitleAsync(CancellationToken ct, bool pauseForWait = false)
    {
        // Mid-playback switch from Off/Local/Online: honor wait-for-translation like open.
        if (pauseForWait && ShouldArmWaitOnLiveSwitch())
        {
            if (!Paused)
            {
                _mpv.SetPause(true);
                Paused = true;
            }

            if (!_waitingForFirstZh)
                BeginWaitForFirstZh();
        }

        var startedFresh = false;
        if (UsingExistingSub)
        {
            await StartPreviewIgnoringExternalAsync(ct).ConfigureAwait(false);
            startedFresh = true;
        }
        else if (_previewRunCts is null && CueCount == 0)
        {
            await StartPreviewAsync(ct).ConfigureAwait(false);
            startedFresh = true;
        }

        if (_displayMode == SubtitleDisplayMode.Off)
            SetDisplayMode(_lastContentMode, announce: !startedFresh);
        else if (!startedFresh)
        {
            if (!_waitingForFirstZh)
            {
                PublishStatus(Loc.Get("Main.Status.LiveActive"));
                ShowOsd(Loc.Get("Main.Osd.LiveActive"), 1400);
            }

            StateChanged?.Invoke();
        }
        else
            StateChanged?.Invoke();
    }

    /// <summary>
    /// Wait checkbox on, MT wanted, and translated coverage not yet at the configured target.
    /// Unlike open-path <see cref="ShouldWaitForFirstZh"/>, does not require AutoPlayOnOpen /
    /// AutoStartPreview — the user already chose Live while playing.
    /// </summary>
    private bool ShouldArmWaitOnLiveSwitch()
        => _settings.WaitForFirstZhBeforePlay
           && WantsPreviewMt
           && WantsTranslatedDisplay
           && !IsEnglishSource
           && !IsZhWaitSatisfied();

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
        var cts = Interlocked.Exchange(ref _previewRunCts, null);
        try { cts?.Cancel(); } catch { /* ignore */ }
        DisposeCtsDeferred(cts);
    }

    private static void DisposeCtsDeferred(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        // Linked job tokens may still observe this CTS briefly after Cancel.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                cts.Dispose();
            }
            catch { /* ignore */ }
        });
    }

    /// <summary>Single-flight preview start: a newer run cancels the previous one.</summary>
    private async Task StartPreviewInBackgroundAsync(int gen, CancellationToken ct)
    {
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var prev = Interlocked.Exchange(ref _previewRunCts, runCts);
        try { prev?.Cancel(); } catch { /* ignore */ }
        DisposeCtsDeferred(prev);

        var mediaPath = MediaPath;
        var busyEpoch = 0;
        var keepRunCts = false;
        try
        {
            busyEpoch = _prefetch?.EnterLiveBusy() ?? 0;
            await StartPreviewAsync(gen, runCts.Token, busyEpoch).ConfigureAwait(false);
            // Job poll links to runCts — keep it until CancelPreviewRun / next start.
            keepRunCts = _engine.HasActiveJob;
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
                await FinishWaitForFirstZhOnUiAsync(play: true, "原文提取启动失败，已开始播放").ConfigureAwait(false);
        }
        finally
        {
            if (!keepRunCts && ReferenceEquals(Volatile.Read(ref _previewRunCts), runCts))
            {
                Interlocked.CompareExchange(ref _previewRunCts, null, runCts);
                DisposeCtsDeferred(runCts);
            }
        }
    }

    public async Task StartPreviewIgnoringExternalAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");
        if (IsStreamPlayback)
            throw new InvalidOperationException(Loc.Get("Main.Status.StreamPlaybackOnly"));

        SaveSubtitleSource(SubtitleSourceKind.Live);
        UsingExistingSub = false;
        _subs.SetUsingExistingSub(false);
        ActiveSubPath = null;
        _externalOrigin = ExternalSubOrigin.None;
        _previewRetryAvailable = false;
        _manualPreviewNeeded = false;
        BeginFreshPreviewExtraction();
        if (_displayMode == SubtitleDisplayMode.Off)
            SetDisplayMode(_lastContentMode);
        ShowOsd(Loc.Get("Main.Osd.StartPreview"));
        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
    }

    public Task RetryPreviewAsync(CancellationToken ct)
        => StartPreviewIgnoringExternalAsync(ct);

    /// <summary>True when partial ASR restart from the playhead is meaningful.</summary>
    public bool CanRestartFromPlayhead =>
        ShowPreviewChrome
        && !string.IsNullOrWhiteSpace(MediaPath)
        && !UsingExistingSub
        && !IsStreamPlayback
        && Position >= 30
        && (CueCount > 0 || SubFrontier > 10);

    /// <summary>Keep cues before playhead; re-run ASR from the aligned chunk at the current position.</summary>
    public async Task RestartPreviewFromPlayheadAsync(CancellationToken ct = default)
    {
        if (!CanRestartFromPlayhead || string.IsNullOrWhiteSpace(MediaPath))
            return;

        var mediaPath = MediaPath;
        var pos = Position;
        var startFrom = ComputeAsrChunkStart(pos);
        PreviewPaths.ClearAsrDone(mediaPath);
        _subs.TruncateAfter(pos, PreviewPaths.SourceSrt(mediaPath));
        _subs.WatchSource();

        _nextPreviewStartFrom = startFrom;
        _nextPreviewSeedCues = _subs.SnapshotCues();
        _previewRetryAvailable = false;
        _announcedFirstSourceCue = _subs.CueCount > 0;
        _announcedFirstZhCue = false;
        _coverage.Reset();

        PublishStatus(Loc.Format("Main.Status.RestartFromPlayhead", MediaTimeFormat.Format(pos)));
        ShowOsd(Loc.Format("Main.Osd.RestartFromPlayhead", MediaTimeFormat.Format(pos)), 2800);
        _log($"从播放头重新识别 · {MediaTimeFormat.Format(pos)} · chunk {MediaTimeFormat.Format(startFrom)}");

        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
    }

    public Task StartPreviewAsync(CancellationToken ct)
        => StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct);

    private async Task StartPreviewAsync(int gen, CancellationToken ct, int liveBusyEpoch)
    {
        if (string.IsNullOrWhiteSpace(MediaPath))
            throw new InvalidOperationException("尚未打开影片。");
        if (IsStreamPlayback)
            return;

        ResolveScene();
        var startFrom = _nextPreviewStartFrom;
        var seedCues = _nextPreviewSeedCues;
        _nextPreviewStartFrom = 0;
        _nextPreviewSeedCues = null;
        var partialRestart = startFrom > 0.5;
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
                var installTarget = ModelPicker.InstallTarget(_settings.AsrModel);
                var prePacks = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
                if (!AsrQualities.IsUsable(installTarget, prePacks)
                    && !(prePacks.IsAsrInstalled(installTarget)
                         || (string.Equals(installTarget, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)
                             && prePacks.TurboInstalled)))
                    SetBootstrapPhase(BootstrapPhase.DownloadingModel);
            }
            catch
            {
                // probe optional; EnsureAsrModel will retry
            }

            var packs = await _engine.EnsureAsrModelAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            ThrowIfStaleMedia(gen);

            // Opening a file: never block on a modal gap dialog — fall back and start preview.
            if (!_depsCheckedForStart)
            {
                if (!await EnsureRuntimeDependenciesAsync(packs, ct, promptUi: false).ConfigureAwait(false))
                {
                    ThrowIfStaleMedia(gen);
                    EndBootstrap();
                    _status(Loc.Get("Main.Status.PresetCancelled"));
                    if (_waitingForFirstZh)
                        FinishWaitForFirstZh(play: true, "原文提取未启动，已开始播放");
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
            AsrModel = SceneProfiles.PickAsr(_settings.AsrModel, packs, ActiveScene.Language);
            MaybeLogAsrAutoPick(packs);
            var label = _engine.EngineLabel;
            EngineDetail = $"{label} · {AsrModel}";
            AnnounceAsrFallbackIfNeeded(
                AsrQualities.ResolvePreferred(_settings.AsrModel, ActiveScene.Language, packs),
                AsrModel,
                packs);

            if (MatchedScene is not null
                && SourceLanguages.IsAuto(_settings.SourceLanguage)
                && !SourceLanguages.IsAuto(MatchedScene.Language)
                && !SourceLanguageSense.IsWeakFilenamePrior(MatchedScene))
            {
                var matchedName = SourceLanguages.DisplayName(MatchedScene.Language);
                _log($"文件名匹配 · {matchedName}");
                ShowOsd(Loc.Format("Main.Osd.SourceLangMatched", matchedName), 2000);
            }

            var mediaPath = MediaPath!;
            var outDir = PreviewPaths.OutDir(mediaPath);
            Directory.CreateDirectory(outDir);
            _subs.SetOutputPaths(
                PreviewPaths.SourceSrt(mediaPath),
                PreviewPaths.TranslatedPreviewSrt(mediaPath, _settings.TranslateTarget),
                PreviewPaths.DualSrt(mediaPath),
                PreviewPaths.DisplaySrt(mediaPath));

            if (!partialRestart && PreviewPaths.HasReadyAsr(mediaPath))
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

                SetBootstrapPhase(BootstrapPhase.LoadingCache);
                await LoadCachedPreviewAsync(ct).ConfigureAwait(false);
                if (_waitingForFirstZh)
                    SetBootstrapPhase(BootstrapPhase.GeneratingZh);
                else
                    EndBootstrap();
                return;
            }

            ThrowIfStaleMedia(gen);
            await MaybeSenseSourceLanguageAsync(mediaPath, gen, ct).ConfigureAwait(false);
            ThrowIfStaleMedia(gen);

            // Re-probe after language sense (auto may resolve to a different installed model later).
            packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            var asrAfterSense = SceneProfiles.PickAsr(_settings.AsrModel, packs, ActiveScene.Language);
            if (!string.Equals(asrAfterSense, AsrModel, StringComparison.OrdinalIgnoreCase))
            {
                AsrModel = asrAfterSense;
                EngineDetail = $"{_engine.EngineLabel} · {AsrModel}";
                MaybeLogAsrAutoPick(packs);
                StateChanged?.Invoke();
            }

            // Drop stale SRT / in-memory cues so progress and mpv do not keep a deleted track.
            if (partialRestart)
            {
                _mpv.ClearSubtitle();
                ActiveSubPath = null;
            }
            else
            {
                BeginFreshPreviewExtraction();
            }

            _subs.SetOutputPaths(
                PreviewPaths.SourceSrt(mediaPath),
                PreviewPaths.TranslatedPreviewSrt(mediaPath, _settings.TranslateTarget),
                PreviewPaths.DualSrt(mediaPath),
                PreviewPaths.DisplaySrt(mediaPath));
            if (WantsPreviewMt)
                KickEnsureTranslate();
            _subs.WatchSource();

            // Start ASR before llama so status leaves「连接中」and source lines can appear.
            SetBootstrapPhase(BootstrapPhase.StartingAsr);
            PublishStatus(WantsPreviewMt ? Loc.Get("Main.Status.StartingAsrMt") : Loc.Get("Main.Status.StartingAsr"));

            var body = new AsrJobRequest(
                mediaPath,
                outDir,
                ActiveScene.Language,
                AsrModel,
                ActiveScene.ContentProfile,
                StartFromSeconds: startFrom,
                SeedCues: seedCues);
            await _engine.StartJobAsync(
                body,
                ct,
                async () =>
                {
                    if (!IsCurrentMedia(gen, mediaPath)) return;
                    _subs.OnSourceChanged();
                    if (WantsPreviewMt)
                        await EnsureTranslateGpuUpgradeAsync(ct).ConfigureAwait(false);
                    await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
                    if (IsCurrentMedia(gen, mediaPath))
                        PreviewPaths.MarkAsrDone(mediaPath);
                    _previewRetryAvailable = false;
                    PublishStatus(BuildStatusLine() + Loc.Get("Main.Status.AsrDone"));
                    MaybeOfferQualityHandoffTip();
                },
                terminal =>
                {
                    try
                    {
                        if (IsCurrentMedia(gen, mediaPath))
                        {
                            if (terminal == "error")
                                _previewRetryAvailable = true;
                            else if (terminal == "cancelled")
                                PublishStatus(Loc.Get("Main.Status.PreviewCancelled"));
                            StateChanged?.Invoke();
                        }
                    }
                    finally
                    {
                        // Epoch check inside ReleaseLiveBusy — always release even if media switched.
                        _prefetch?.ReleaseLiveBusy(liveBusyEpoch);
                    }
                },
                onSourceFlushed: () =>
                {
                    if (!IsCurrentMedia(gen, mediaPath)) return Task.CompletedTask;
                    _subs.OnSourceChanged();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            jobStarted = true;

            _coverage.OnAsrJobStarted(AsrModel, Duration);
            SetBootstrapPhase(BootstrapPhase.GeneratingSource);
            if (!_waitingForFirstZh)
                EndBootstrap();

            PublishStatus(BuildStatusLine(), $"任务 {_engine.JobId} · {SceneLogName} · {AsrModel} · {label}");
            PlayerLog.WriteEngine($"任务 {_engine.JobId} · {SceneLogName} · {AsrModel} · {label}");
            MaybeOfferLagMentalModelTip();
            _log(PreviewTextSanitize.DescribeReady(_settings));
            if (_settings.TextSanitizeEnabled && JaAsrDomainLexicon.LoadedFromPath is { } lexPath)
                _log("域词库 · " + lexPath);
            StateChanged?.Invoke();

            if (WantsPreviewMt)
            {
                PublishStatus(Loc.Get("Main.Status.AsrStartedMt"));
                KickEnsureTranslate();
                _ = _subs.TryTranslatePendingAsync();
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

    public void SetSourceLanguage(string lang)
        => _ = SetSourceLanguageAsync(lang, CancellationToken.None);

    public async Task SetSourceLanguageAsync(string lang, CancellationToken ct, bool fromPlayhead = false)
    {
        var prevLang = ActiveScene.Language;
        var prevRoute = ActiveMtRoute;
        var prevAsr = AsrModel;

        _settings.SourceLanguage = SourceLanguages.Normalize(lang);
        _settings.Save();
        if (!SourceLanguages.IsAuto(_settings.SourceLanguage))
            _sessionSensedLanguage = null;
        ResolveScene();
        PersistViewPrefs();
        StateChanged?.Invoke();

        if (IsLocalSubtitleSource || UsingExistingSub || string.IsNullOrWhiteSpace(MediaPath))
            return;

        RuntimePacks? packs = null;
        var depsOk = true;
        try
        {
            await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            packs = await _engine.EnsureAsrModelAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            depsOk = await EnsureRuntimeDependenciesAsync(packs, ct).ConfigureAwait(false);
            if (depsOk)
                _depsCheckedForStart = true;
        }
        catch (Exception ex)
        {
            _log("检查运行依赖：" + ex.Message);
            _depsCheckedForStart = false;
        }

        if (!depsOk)
        {
            PublishStatus(Loc.Get("Main.Status.PresetCancelled"));
            _depsCheckedForStart = false;
            if (_waitingForFirstZh)
                FinishWaitForFirstZh(play: true, Loc.Get("Main.Status.PresetCancelled"));
            return;
        }

        if (!ShowPreviewChrome || !_settings.AutoStartPreview)
            return;

        var newAsr = packs is not null
            ? SceneProfiles.PickAsr(_settings.AsrModel, packs, ActiveScene.Language)
            : AsrModel;
        var langChanged = !string.Equals(prevLang, ActiveScene.Language, StringComparison.OrdinalIgnoreCase);
        var mtChanged = prevRoute != ActiveMtRoute;
        var asrSame = !string.IsNullOrWhiteSpace(prevAsr)
                      && string.Equals(prevAsr, newAsr, StringComparison.OrdinalIgnoreCase);

        // Source language change requires a new ASR job; only retranslate when language is unchanged.
        if (!langChanged && mtChanged && asrSame && CueCount > 0)
        {
            await RetranslateLivePreviewAsync(
                ct,
                "Main.Osd.PresetRetranslating",
                "Main.Osd.PresetMtOff",
                continuePlayback: !Paused).ConfigureAwait(false);
            return;
        }

        if (langChanged && fromPlayhead && CanRestartFromPlayhead)
        {
            var pos = Position;
            var startFrom = ComputeAsrChunkStart(pos);
            PreviewPaths.ClearAsrDone(MediaPath!);
            _subs.TruncateAfter(pos, PreviewPaths.SourceSrt(MediaPath!));
            _nextPreviewStartFrom = startFrom;
            _nextPreviewSeedCues = _subs.SnapshotCues();
            _previewRetryAvailable = false;
            _announcedFirstSourceCue = _subs.CueCount > 0;
            _announcedFirstZhCue = false;
            _coverage.Reset();
            PublishStatus(Loc.Format("Main.Status.SourceLangFromPlayhead", MediaTimeFormat.Format(pos)));
            ShowOsd(Loc.Format("Main.Osd.SourceLangFromPlayhead", MediaTimeFormat.Format(pos)), 2800);
            _log($"片源语切换 · 从 {MediaTimeFormat.Format(pos)} 重新识别");
            await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
            return;
        }

        if (langChanged)
            MaybeBeginWaitForLangSwitch();

        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
    }

    public async Task<PresetGapReport?> ProbeRuntimeGapsAsync(CancellationToken ct)
    {
        try
        {
            await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            var packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
            if (!packs.TurboInstalled)
                packs = await _engine.EnsureAsrModelAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            var wantsMt = MtRoute.WantsTranslation(ActiveMtRoute) && _settings.TranslateEnabled;
            return PresetReadiness.AnalyzeDisk(_settings, wantsMt);
        }
        catch (Exception ex)
        {
            _log("探测运行依赖失败：" + ex.Message);
            return null;
        }
    }

    public void TogglePause()
    {
        if (_mpv.LiveLightIpc)
        {
            // No pause observe_property in live MVP mode — keep UI state locally.
            Paused = !Paused;
            _mpv.SetPause(Paused);
            StateChanged?.Invoke();
            return;
        }

        _mpv.TogglePause();
    }

    /// <summary>Poll time-pos for live (no observe_property flood).</summary>
    public void TickLiveClock()
    {
        if (!_mpv.LiveLightIpc || !_mpv.IsRunning || Paused)
            return;
        if (Interlocked.CompareExchange(ref _liveClockPollBusy, 1, 0) != 0)
            return;

        _ = PollLiveClockAsync();
    }

    /// <summary>
    /// Keep first-cue ETA text live on the wait overlay / status bar.
    /// Bootstrap detail is otherwise frozen until coverage changes (often only when the first cue arrives).
    /// </summary>
    public void TickFirstCueEta()
    {
        if (!_coverage.AwaitingFirstCue || CueCount > 0)
            return;

        if (_bootstrapActive
            && _bootstrapPhase is BootstrapPhase.GeneratingSource or BootstrapPhase.GeneratingZh)
        {
            RefreshBootstrapDetail();
            RefreshWaitZhOverlay();
        }

        if (ShowPreviewChrome)
            _status(BuildStatusLine());
    }

    private async Task PollLiveClockAsync()
    {
        try
        {
            var t = await _mpv.GetDoubleAsync("time-pos", 350).ConfigureAwait(false);
            if (t is double pos && pos >= 0)
                Position = pos;
        }
        finally
        {
            Interlocked.Exchange(ref _liveClockPollBusy, 0);
        }
    }

    public void Seek(double seconds)
    {
        if (Duration > 0)
            seconds = Math.Clamp(seconds, 0, Duration);
        else if (seconds < 0)
            seconds = 0;
        Position = seconds;
        _mpv.Seek(seconds);
        OnUserSeek();
    }

    public void SeekRelative(double delta)
    {
        var target = Position + delta;
        if (Duration > 0)
            target = Math.Clamp(target, 0, Duration);
        Position = Math.Max(0, target);
        _mpv.SeekRelative(delta);
        OnUserSeek();
    }

    public void SeekPercent(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (Duration > 0)
            Position = Duration * (percent / 100.0);
        _mpv.SeekPercent(percent);
        OnUserSeek();
    }

    /// <summary>
    /// After a user seek: apply deferred subtitle reload and re-kick MT so the queue
    /// prefers cues near the new playhead. ASR remains a whole-file job (engine has no startSec).
    /// </summary>
    private void OnUserSeek()
    {
        _subs.FlushPendingSubReload();
        // Fire-and-forget is safe: TryTranslatePendingAsync is single-flight via _translateBusy.
        _ = _subs.TryTranslatePendingAsync();
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

    public bool IsSubtitleReadyAt(double seconds)
        => !ShowPreviewChrome || SubtitleCoverageTracker.IsReadyAt(seconds, EffectiveReadyFrontier());

    /// <summary>True when playback position is ahead of generated subtitles (same threshold as lag OSD).</summary>
    public bool IsSeekPastSubtitleReady(double seconds, out double ready, out double gap)
    {
        ready = EffectiveReadyFrontier();
        gap = 0;
        if (!ShowPreviewChrome) return false;
        if (UsingExistingSub || _displayMode == SubtitleDisplayMode.Off) return false;
        gap = SubtitleCoverageTracker.GapPastReady(seconds, ready);
        // No frontier yet: mid-file seek while ASR is still warming up.
        if (ready <= 0)
            return (_coverage.AwaitingFirstCue || CueCount == 0) && seconds >= 3;
        return gap >= 2.0;
    }

    public int? FirstCueEtaSeconds => _coverage.EstimateSecondsToFirstCue();

    /// <summary>Wall-clock ETA for ASR to reach a media time (when rate is known).</summary>
    public int? EstimateSecondsToCoverage(double mediaSeconds)
        => _coverage.EstimateSecondsToReach(mediaSeconds, SubFrontier);

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
            SetDisplayMode(_lastContentMode, announce: true);
        else
            SetDisplayMode(SubtitleDisplayMode.Off, announce: true);
    }
    public string? Screenshot() => _mpv.Screenshot(_settings.ResolveScreenshotDir());

    public bool CanRecordStream =>
        !string.IsNullOrWhiteSpace(MediaPath)
        && MediaSourceHelper.IsRemoteUrl(MediaPath);

    public bool HasStreamQualities => _streamQualities.Count > 1;

    public IReadOnlyList<StreamQualityOption> StreamQualities => _streamQualities;

    public string? SelectedStreamQualityId => _streamQualityId;

    public bool IsRecording => _mpv.IsRecording;

    public TimeSpan RecordingElapsed => _mpv.RecordingElapsed;

    public async Task SetStreamQualityAsync(string qualityId)
    {
        if (string.IsNullOrWhiteSpace(qualityId) || _streamQualities.Count == 0) return;
        if (string.Equals(_streamQualityId, qualityId, StringComparison.OrdinalIgnoreCase)) return;
        if (IsRecording)
            throw new InvalidOperationException(Loc.Get("Main.StreamQuality.BusyRecording"));

        var opt = _streamQualities.FirstOrDefault(q =>
            string.Equals(q.Id, qualityId, StringComparison.OrdinalIgnoreCase));
        if (opt is null) return;

        _streamQualityId = opt.Id;
        var playUrl = StripchatLiveCdn.PreferPlayableCdn(opt.Url);
        var referer = _streamHeaders is not null
                      && _streamHeaders.TryGetValue("Referer", out var pageRef)
                      && !string.IsNullOrWhiteSpace(pageRef)
            ? pageRef
            : StripchatHlsPlaylist.CdnReferer;
        try
        {
            playUrl = await StripchatHlsPlaylist.EnsureProxiedPlayUrlAsync(playUrl, referer, CancellationToken.None, _liveMasterUrl)
                .ConfigureAwait(false);
            if (StripchatLiveCdn.IsSacdnssedge(playUrl))
            {
                playUrl = await StripchatHlsPlaylist.ResolveSacdnssedgeForMpvAsync(playUrl, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            _streamQualities = _streamQualities
                .Select(q => q.Id == opt.Id ? q with { Url = playUrl } : q)
                .ToList();
        }
        catch (Exception ex)
        {
            _log("清晰度代理：" + ex.Message);
        }

        _streamHeaders = HeadersForStreamUrl(playUrl, _streamHeaders);
        var wasPaused = Paused;
        try
        {
            _mpvPlayUrl = playUrl;
            _mpvPlayHeaders = _streamHeaders;
            _mpvPlayOsd = _streamDisplayName ?? MediaSourceHelper.DisplayName(MediaPath ?? "");
            if (!await EnsureLoadIntoMpvAsync(playUrl, autoPlay: true, _streamHeaders, _mpvPlayOsd).ConfigureAwait(false))
                throw new InvalidOperationException(Loc.Get("Main.Status.MpvLoadFailed"));
            if (wasPaused)
                _mpv.SetPause(true);
            Paused = wasPaused;
            PublishStreamStatus(Loc.Format("Main.Status.StreamQuality", opt.Label));
            ShowOsd(Loc.Format("Main.Osd.StreamQuality", opt.Label), 1600);
        }
        catch (Exception ex)
        {
            PublishStatus(ex.Message);
            _log("切换清晰度失败：" + ex.Message);
        }

        StateChanged?.Invoke();
    }

    private void ClearStreamQualities()
    {
        _streamQualities = [];
        _streamQualityId = null;
        _streamHeaders = null;
        _streamDisplayName = null;
        _mpvPlayUrl = null;
        _mpvPlayHeaders = null;
        _mpvPlayOsd = null;
        _liveMasterUrl = null;
        _streamBaseStatus = null;
        _streamBuffering = false;
        _streamBufferingPercent = 0;
    }

    private static IReadOnlyDictionary<string, string> HeadersForStreamUrl(
        string playUrl,
        IReadOnlyDictionary<string, string>? baseline)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseline is not null)
        {
            foreach (var kv in baseline)
                headers[kv.Key] = kv.Value;
        }

        headers["User-Agent"] = baseline is not null && baseline.TryGetValue("User-Agent", out var ua) && !string.IsNullOrWhiteSpace(ua)
            ? ua
            : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        if (playUrl.Contains("127.0.0.1", StringComparison.Ordinal))
        {
            headers["Referer"] = StripchatHlsPlaylist.CdnReferer;
            headers["Origin"] = "https://stripchat.com";
        }
        else if (StripchatLiveCdn.IsSacdnssedge(playUrl))
        {
            headers["Referer"] = StripchatLiveCdn.SacdnssedgePlayReferer;
            headers.Remove("Origin");
        }
        else
        {
            headers["Referer"] = StripchatLiveCdn.PlayReferer(playUrl);
            headers["Origin"] = "https://stripchat.com";
        }

        return headers;
    }

    public async Task StartStreamRecordAsync(string outputPath, CancellationToken ct = default)
    {
        if (!CanRecordStream)
            throw new InvalidOperationException(Loc.Get("Main.StreamRecord.NotStream"));
        await _mpv.StartStreamRecordAsync(outputPath, ct).ConfigureAwait(false);
        StateChanged?.Invoke();
    }

    public async Task<StreamRecordStopResult> StopStreamRecordAsync(CancellationToken ct = default)
    {
        var result = await _mpv.StopStreamRecordAsync(ct).ConfigureAwait(false);
        StateChanged?.Invoke();
        return result;
    }

    private async Task StopStreamRecordSilentlyAsync(CancellationToken ct = default)
    {
        if (!_mpv.IsRecording) return;
        try { await _mpv.StopStreamRecordAsync(ct).ConfigureAwait(false); }
        catch { /* ignore */ }
    }

    public void NudgeSubDelay(double deltaSeconds)
    {
        _settings.SubDelaySec = Math.Clamp(Math.Round(_settings.SubDelaySec + deltaSeconds, 1), -30, 30);
        _settings.SaveSoon();
        _mpv.SetSubDelay(_settings.SubDelaySec);
        var sign = _settings.SubDelaySec >= 0 ? "+" : "";
        ShowOsd(Loc.Format("Main.Osd.SubSync", sign, _settings.SubDelaySec), 1800);
        PersistViewPrefs();
    }

    public void ApplyPlayerSettings()
    {
        _mpv.ApplySubtitleSettings(_settings);
        _mpv.ApplyPlaybackSettings(_settings);
        ApplyDisplayVisibility();
        PersistViewPrefs();
    }

    /// <summary>
    /// After Settings dialog save: rebind engine if install/URL/models changed, and align translate with menu toggle.
    /// </summary>
    public async Task<IReadOnlyList<string>> ApplySettingsSideEffectsAsync(
        string prevAsrBackend,
        string prevModelsPath,
        string prevAdvancedLlmPath,
        string prevTranslateUrl,
        bool prevTranslateEnabled,
        string prevTranslateTarget,
        string? prevAsrModel = null,
        string? prevTranslateModelId = null,
        CancellationToken ct = default)
    {
        var osdParts = new List<string>();

        var backendChanged = !string.Equals(
            prevAsrBackend, _settings.AsrBackend, StringComparison.OrdinalIgnoreCase);
        var modelsPathChanged = !string.Equals(
            prevModelsPath, _settings.ModelsPath, StringComparison.OrdinalIgnoreCase);
        var advancedLlmPathChanged = !string.Equals(
            prevAdvancedLlmPath, _settings.AdvancedLlmPath, StringComparison.OrdinalIgnoreCase);
        var engineChanged = backendChanged || modelsPathChanged || advancedLlmPathChanged;

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

        if (prevTranslateEnabled != _settings.TranslateEnabled || translateUrlChanged || advancedLlmPathChanged)
            await ApplyTranslateEnabledAsync(ct).ConfigureAwait(false);

        var targetChanged = !string.Equals(
            TranslateTargets.Normalize(prevTranslateTarget),
            TranslateTargets.Normalize(_settings.TranslateTarget),
            StringComparison.OrdinalIgnoreCase);
        if (targetChanged)
            await ApplyTranslateTargetChangeAsync(ct).ConfigureAwait(false);

        var asrChanged = prevAsrModel is not null
            && !string.Equals(
                ModelPicker.Normalize(prevAsrModel),
                ModelPicker.Normalize(_settings.AsrModel),
                StringComparison.OrdinalIgnoreCase);
        if (asrChanged)
            await ApplyAsrModelChangeAsync(ct).ConfigureAwait(false);

        var translateModelChanged = prevTranslateModelId is not null
            && !string.Equals(
                TranslateModels.Normalize(prevTranslateModelId),
                TranslateModels.Normalize(_settings.TranslateModelId),
                StringComparison.OrdinalIgnoreCase);
        // ASR restart already clears/rebuilds MT; skip a second retranslate pass.
        if (translateModelChanged && !asrChanged)
            await ApplyTranslateModelChangeAsync(ct).ConfigureAwait(false);

        if (backendChanged && !asrChanged)
        {
            if (await TryRestartPreviewForBackendChangeAsync(ct).ConfigureAwait(false))
                osdParts.Add(Loc.Get("Main.Osd.AsrBackendRestart"));
            else
                osdParts.Add(Loc.Get("Main.Osd.AsrBackendSaved"));
        }
        else if (modelsPathChanged && !asrChanged && !backendChanged)
            osdParts.Add(Loc.Get("Main.Osd.ModelsPathSaved"));
        else if (advancedLlmPathChanged && !asrChanged && !backendChanged && !modelsPathChanged)
            osdParts.Add(Loc.Get("Main.Osd.AdvancedLlmPathSaved"));

        StateChanged?.Invoke();
        return osdParts;
    }

    private async Task<bool> TryRestartPreviewForBackendChangeAsync(CancellationToken ct)
    {
        if (IsLocalSubtitleSource || UsingExistingSub || string.IsNullOrWhiteSpace(MediaPath) || IsStreamPlayback)
            return false;
        if (!ShowPreviewChrome || !_settings.AutoStartPreview)
            return false;

        PublishStatus(Loc.Get("Main.Status.AsrBackendRestart"));
        BeginFreshPreviewExtraction();
        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>After preferred ASR model changes: invalidate cache and restart live extraction when applicable.</summary>
    public async Task ApplyAsrModelChangeAsync(CancellationToken ct = default)
    {
        ResolveScene();
        PacksChanged?.Invoke();
        StateChanged?.Invoke();

        if (IsLocalSubtitleSource || UsingExistingSub || string.IsNullOrWhiteSpace(MediaPath) || IsStreamPlayback)
            return;
        if (!ShowPreviewChrome || !_settings.AutoStartPreview)
            return;

        PublishStatus(Loc.Get("Main.Status.AsrModelRestart"));
        ShowOsd(Loc.Get("Main.Osd.AsrModelRestart"), 2200);
        BeginFreshPreviewExtraction();
        await StartPreviewInBackgroundAsync(Volatile.Read(ref _mediaGeneration), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clear in-memory cues + mpv track before deleting preview SRTs.
    /// Otherwise the seek bar keeps stale frontiers while the screen goes blank.
    /// </summary>
    private void BeginFreshPreviewExtraction()
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;
        _announcedFirstSourceCue = false;
        _announcedFirstZhCue = false;
        _fellBackToSourceThisMedia = false;
        _coverage.Reset();
        _subs.PrepareFreshRun();
        _mpv.ClearSubtitle();
        ActiveSubPath = null;
        PreviewPaths.InvalidatePreviewOutputs(MediaPath!);
        StateChanged?.Invoke();
    }

    /// <summary>After preferred MT GGUF changes: restart llama and retranslate existing cues.</summary>
    public async Task ApplyTranslateModelChangeAsync(CancellationToken ct = default)
    {
        ResolveScene();
        PacksChanged?.Invoke();
        await RetranslateLivePreviewAsync(
            ct,
            "Main.Osd.TranslateModelRetranslating",
            "Main.Osd.TranslateModelSourceOnly",
            refreshOutputPaths: true,
            continuePlayback: !Paused).ConfigureAwait(false);
    }

    private async Task ApplyTranslateTargetChangeAsync(CancellationToken ct)
    {
        ResolveScene();
        PacksChanged?.Invoke();
        await RetranslateLivePreviewAsync(
            ct,
            "Main.Osd.TranslateTargetRetranslating",
            "Main.Osd.TranslateTargetSourceOnly",
            refreshOutputPaths: true,
            continuePlayback: !Paused).ConfigureAwait(false);
    }

    private async Task RetranslateLivePreviewAsync(
        CancellationToken ct,
        string osdRetranslatingKey,
        string osdSourceOnlyKey,
        bool refreshOutputPaths = false,
        bool continuePlayback = false)
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

        _retranslatingActive = true;
        _subs.RefreshDisplaySub();
        if (continuePlayback && WantsTranslatedDisplay)
        {
            PublishStatus(Loc.Get("Main.Status.SwitchToSource"));
            ShowOsd(Loc.Get("Main.Osd.RetranslateContinueSource"), 2800);
        }
        else
        {
            PublishStatus(Loc.Get("Main.Status.Retranslating"));
            ShowOsd(Loc.Get(osdRetranslatingKey), 2800);
        }

        MaybeOfferTranslateTargetEnTip();
        try
        {
            await EnsureTranslateAsync(ct).ConfigureAwait(false);
            await _subs.RetranslateDrainAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _retranslatingActive = false;
            PublishStatus(BuildStatusLine());
        }

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
        _translateRunningOnCpu = false;
        try { _llama?.Dispose(); } catch { /* ignore */ }
        _llama = null;
    }

    public async Task ApplyTranslateEnabledAsync(CancellationToken ct = default)
    {
        // Start llama only when a media session actually needs MT — never on idle home / wizard.
        if (_settings.TranslateEnabled
            && WantsPreviewMt
            && !string.IsNullOrWhiteSpace(MediaPath))
        {
            await EnsureTranslateAsync(ct).ConfigureAwait(false);
            await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
        }
        else
        {
            if (!_settings.TranslateEnabled || !WantsPreviewMt)
                ResetTranslateStack();
            else
                _translateReady = null;
            _subs.RefreshDisplaySub();
        }

        PublishStatus(BuildStatusLine());
        StateChanged?.Invoke();
    }

    public void SavePlaybackPosition()
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;
        if (_settings.RememberPlaybackPosition)
            PlaybackPositionStore.Save(MediaPath, Position);
        PersistViewPrefs();
    }

    public void ClearPlaybackPosition()
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;
        PlaybackPositionStore.Save(MediaPath, 0);
        if (_pendingResumeAt > 0)
        {
            _pendingResumeAt = 0;
            StateChanged?.Invoke();
        }
    }

    private void PersistViewPrefs()
    {
        if (string.IsNullOrWhiteSpace(MediaPath)) return;
        if (MediaSourceHelper.IsNonLocalMedia(MediaPath)) return;
        PlaybackPositionStore.UpdateViewPrefs(
            MediaPath,
            _settings.SourceLanguage,
            SubtitleDisplayModeUtil.ToSetting(_displayMode),
            _settings.SubDelaySec,
            sensedSourceLanguage: SourceLanguages.IsAuto(_settings.SourceLanguage)
                ? (_sessionSensedLanguage ?? SensedSourceLanguage ?? "")
                : "");
    }

    /// <summary>
    /// Load per-file or same-folder sensed language into the session (settings stay <c>auto</c>).
    /// </summary>
    private void HydrateSessionSense(MediaSessionPrefs prefs, string mediaPath)
    {
        _sessionSensedLanguage = null;
        if (!SourceLanguages.IsAuto(_settings.SourceLanguage))
            return;

        string? lang = null;
        if (!string.IsNullOrWhiteSpace(prefs.SensedSourceLanguage))
        {
            var n = SourceLanguages.Normalize(prefs.SensedSourceLanguage);
            if (!SourceLanguages.IsAuto(n))
                lang = n;
        }

        if (lang is null)
        {
            var folder = PlaybackPositionStore.FindFolderSensedLanguage(mediaPath);
            if (!string.IsNullOrWhiteSpace(folder) && !SourceLanguages.IsAuto(folder))
                lang = SourceLanguages.Normalize(folder);
        }

        _sessionSensedLanguage = lang;
    }

    private void ApplyRememberedViewPrefs(MediaSessionPrefs prefs)
    {
        if (prefs is null || !prefs.HasViewPrefs) return;

        if (!string.IsNullOrWhiteSpace(prefs.SourceLanguage))
        {
            var remembered = SourceLanguages.Normalize(prefs.SourceLanguage);
            if (!string.Equals(SourceLanguages.Normalize(_settings.SourceLanguage), remembered, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SourceLanguage = remembered;
                _settings.SaveSoon();
                ResolveScene();
            }
        }

        if (prefs.SubDelaySec is double delay)
        {
            _settings.SubDelaySec = Math.Clamp(Math.Round(delay, 1), -30, 30);
            _settings.SaveSoon();
            try { _mpv.SetSubDelay(_settings.SubDelaySec); } catch { /* player may not be up yet */ }
        }

        if (!string.IsNullOrWhiteSpace(prefs.SubtitleMode))
        {
            var mode = SubtitleDisplayModeUtil.Parse(prefs.SubtitleMode);
            if (mode != _displayMode)
            {
                _displayMode = mode;
                if (SubtitleDisplayModeUtil.IsContentMode(mode))
                    _lastContentMode = mode;
                _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(mode);
                _settings.SubVisibleOnStart = mode != SubtitleDisplayMode.Off;
                _settings.SaveSoon();
            }
        }
    }

    private void MaybeAnnounceSessionRestored(MediaSessionPrefs prefs)
    {
        if (prefs is null || !prefs.HasViewPrefs) return;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(prefs.SourceLanguage)
            && !SourceLanguages.IsAuto(prefs.SourceLanguage))
            parts.Add(SourceLanguages.DisplayName(prefs.SourceLanguage));
        else if (SourceLanguages.IsAuto(_settings.SourceLanguage)
                 && !string.IsNullOrWhiteSpace(SensedSourceLanguage))
            parts.Add(Loc.Format(
                "Main.Status.SessionSensed",
                SourceLanguages.DisplayName(SensedSourceLanguage)));
        if (!string.IsNullOrWhiteSpace(prefs.SubtitleMode))
            parts.Add(ModeUiLabel(SubtitleDisplayModeUtil.Parse(prefs.SubtitleMode)));
        if (prefs.SubDelaySec is double d && Math.Abs(d) >= 0.05)
            parts.Add(Loc.Format("Main.Status.SessionDelay", d >= 0 ? "+" : "", d));
        if (parts.Count == 0) return;
        PublishStatus(Loc.Format("Main.Status.SessionRestored", string.Join(" · ", parts)));
    }

    /// <summary>MT / translate failure: keep playing, switch layout to source so the user is not stuck on empty translation.</summary>
    private void FallbackDisplayToSource(string statusLine)
    {
        if (_fellBackToSourceThisMedia) return;
        if (!ShowPreviewChrome) return;
        if (_displayMode is not (SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual))
            return;

        _fellBackToSourceThisMedia = true;
        if (SubtitleDisplayModeUtil.IsContentMode(_displayMode))
            _lastContentMode = _displayMode;
        _displayMode = SubtitleDisplayMode.Source;
        _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(SubtitleDisplayMode.Source);
        _settings.SaveSoon();
        ApplyDisplayVisibility();
        _subs.RefreshDisplaySub();
        ShowOsd(Loc.Get("Main.Osd.FallbackSource"), 2200);
        PublishStatus(statusLine);
        PersistViewPrefs();
        StateChanged?.Invoke();
    }

    public void ShowOsd(string text, int durationMs = 1200) => _mpv.ShowOsd(text, durationMs);

    public void SetDisplayMode(SubtitleDisplayMode mode, bool announce = false)
    {
        if (mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
            _fellBackToSourceThisMedia = false;

        if (mode == SubtitleDisplayMode.Off)
        {
            CancelWaitForModeReady(userOverride: false);
            _displayMode = SubtitleDisplayMode.Off;
            _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(mode);
            _settings.SubVisibleOnStart = false;
            _settings.SaveSoon();
            ApplyDisplayVisibility();
            PersistViewPrefs();
            StateChanged?.Invoke();
            if (announce)
                AnnounceDisplayMode(mode);
            return;
        }

        if (UsingExistingSub)
        {
            // 成片外挂只有显隐，无译文/原文/双语版式。
            CancelWaitForModeReady(userOverride: false);
            if (!_mpv.SubVisible)
                _mpv.SetSubVisible(true, showOsd: false);
            _displayMode = mode;
            if (SubtitleDisplayModeUtil.IsContentMode(mode))
                _lastContentMode = mode;
            _settings.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(mode);
            _settings.SubVisibleOnStart = true;
            _settings.SaveSoon();
            PersistViewPrefs();
            StateChanged?.Invoke();
            if (announce)
                AnnounceDisplayMode(mode);
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
        PersistViewPrefs();
        MaybeBeginWaitForModeReady(userInitiated: announce);
        StateChanged?.Invoke();
        if (announce)
            AnnounceDisplayMode(mode);
    }

    /// <summary>User-facing ack for 1/2/3 / source Off — OSD + status so layout switches are not silent.</summary>
    private void AnnounceDisplayMode(SubtitleDisplayMode mode)
    {
        var fullscreen = IsFullscreenProvider?.Invoke() == true;

        if (mode == SubtitleDisplayMode.Off)
        {
            PublishStatus(BuildStatusLine());
            ShowOsd(Loc.Get("Main.Osd.SubVisibleOff"), 1200);
            return;
        }

        if (UsingExistingSub)
        {
            PublishStatus(Loc.Get("Main.Status.Mode.External"));
            ShowOsd(Loc.Get("Main.Osd.SubVisibleOn"), 1200);
            return;
        }

        var label = ModeUiLabel(mode);
        if (_waitingForModeReady)
        {
            PublishStatus(Loc.Format("Main.Status.Mode.WaitingPause", label));
            ShowOsd(
                fullscreen
                    ? Loc.Format("Main.Osd.Mode.Waiting.Fullscreen", label)
                    : Loc.Format("Main.Osd.Mode.Waiting", label),
                1600);
            return;
        }

        if (CueCount == 0)
        {
            PublishStatus(Loc.Format("Main.Status.Mode.Waiting", label));
            ShowOsd(
                fullscreen
                    ? Loc.Format("Main.Osd.Mode.Waiting.Fullscreen", label)
                    : Loc.Format("Main.Osd.Mode.Waiting", label),
                1600);
            return;
        }

        if (ShowingZhPending && mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
        {
            PublishStatus(Loc.Format("Main.Status.Mode.Pending", label));
            ShowOsd(
                fullscreen
                    ? Loc.Format("Main.Osd.Mode.Pending.Fullscreen", label)
                    : Loc.Format("Main.Osd.Mode.Pending", label),
                1600);
            return;
        }

        PublishStatus(Loc.Format("Main.Status.Mode.Switched", label));
        ShowOsd(
            fullscreen
                ? Loc.Format("Main.Osd.Mode.Switched.Fullscreen", label)
                : Loc.Format("Main.Osd.Mode.Switched", label),
            1200);
    }

    private static double ComputeAsrChunkStart(double playheadSeconds)
    {
        if (playheadSeconds <= AsrChunkStepSec)
            return 0;
        var chunk = (int)Math.Floor(playheadSeconds / AsrChunkStepSec);
        return Math.Max(0, (chunk - 1) * AsrChunkStepSec);
    }

    private string ModeUiLabel(SubtitleDisplayMode mode) => mode switch
    {
        SubtitleDisplayMode.Source => Loc.Get("Main.Mode.Src"),
        SubtitleDisplayMode.Dual => TranslateTargetUi.ModeDualLabel(_settings),
        SubtitleDisplayMode.Off => Loc.Get("Main.SubSource.Off"),
        _ => TranslateTargetUi.ModeTranslationLabel(_settings),
    };

    private void ApplyDisplayVisibility()
    {
        var wantVisible = _displayMode != SubtitleDisplayMode.Off;
        if (_mpv.SubVisible != wantVisible)
            _mpv.SetSubVisible(wantVisible, showOsd: false);
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

        await StopStreamRecordSilentlyAsync(ct).ConfigureAwait(false);
        StripchatHlsProxy.StopShared();

        Interlocked.Increment(ref _mediaGeneration);
        _prefetch?.SetLiveBusy(true);
        CancelPreviewRun();
        ResetSubtitleWaits();
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
        try { _finishedSub.Disarm(); } catch { /* ignore */ }
        _pendingResumeAt = 0;
        UsingExistingSub = false;
        ActiveSubPath = null;
        _externalOrigin = ExternalSubOrigin.None;
        ClearStreamQualities();
        VideoWidth = 0;
        VideoHeight = 0;
        Position = 0;
        Duration = 0;
        Paused = true;
        _translateReady = null;
        _translateRunningOnCpu = false;
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
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }
        try { SavePlaybackPosition(); } catch { /* ignore */ }
        try { await StopStreamRecordSilentlyAsync().ConfigureAwait(false); } catch { /* ignore */ }
        // Silence video immediately before GPU/cache teardown (may take up to HttpBudget).
        StopPlaybackImmediate();
        StripchatHlsProxy.StopShared();
        CancelPreviewRun();
        ResetSubtitleWaits();
        try { _finishedSub.Dispose(); } catch { /* ignore */ }
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
        try { _lifetimeCts.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }
        try { SavePlaybackPosition(); } catch { /* ignore */ }
        CancelWaitForFirstZh();
        CancelWaitForModeReady(userOverride: false);
        try { _finishedSub.Dispose(); } catch { /* ignore */ }
        try { _prefetch?.Dispose(); } catch { /* ignore */ }
        _prefetch = null;
        try { _subs.Dispose(); } catch { /* ignore */ }
        try { _llama?.Dispose(); } catch { /* ignore */ }
        _llama = null;
        try { _translateGate.Dispose(); } catch { /* ignore */ }
        try { _engine.Dispose(); } catch { /* ignore */ }
        try { _mpv.Dispose(); } catch { /* ignore */ }
        try { _settings.Save(); } catch { /* flush debounced */ }
        try { _lifetimeCts.Dispose(); } catch { /* ignore */ }
    }

    private bool WantsPreviewMt =>
        MtRoute.WantsTranslation(ActiveMtRoute) && _settings.TranslateEnabled && !_mtDisabledForSession;

    /// <summary>Display modes that expect translated lines (not source-only / off).</summary>
    private bool WantsTranslatedDisplay
        => _displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual;

    private bool ShouldWaitForFirstZh()
        => _settings.AutoPlayOnOpen
           && _settings.AutoStartPreview
           && WantsPreviewMt
           && WantsTranslatedDisplay
           && !IsEnglishSource
           && !_settings.PlayImmediatelyOnOpen;

    /// <summary>Translation timeline (seconds) required before unpausing on open.</summary>
    private double EffectiveWaitZhTargetSeconds()
    {
        var minutes = _settings.WaitForFirstZhBeforePlay
            ? Math.Clamp(_settings.WaitForZhMinutes, 0, 30)
            : 0;
        var baseTarget = minutes <= 0 ? 0.05 : minutes * 60.0;
        // Mid-file resume: need coverage at the playhead, not only from t=0.
        var pos = Math.Max(0, Position);
        if (pos > 1.5)
            return Math.Max(baseTarget, pos);
        return baseTarget;
    }

    private double WaitZhTargetSeconds => EffectiveWaitZhTargetSeconds();

    private void BeginWaitForFirstZh()
    {
        CancelWaitForModeReady(userOverride: false);
        _waitingForFirstZh = true;
        _mpv.SetPause(true);
        Paused = true;
        if (_settings.AutoStartPreview && !_bootstrapActive)
            SetBootstrapPhase(BootstrapPhase.ConnectingEngine);
        RefreshWaitZhOverlay();
        var until = MediaTimeFormat.Format(WaitZhTargetSeconds);
        PublishStatus(Loc.Format("Main.Status.WaitZhUntil", until));
        ShowOsd(Loc.Format("Main.Osd.WaitZhUntil", until), 2200);
        if (WantsPreviewMt)
            KickEnsureTranslate();
        StartWaitZhTimeout();
        StateChanged?.Invoke();
    }

    private void OnSubtitleStateChanged()
    {
        _coverage.OnCoverage(SubFrontier, CueCount);
        RefreshBootstrapProgress();
        MaybeAnnounceSubtitleMilestones();
        MaybeFinishModeWaitIfReady();
        StateChanged?.Invoke();
    }

    /// <summary>First source / translation cues: tell the user subtitles are actually working.</summary>
    private void MaybeAnnounceSubtitleMilestones()
    {
        if (UsingExistingSub || string.IsNullOrWhiteSpace(MediaPath))
            return;

        if (!_announcedFirstSourceCue && CueCount > 0)
        {
            _announcedFirstSourceCue = true;
            PublishStatus(BuildStatusLine());
            ShowOsd(Loc.Get("Main.Osd.FirstSourceCue"), 1800);
        }

        if (!_announcedFirstZhCue && TranslatedCount > 0 && WantsPreviewMt)
        {
            _announcedFirstZhCue = true;
            PublishStatus(BuildStatusLine());
            // Only flash when user is waiting on translation layout; source-only mode already feels "alive".
            if (_displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
                ShowOsd(Loc.Get("Main.Osd.FirstZhCue"), 1800);
        }
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

        if (_bootstrapPhase is BootstrapPhase.PreparingTranslate or BootstrapPhase.LoadingCache
            && CueCount > 0)
        {
            if (!_waitingForFirstZh)
            {
                EndBootstrap();
                return;
            }

            if (WantsPreviewMt && (TranslatedCount > 0 || SubFrontier > 2))
            {
                _bootstrapPhase = BootstrapPhase.GeneratingZh;
                _bootstrapPhaseTitle = BootstrapPhaseTitle(_bootstrapPhase);
            }
            else
            {
                _bootstrapPhase = BootstrapPhase.GeneratingSource;
                _bootstrapPhaseTitle = BootstrapPhaseTitle(_bootstrapPhase);
            }
        }
        else if (_bootstrapPhase == BootstrapPhase.GeneratingSource
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
            BootstrapPhase.GeneratingSource => FormatGeneratingSourceDetail(),
            BootstrapPhase.GeneratingZh => BuildWaitZhDetail(),
            _ => _bootstrapPhaseDetail,
        };
    }

    private string FormatGeneratingSourceDetail()
    {
        if (_coverage.EstimateSecondsToFirstCue() is int eta && CueCount == 0)
            return Loc.Format("Main.Bootstrap.FirstCueEta", eta);
        return Loc.Format("Main.Bootstrap.SourceProgress", MediaTimeFormat.Format(SubFrontier));
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
        if (_waitingForFirstZh)
        {
            RefreshBootstrapProgress();
            StateChanged?.Invoke();
            if (IsZhWaitSatisfied())
                _ = FinishWaitForFirstZhOnUiAsync(play: true, "译文已就绪，开始播放");
            return;
        }

        MaybeFinishModeWaitIfReady();
    }

    private bool IsDisplayModeReady(SubtitleDisplayMode mode)
    {
        if (!ShowPreviewChrome)
            return true;

        // 「对应字幕」= 当前播放进度已被该版式的前沿覆盖（不是「曾经有过任意一句」）。
        var pos = Math.Max(0, Position);
        const double slack = 1.5;
        return mode switch
        {
            SubtitleDisplayMode.Off => true,
            SubtitleDisplayMode.Source => CueCount > 0
                                          && SubFrontier > 0.05
                                          && pos <= SubFrontier + slack,
            SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual when WantsPreviewMt
                => TranslatedCount > 0
                   && ZhFrontier > 0.05
                   && pos <= ZhFrontier + slack,
            SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual
                => CueCount > 0
                   && SubFrontier > 0.05
                   && pos <= SubFrontier + slack,
            _ => true,
        };
    }

    private void MaybeBeginWaitForModeReady(bool userInitiated)
    {
        if (_waitingForFirstZh || !ShowPreviewChrome)
        {
            CancelWaitForModeReady(userOverride: false);
            return;
        }

        if (!userInitiated)
            return;

        if (IsDisplayModeReady(_displayMode))
        {
            if (_waitingForModeReady)
                FinishWaitForModeReady(play: _resumeAfterModeWait);
            return;
        }

        if (_waitingForModeReady)
        {
            RefreshWaitZhOverlay();
            StateChanged?.Invoke();
            return;
        }

        _coverageWaitReason = CoverageWaitReason.DisplayMode;
        _waitingForModeReady = true;
        _resumeAfterModeWait = !Paused;
        // Always assert pause — local Paused can lag behind mpv IPC.
        _mpv.SetPause(true);
        Paused = true;
        RefreshWaitZhOverlay();
        _log($"切换{ModeUiLabel(_displayMode)} · 字幕未覆盖进度 {MediaTimeFormat.Format(Position)}，已暂停等待");
        if (WantsPreviewMt
            && _displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual
            && _translateReady != true)
        {
            KickEnsureTranslate();
        }

        StateChanged?.Invoke();
    }

    private void MaybeFinishModeWaitIfReady()
    {
        if (!_waitingForModeReady) return;
        if (!IsDisplayModeReady(_displayMode))
        {
            RefreshWaitZhOverlay();
            return;
        }

        FinishWaitForModeReady(play: _resumeAfterModeWait);
    }

    private void FinishWaitForModeReady(bool play)
    {
        if (!_waitingForModeReady) return;
        var reason = _coverageWaitReason;
        _waitingForModeReady = false;
        _resumeAfterModeWait = false;
        _coverageWaitReason = CoverageWaitReason.None;
        RefreshWaitZhOverlay();
        if (play && Paused)
            _mpv.SetPause(false);
        PublishStatus(BuildStatusLine());
        ShowOsd(
            reason == CoverageWaitReason.LanguageSwitch
                ? Loc.Get("Main.Osd.LangSwitch.Ready")
                : Loc.Format("Main.Osd.Mode.Switched", ModeUiLabel(_displayMode)),
            1200);
        StateChanged?.Invoke();
    }

    private void CancelWaitForModeReady(bool userOverride)
    {
        if (!_waitingForModeReady) return;
        _waitingForModeReady = false;
        _resumeAfterModeWait = false;
        _coverageWaitReason = CoverageWaitReason.None;
        RefreshWaitZhOverlay();
        if (userOverride)
        {
            _log("用户跳过模式等待 · 继续播放");
            PublishStatus(BuildStatusLine());
        }
        StateChanged?.Invoke();
    }

    private bool IsZhWaitSatisfied()
    {
        var frontier = ZhFrontier;
        var need = WaitZhTargetSeconds;
        if (frontier + 0.05 >= need) return true;
        if (Duration > 1 && frontier + 0.5 >= Duration) return true;
        return false;
    }

    /// <summary>During playback, pause until subtitles cover the playhead after a language switch.</summary>
    private void MaybeBeginWaitForLangSwitch()
    {
        if (_waitingForFirstZh || !ShowPreviewChrome)
            return;
        if (UsingExistingSub || IsLocalSubtitleSource || string.IsNullOrWhiteSpace(MediaPath))
            return;
        if (Paused)
            return;

        if (_waitingForModeReady)
        {
            _coverageWaitReason = CoverageWaitReason.LanguageSwitch;
            RefreshWaitZhOverlay();
            StateChanged?.Invoke();
            return;
        }

        _coverageWaitReason = CoverageWaitReason.LanguageSwitch;
        _waitingForModeReady = true;
        _resumeAfterModeWait = true;
        _mpv.SetPause(true);
        Paused = true;
        RefreshWaitZhOverlay();
        PublishStatus(Loc.Get("Main.Status.LangSwitch.WaitingPause"));
        ShowOsd(Loc.Get("Main.Osd.LangSwitch.WaitingPause"), 1800);
        _log($"切换语言 · 等待字幕覆盖进度 {MediaTimeFormat.Format(Position)}，已暂停");
        if (WantsPreviewMt
            && WantsTranslatedDisplay
            && _translateReady != true)
        {
            KickEnsureTranslate();
        }

        StateChanged?.Invoke();
    }

    private void RefreshWaitZhOverlay()
    {
        if (_waitingForModeReady)
        {
            WaitZhOverlayTitle = _coverageWaitReason == CoverageWaitReason.LanguageSwitch
                ? Loc.Get("Main.WaitLang.Title")
                : Loc.Format("Main.WaitMode.Title", ModeUiLabel(_displayMode));
            WaitZhOverlayDetail = BuildModeWaitDetail();
            return;
        }

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

    private string BuildModeWaitDetail()
    {
        if ((_displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual) && WantsPreviewMt)
        {
            return CueCount == 0
                ? Loc.Format("Main.Bootstrap.SourceProgress", MediaTimeFormat.Format(SubFrontier))
                : Loc.Format("Main.WaitZh.ProgressFirst", MediaTimeFormat.Format(ZhFrontier));
        }

        return Loc.Format("Main.Bootstrap.SourceProgress", MediaTimeFormat.Format(SubFrontier));
    }

    private void OnZhTranslationFailed()
    {
        if (_waitingForFirstZh)
            _ = FinishWaitForFirstZhOnUiAsync(play: true, Loc.Get("Main.Osd.FallbackSourcePlay"));
        FallbackDisplayToSource(Loc.Get("Main.Status.FallbackSource.Mt"));
    }

    private void ResetSubtitleWaits()
    {
        CancelWaitForFirstZh();
        CancelWaitForModeReady(userOverride: false);
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

    private Task FinishWaitForFirstZhOnUiAsync(bool play, string? osdMessage = null)
    {
        if (_host.Dispatcher.CheckAccess())
        {
            FinishWaitForFirstZh(play, osdMessage);
            return Task.CompletedTask;
        }

        return _host.Dispatcher.InvokeAsync(() => FinishWaitForFirstZh(play, osdMessage)).Task;
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
                await FinishWaitForFirstZhOnUiAsync(play: true, "等待译文超时，已开始播放").ConfigureAwait(false);
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

    private string SceneLogName
    {
        get
        {
            var lang = SourceLanguages.DisplayName(ActiveScene.Language);
            var asr = string.IsNullOrWhiteSpace(AsrModel)
                ? AsrModelCatalog.DisplayName(ModelPicker.InstallTarget(_settings.AsrModel))
                : AsrModelCatalog.DisplayName(AsrModel);
            return $"{lang} · {asr}";
        }
    }

    private void MaybeLogAsrAutoPick(RuntimePacks packs)
    {
        _ = packs;
        if (!ModelPicker.IsAuto(_settings.AsrModel)) return;
        if (!string.Equals(AsrModel, ModelPicker.Turbo, StringComparison.OrdinalIgnoreCase)) return;
        var lang = SourceLanguages.Normalize(ActiveScene.Language);
        if (lang != SourceLanguages.Auto)
            _log("自动选用 whisper turbo（" + SourceLanguages.DisplayName(lang) + "）");
    }

    private void ResolveScene()
    {
        ActiveScene = SceneProfiles.Resolve(_settings.SourceLanguage, MediaPath, out var matched);
        MatchedScene = matched;
        SensedSourceLanguage = null;

        if (!SourceLanguages.IsAuto(_settings.SourceLanguage))
            return;
        if (string.IsNullOrWhiteSpace(_sessionSensedLanguage))
            return;
        // Strong filename prior wins over remembered sense.
        if (matched is not null
            && !SourceLanguages.IsAuto(matched.Language)
            && !SourceLanguageSense.IsWeakFilenamePrior(matched))
            return;

        var sensed = SourceLanguages.Normalize(_sessionSensedLanguage);
        if (SourceLanguages.IsAuto(sensed))
            return;
        SensedSourceLanguage = sensed;
        ActiveScene = ActiveScene with { Language = sensed };
    }

    /// <summary>
    /// When source is auto and filename did not lock a lang, short-window Whisper LID fills ActiveScene.
    /// Does not change <see cref="AppSettings.SourceLanguage"/> (stays auto; user override still wins).
    /// </summary>
    private async Task MaybeSenseSourceLanguageAsync(string mediaPath, int gen, CancellationToken ct)
    {
        if (!SourceLanguageSense.ShouldProbe(_settings.SourceLanguage, MatchedScene))
            return;

        // Already restored from resume.json or same-folder siblings.
        if (!string.IsNullOrWhiteSpace(SensedSourceLanguage)
            && !SourceLanguages.IsAuto(SensedSourceLanguage))
        {
            var remembered = SourceLanguages.DisplayName(SensedSourceLanguage);
            _log($"沿用感知 · {remembered}");
            ShowOsd(Loc.Format("Main.Osd.SourceLangRemembered", remembered), 2000);
            return;
        }

        PublishStatus(Loc.Get("Main.Status.SensingSourceLang"));
        try
        {
            var json = await _engine.DetectLanguageAsync(
                    mediaPath,
                    AsrModel,
                    SourceLanguageSense.DurationSec,
                    SourceLanguageSense.StartSec,
                    ct)
                .ConfigureAwait(false);
            ThrowIfStaleMedia(gen);

            if (!SourceLanguageSense.TryParse(json, MatchedScene, out var lang, out var confidence))
            {
                _log($"语种探测未采纳 · conf={confidence:0.00}");
                return;
            }

            _sessionSensedLanguage = lang;
            SensedSourceLanguage = lang;
            ActiveScene = ActiveScene with { Language = lang };
            PersistViewPrefs();
            var name = SourceLanguages.DisplayName(lang);
            _log($"音频感知 · {name} · {confidence:0.00}");

            if (MatchedScene is not null
                && !SourceLanguages.IsAuto(MatchedScene.Language)
                && !SourceLanguages.EqualsLang(MatchedScene.Language, lang))
            {
                ShowOsd(
                    Loc.Format(
                        "Main.Osd.SourceLangSensedOverride",
                        SourceLanguages.DisplayName(MatchedScene.Language),
                        name),
                    3200);
            }
            else
            {
                ShowOsd(Loc.Format("Main.Osd.SourceLangSensed", name), 2500);
            }

            if (!MtRoute.WantsTranslation(ActiveMtRoute))
                _log(Loc.Get("Main.Osd.PresetMtOff"));

            StateChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log("语种探测跳过 · " + ex.Message);
        }
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
        PresetReadiness.UpdateLivePacks(packs);
        PacksChanged?.Invoke();
    }

    private async Task<bool> EnsureRuntimeDependenciesAsync(
        RuntimePacks packs,
        CancellationToken ct,
        bool promptUi = true)
    {
        var report = BuildGapReport(packs);
        if (!report.HasGaps)
        {
            _pendingGapReport = null;
            return true;
        }

        var skipKey = GapSkipKey();

        if (_skipGapPrompts.Contains(skipKey))
        {
            _log("跳过依赖提示 · " + report.SummaryLine());
            return !HasBlockingAsrGaps(report);
        }

        // Open-media path: never block on a modal. ASR gaps cancel preview; MT-only gaps continue.
        if (!promptUi)
        {
            _pendingGapReport = report;
            if (HasBlockingAsrGaps(report))
            {
                _log("缺识别依赖 · " + report.SummaryLine());
                PublishStatus(Loc.Format("Main.Status.DepsMissingAsr", SceneLogName, report.SummaryLine()));
                ShowOsd(Loc.Format("Main.Osd.DepsMissingAsr", SceneLogName), 3500);
                PacksChanged?.Invoke();
                return false;
            }

            _skipGapPrompts.Add(skipKey);
            _log("缺翻译依赖，先出原文 · " + report.SummaryLine());
            PublishStatus(Loc.Format("Main.Status.DepsDowngrade", SceneLogName, report.SummaryLine()));
            ShowOsd(Loc.Format("Main.Osd.DepsDowngrade", SceneLogName), 3500);
            PacksChanged?.Invoke();
            return true;
        }

        _pendingGapReport = null;

        if (OfferPresetSetupAsync is null)
        {
            _log("缺依赖（无 UI 回调）· " + report.SummaryLine());
            return !HasBlockingAsrGaps(report);
        }

        _status("等待确认组件…");
        var choice = await OfferPresetSetupAsync(report).ConfigureAwait(false);
        switch (choice)
        {
            case PresetSetupChoice.Cancel:
                return false;
            case PresetSetupChoice.UseFallback:
                // Legacy choice (tiny removed): treat as cancel for ASR gaps, continue for MT-only.
                _skipGapPrompts.Add(skipKey);
                return !HasBlockingAsrGaps(report);
            case PresetSetupChoice.ManualInstall:
            {
                packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
                PublishPacks(packs);
                report = BuildGapReport(packs);
                if (report.HasGaps)
                {
                    _skipGapPrompts.Add(skipKey);
                    if (HasBlockingAsrGaps(report))
                    {
                        _status(Loc.Get("Main.Status.DepsMissingAsrShort"));
                        return false;
                    }

                    _status(Loc.Get("Main.Status.DepsMtMissingContinue"));
                }
                else
                {
                    _status("依赖已检测到，继续");
                }

                return true;
            }
            case PresetSetupChoice.AutoInstall:
            {
                // Install already ran inside PresetSetupDialog with progress UI.
                try
                {
                    await RefreshGapsAfterInstallAsync(ct).ConfigureAwait(false);
                    if (_pendingGapReport?.HasGaps == true)
                    {
                        _log("安装后仍缺：" + _pendingGapReport.SummaryLine());
                        if (HasBlockingAsrGaps(_pendingGapReport))
                        {
                            _status(Loc.Get("Main.Status.DepsMissingAsrShort"));
                            return false;
                        }

                        _status(Loc.Get("Main.Status.DepsMtMissingContinue"));
                        _skipGapPrompts.Add(skipKey);
                    }
                    else
                    {
                        ShowOsd(Loc.Format("Settings.Presets.InstallDone", SceneLogName), 1800);
                    }
                }
                catch (Exception ex)
                {
                    _log("自动安装后刷新失败：" + ex.Message);
                    _skipGapPrompts.Add(skipKey);
                    return !HasBlockingAsrGaps(report);
                }

                return true;
            }
            default:
                return true;
        }
    }

    private static bool HasBlockingAsrGaps(PresetGapReport report)
        => report.Gaps.Any(g => g.Kind is PresetGapKind.AsrModel);

    /// <summary>Install gaps reported by readiness (used by setup dialog progress UI).</summary>
    public Task InstallGapsAsync(PresetGapReport report, Action<string> status, CancellationToken ct)
    {
        var installer = new PresetDependencyInstaller(_settings, status, _log);
        return installer.InstallAsync(report, _engine, ct);
    }

    /// <summary>After dialog auto-install: re-probe packs and refresh「去安装」.</summary>
    public async Task RefreshGapsAfterInstallAsync(CancellationToken ct)
    {
        try
        {
            await _engine.EnsureReadyAsync(ct).ConfigureAwait(false);
            var packs = await _engine.ProbePacksAsync(ct).ConfigureAwait(false);
            PublishPacks(packs);
            var report = BuildGapReport(packs);
            _pendingGapReport = report.HasGaps ? report : null;
            PacksChanged?.Invoke();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log("安装后探测失败：" + ex.Message);
        }
    }

    private PresetGapReport BuildGapReport(RuntimePacks packs)
    {
        var wantsMt = MtRoute.WantsTranslation(ActiveMtRoute) && _settings.TranslateEnabled;
        var llamaOk = ManagedLlmInstaller.HasLlamaRuntime(_settings);
        var ggufOk = ManagedLlmInstaller.HasPreferredGguf(_settings);
        // If llama is already healthy on configured URL, treat MT runtime as ready.
        var translateProbeReady = _translateReady == true
            || (wantsMt && llamaOk && ggufOk);

        return PresetReadiness.Analyze(
            _settings.AsrModel,
            _engine.ModelsRoot,
            packs,
            wantsMt,
            translateReady: translateProbeReady,
            llamaRuntimePresent: llamaOk,
            preferredGgufPresent: ggufOk,
            translateModelId: _settings.TranslateModelId,
            mtModelsDir: AppPaths.ResolveAdvancedLlmModelsDir(_settings));
    }

    private string GapSkipKey()
        => $"{ModelPicker.Normalize(_settings.AsrModel)}|{ActiveScene.Language}|{_settings.TranslateTarget}|{_settings.TranslateEnabled}|{TranslateModels.Normalize(_settings.TranslateModelId)}";

    private IReadOnlyList<string> PreferredGgufs
        => TranslateModels.PreferredFilenames;

    private void KickEnsureTranslate()
    {
        if (_disposed != 0) return;
        CancellationToken ct;
        try { ct = _lifetimeCts.Token; }
        catch (ObjectDisposedException) { return; }

        _ = EnsureTranslateFireAndForgetAsync(ct);
    }

    private void KickEnsureTranslateGpuUpgrade()
    {
        if (_disposed != 0 || !_translateRunningOnCpu) return;
        CancellationToken ct;
        try { ct = _lifetimeCts.Token; }
        catch (ObjectDisposedException) { return; }

        _ = EnsureTranslateGpuUpgradeFireAndForgetAsync(ct);
    }

    private async Task EnsureTranslateGpuUpgradeAsync(CancellationToken ct)
    {
        if (_disposed != 0 || !_translateRunningOnCpu || _engine.HasActiveJob) return;
        await EnsureTranslateAsync(ct, forceGpuRestart: true).ConfigureAwait(false);
        await _subs.TryTranslatePendingAsync().ConfigureAwait(false);
    }

    private async Task EnsureTranslateGpuUpgradeFireAndForgetAsync(CancellationToken ct)
    {
        try
        {
            if (_disposed != 0) return;
            await EnsureTranslateGpuUpgradeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown / media switch
        }
        catch (ObjectDisposedException)
        {
            // gate or llama already torn down
        }
        catch (Exception ex)
        {
            _log("翻译模型 GPU 切换：" + ex.Message);
        }
    }

    private async Task EnsureTranslateFireAndForgetAsync(CancellationToken ct)
    {
        try
        {
            if (_disposed != 0) return;
            await EnsureTranslateAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown / media switch
        }
        catch (ObjectDisposedException)
        {
            // gate or llama already torn down
        }
        catch (Exception ex)
        {
            _log("翻译模型启动：" + ex.Message);
        }
    }

    private async Task EnsureTranslateAsync(CancellationToken ct, bool forceGpuRestart = false)
    {
        if (_disposed != 0) return;
        await _translateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed != 0) return;
            var url = string.IsNullOrWhiteSpace(_settings.TranslateUrl)
                ? LlamaServerProcess.DefaultBaseUrl
                : _settings.TranslateUrl.Trim().TrimEnd('/');
            _settings.TranslateUrl = url;

            try
            {
                // Do not unload ASR GPU while a job is still running (MT reconnect mid-preview).
                var asrHoldsGpu = _engine.HasActiveJob;
                if (forceGpuRestart && _translateRunningOnCpu && !asrHoldsGpu)
                {
                    _llama?.Stop();
                    _translateReady = null;
                }
                else if (_translateReady == true && !forceGpuRestart)
                {
                    return;
                }

                if (!asrHoldsGpu)
                    await _engine.ReleaseGpuAsync(ct).ConfigureAwait(false);

                if (_disposed != 0) return;
                _llama ??= new LlamaServerProcess();
                _status(asrHoldsGpu
                    ? Loc.Get("Main.Status.StartingMt.GpuBusy")
                    : Loc.Get("Main.Status.PreparingMt"));
                await _llama.EnsureRunningAsync(
                        _settings,
                        _log,
                        ct,
                        PreferredGgufs,
                        preferCpu: asrHoldsGpu && !forceGpuRestart)
                    .ConfigureAwait(false);
                if (_disposed != 0) return;
                _settings.TranslateUrl = _llama.BaseUrl;
                _translateReady = true;
                _translateRunningOnCpu = asrHoldsGpu && !forceGpuRestart && _llama.Spawned;
                _log($"翻译模型就绪 · {_llama.BaseUrl}" + (_llama.ModelPath is null ? "" : " · " + Path.GetFileName(_llama.ModelPath)));
                PublishStatus(BuildStatusLine());
                MaybeOfferTranslateTargetEnTip();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_disposed != 0) return;
                _translateReady = false;
                _translateRunningOnCpu = false;
                PublishStatus(Loc.Get("Main.Status.MtStartFailed"), "翻译模型启动失败：" + ex.Message);
                FallbackDisplayToSource(Loc.Get("Main.Status.FallbackSource.Mt"));
            }
        }
        finally
        {
            try { _translateGate.Release(); } catch (ObjectDisposedException) { /* shutdown */ }
        }
    }

    private void PublishStatusWithCoverage(string line)
    {
        if (_coverage.EstimateSecondsToFirstCue() is int eta && CueCount == 0
            && !string.IsNullOrWhiteSpace(line)
            && !line.Contains("第一句", StringComparison.Ordinal)
            && !line.Contains("first cue", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("first subtitle", StringComparison.OrdinalIgnoreCase))
        {
            line += " · " + Loc.Format("Main.Status.FirstCueEtaSuffix", eta);
        }

        _status(line);
    }

    private void PublishStatus(string user, string? detail = null)
    {
        _status(user);
        if (!string.IsNullOrWhiteSpace(detail))
            _log(detail);
    }

    private void PublishStreamStatus(string user, string? detail = null)
    {
        _streamBaseStatus = user;
        if (!_streamBuffering)
            PublishStatus(user, detail);
    }

    private void OnMpvBufferingChanged(bool buffering, double percent)
    {
        if (!MediaSourceHelper.IsNonLocalMedia(MediaPath)) return;
        if (_mpv.IsRecording) return;

        var pct = (int)Math.Round(percent);
        if (_streamBuffering == buffering && (!_streamBuffering || pct == _streamBufferingPercent))
            return;

        _streamBuffering = buffering;
        _streamBufferingPercent = pct;
        if (buffering)
            PublishStatus(FormatStreamBufferingStatus(pct));
        else if (_streamBaseStatus is not null)
            PublishStatus(_streamBaseStatus);
    }

    private static string FormatStreamBufferingStatus(int percent)
        => percent is > 0 and < 100
            ? Loc.Format("Main.Status.StreamBuffering", percent)
            : Loc.Get("Main.Status.StreamBufferingIndeterminate");

    private void MarkPreviewFailed(string? message)
    {
        _previewRetryAvailable = true;
        EndBootstrap();
        var user = string.IsNullOrWhiteSpace(message)
            ? Loc.Get("Main.Status.PreviewFailed")
            : Loc.Format("Main.Status.PreviewFailedDetail", message);
        PublishStatus(user, message is null ? null : "原文提取启动失败：" + message);
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

        var report = PresetReadiness.AnalyzeDisk(
            _settings,
            MtRoute.WantsTranslation(ActiveMtRoute) && _settings.TranslateEnabled);
        if (!report.HasGaps)
        {
            ShowOsd(Loc.Get("Main.EnSource.InstallLater"), 2200);
            return;
        }

        if (OfferPresetSetupAsync is null) return;
        _ = ct;
        var setup = await OfferPresetSetupAsync(report).ConfigureAwait(false);
        if (setup is PresetSetupChoice.Cancel) return;
        var after = PresetReadiness.AnalyzeDisk(
            _settings,
            MtRoute.WantsTranslation(ActiveMtRoute) && _settings.TranslateEnabled);
        _pendingGapReport = after.HasGaps ? after : null;
        PacksChanged?.Invoke();
    }

    private void MaybeOfferQualityHandoffTip()
    {
        if (!UserTips.ShouldShow(_settings, UserTips.QualityHandoff)) return;
        if (!ShowPreviewChrome) return;
        if (CueCount < 1) return;
        UserTips.Dismiss(_settings, UserTips.QualityHandoff);
        PublishStatus(Loc.Get("Main.Status.QualityHandoff"));
    }

    private string BuildStatusLine(string? engineLabel = null)
    {
        _ = engineLabel;
        var mode = ModeUiLabel(_displayMode);
        if (_displayMode == SubtitleDisplayMode.Off)
            return UsingExistingSub
                ? Loc.Get("Main.Status.Build.OffExisting")
                : Loc.Get("Main.Status.Build.OffPreview");
        if (UsingExistingSub || IsLocalSubtitleSource)
            return UsingExistingSub
                ? Loc.Get("Main.Status.ExternalSub") + Loc.Get("Main.Status.ExternalSubHint")
                : Loc.Get("Main.SubSource.LocalOnlyNone");
        if (_retranslatingActive)
            return Loc.Format(
                "Main.Status.RetranslatingProgress",
                mode,
                TranslatedCount,
                CueCount,
                MediaTimeFormat.Format(SubFrontier));
        if (_coverage.EstimateSecondsToFirstCue() is int firstEta && CueCount == 0 && ShowPreviewChrome)
            return Loc.Format("Main.Status.Build.FirstCueEta", mode, firstEta);
        if (ActiveMtRoute.IsOff)
            return Loc.Format("Main.Status.Build.PreviewJa", mode);
        if (!_settings.TranslateEnabled
            && _displayMode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
            return Loc.Format("Main.Status.Build.TranslateOff", mode);
        if (WantsPreviewMt && _translateReady == false && TranslatedCount == 0)
        {
            var diskMtOk = ManagedLlmInstaller.HasLlamaRuntime(_settings)
                           && ManagedLlmInstaller.HasPreferredGguf(_settings);
            return diskMtOk
                ? Loc.Format("Main.Status.Build.MtFailed", mode)
                : Loc.Format("Main.Status.Build.MtNotReady", mode);
        }
        if (WantsPreviewMt && TranslatedCount == 0)
            return Loc.Format("Main.Status.Build.Generating", mode);
        if (WantsPreviewMt && (_translateReady == true || TranslatedCount > 0))
            return Loc.Format("Main.Status.Build.PreviewMt", mode, TranslatedCount, CueCount);
        if (WantsPreviewMt)
            return Loc.Get("Main.Status.Build.StartingMt");
        return Loc.Format("Main.Status.Build.SourceOnly", mode);
    }

    public string ModeTip(SubtitleDisplayMode mode)
    {
        if (!_settings.TranslateEnabled
            && MtRoute.WantsTranslation(ActiveMtRoute)
            && mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Dual)
        {
            return mode == SubtitleDisplayMode.Dual
                ? Loc.Get("Main.Mode.Dual.TranslateOff")
                : Loc.Get("Main.Mode.Zh.TranslateOff");
        }

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
            var diskMtOk = ManagedLlmInstaller.HasLlamaRuntime(_settings)
                           && ManagedLlmInstaller.HasPreferredGguf(_settings);
            if (diskMtOk)
            {
                return mode == SubtitleDisplayMode.Dual
                    ? Loc.Get("Main.Mode.Dual.Pending.MtFailed")
                    : Loc.Get("Main.Mode.Zh.Pending.MtFailed");
            }

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

    private void AnnounceAsrFallbackIfNeeded(string? preferred, string actual, RuntimePacks packs)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return;
        if (string.Equals(preferred, actual, StringComparison.OrdinalIgnoreCase)) return;

        var preferredName = AsrDisplayName(preferred);
        var actualName = AsrDisplayName(actual);
        _log($"ASR 回退 {preferred} → {actual}");

        // Open-path gap downgrade already explained missing components for this model.
        if (_pendingGapReport?.HasGaps == true
            && _pendingGapReport.Gaps.Any(g =>
                g.Kind is PresetGapKind.AsrModel
                && (string.IsNullOrWhiteSpace(g.Id)
                    || string.Equals(g.Id, preferred, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.Id, "gpu", StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        var osd = Loc.Format("Main.Osd.AsrFallback.NotInstalled", preferredName, actualName);
        var status = Loc.Format("Main.Status.AsrFallback.NotInstalled", preferredName, actualName);

        ShowOsd(osd, 3200);
        PublishStatus(status);
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
