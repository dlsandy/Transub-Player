using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

public sealed class AppSettings
{
    /// <summary>Optional Transub install folder for reusing MT / glossary / lexicons (not required).</summary>
    public string TransubInstallPath { get; set; } = "";
    /// <summary>Whisper.cpp backend: auto (Vulkan→CPU) | cpu | vulkan.</summary>
    public string AsrBackend { get; set; } = AsrBackends.Auto;
    /// <summary>ASR models root (contains <c>asr/</c>). Empty = <c>data/models</c>.</summary>
    public string ModelsPath { get; set; } = "";
    /// <summary>
    /// Translation runtime + GGUF root (contains <c>runtime/</c> and <c>models/</c>).
    /// Empty = <c>data/advanced-llm</c>.
    /// </summary>
    public string AdvancedLlmPath { get; set; } = "";
    public string HfEndpoint { get; set; } = HfEndpoints.Default();
    /// <summary>ASR preference: <c>auto</c> (default → turbo) | <c>whisper-large-v3-turbo</c>.</summary>
    public string AsrModel { get; set; } = ModelPicker.Auto;
    /// <summary>Legacy JSON only — superseded by <see cref="SourceLanguage"/>.</summary>
    public string Language { get; set; } = "auto";
    /// <summary>UI language: auto (OS) | zh-Hans | en | ja | ko | … See <c>Localization.UiLanguages</c>.</summary>
    public string UiLanguage { get; set; } = "auto";
    /// <summary>Legacy JSON only — superseded by <see cref="SourceLanguage"/>; kept so old settings round-trip.</summary>
    public string PresetId { get; set; } = "auto-speed";
    /// <summary>ASR source language: auto | ja | ko | en | zh. Filename may guess when auto.</summary>
    public string SourceLanguage { get; set; } = SourceLanguages.Auto;
    /// <summary>Legacy JSON only — always normalized to turbo tier (<see cref="AsrQualities.Better"/>).</summary>
    public string AsrQuality { get; set; } = AsrQualities.Better;
    /// <summary>zh | src | dual — default zh so users see translation first.</summary>
    public string SubtitleMode { get; set; } = "zh";
    /// <summary>Incremental preview MT via local translation model. Default on; falls back to source if llama-server is down.</summary>
    public bool TranslateEnabled { get; set; } = true;
    /// <summary>Preview translation language: zh (default) | en | ja | ko.</summary>
    public string TranslateTarget { get; set; } = TranslateTargets.Zh;
    /// <summary>
    /// Local MT GGUF preference: <c>translategemma-4b-q4</c> (default).
    /// </summary>
    public string TranslateModelId { get; set; } = TranslateModels.TranslateGemma4B;
    public string TranslateUrl { get; set; } = "http://127.0.0.1:39281";
    public bool AutoStartPreview { get; set; } = true;
    /// <summary>Pause after open until translated subtitle frontier covers WaitForZhMinutes.</summary>
    public bool WaitForFirstZhBeforePlay { get; set; }
    /// <summary>When true, skip the open-path wait and play immediately (source may show while MT catches up).</summary>
    public bool PlayImmediatelyOnOpen { get; set; }
    /// <summary>Minutes of translated subtitle timeline required before auto-play (0 = first batch).</summary>
    public double WaitForZhMinutes { get; set; } = 1;
    /// <summary>When a sidecar SRT/ASS exists, load it instead of starting preview ASR.</summary>
    public bool PreferExternalSubtitle { get; set; } = true;
    /// <summary>When no local subtitle exists, search subtitlecat.com and let the user pick a download.</summary>
    public bool FetchSubtitleFromSubtitleCat { get; set; }
    /// <summary>
    /// Apply Transub-style preview cleaning: JA ASR domain lexicon, name-loop strip,
    /// soft-voice hallucination collapse, light post-MT sanitize, and optional glossary unify. Default on.
    /// </summary>
    public bool TextSanitizeEnabled { get; set; } = true;
    /// <summary>Optional glossary JSON (Transub editor format). Empty = auto-detect data/glossary.json or Transub install.</summary>
    public string GlossaryPath { get; set; } = "";
    public int Volume { get; set; } = 50;
    public double Speed { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; }
    /// <summary>When true, live window resize keeps video-area aspect (or current window aspect if no video).</summary>
    public bool LockWindowAspectRatio { get; set; }

    // —— 常规（PotPlayer 风格）——
    public bool RememberWindowBounds { get; set; } = true;
    /// <summary>null = never saved; avoids NaN which System.Text.Json cannot write by default.</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 720;
    public bool HideChromeInFullscreen { get; set; } = true;
    public double FullscreenHideDelaySec { get; set; } = 2.2;
    public string ScreenshotDir { get; set; } = "";
    /// <summary>When false, newly opened files are not added to <see cref="RecentFiles"/>.</summary>
    public bool RememberRecentFiles { get; set; } = true;
    public int RecentFilesMax { get; set; } = 12;
    public List<string> RecentFiles { get; set; } = [];
    /// <summary>Saved live / stream source URLs (page or stream address, not ephemeral HLS).</summary>
    public List<LiveFavoriteEntry> LiveFavorites { get; set; } = [];
    /// <summary>One-time tip ids the user has dismissed (see <c>UserTips</c>).</summary>
    public List<string> DismissedTips { get; set; } = [];
    /// <summary>In fullscreen, space out lag OSD / action bar so they interrupt less.</summary>
    public bool FullscreenQuietOsd { get; set; } = true;
    /// <summary>Extensions registered as default open-with handlers (lowercase, with dot).</summary>
    public List<string> AssociatedExtensions { get; set; } = [];

    // —— 播放 ——
    public bool AutoPlayOnOpen { get; set; } = true;
    public bool AutoPlayNext { get; set; } = true;
    /// <summary>After current media ends, pre-generate ASR/MT for remaining playlist items (default on).</summary>
    public bool PrefetchPlaylistSubtitles { get; set; } = true;
    /// <summary>When opening a single media file, queue other video/audio files in the same folder.</summary>
    public bool AddSameFolderToPlaylist { get; set; } = true;
    public bool RememberPlaybackPosition { get; set; } = true;
    public int SeekStepSeconds { get; set; } = 5;
    public int SeekStepFineSeconds { get; set; } = 1;
    public int SeekStepLargeSeconds { get; set; } = 30;
    /// <summary>auto | no | d3d11va | dxva2 | nvdec</summary>
    public string HwDec { get; set; } = "auto";
    /// <summary>window | contain | cover | stretch — default window：打开后按视频比例调整窗口</summary>
    public string VideoFit { get; set; } = "window";

    // —— 字幕渲染（mpv）——
    public string SubFont { get; set; } = "Microsoft YaHei";
    public int SubFontSize { get; set; } = 42;
    public bool SubBold { get; set; } = true;
    public int SubBorderSize { get; set; } = 2;
    public int SubMarginY { get; set; } = 36;
    /// <summary>Positive = subtitles appear later (mpv sub-delay).</summary>
    public double SubDelaySec { get; set; }
    /// <summary>Kept in sync with <see cref="SubtitleMode"/> ≠ off. Visibility is owned by display mode.</summary>
    public bool SubVisibleOnStart { get; set; } = true;
    /// <summary>Control-bar subtitle source: off | online | local | live. Default live = auto preview path.</summary>
    public string SubtitleSource { get; set; } = "live";

    /// <summary>Update mirror: auto (region) | github | gitcode.</summary>
    public string UpdateSource { get; set; } = AppUpdateEndpoints.Auto;
    /// <summary>When true, quietly check for a newer portable build about once per day.</summary>
    public bool CheckUpdatesOnStartup { get; set; }
    /// <summary>ISO-8601 UTC of last successful or attempted update check.</summary>
    public string LastUpdateCheckUtc { get; set; } = "";

    public string ResolveScreenshotDir()
    {
        if (!string.IsNullOrWhiteSpace(ScreenshotDir))
        {
            try
            {
                Directory.CreateDirectory(ScreenshotDir);
                return ScreenshotDir;
            }
            catch
            {
                // fall through
            }
        }

        var dir = AppPaths.ScreenshotsDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AppSettings? _session;

    public static AppSettings Load()
    {
        if (_session is not null)
            return _session;

        _session = LoadFromDisk();
        return _session;
    }

    private static AppSettings LoadFromDisk()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsPath))
            {
                var json = File.ReadAllText(AppPaths.SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                MigrateLegacy(json, settings);
                return settings;
            }
        }
        catch
        {
            // keep defaults
        }

        return FreshDefaults();
    }

    /// <summary>First-run defaults: UI follows OS; HF mirror follows region; MT target follows UI.</summary>
    private static AppSettings FreshDefaults()
    {
        var settings = new AppSettings
        {
            UiLanguage = UiLanguages.Auto,
            HfEndpoint = HfEndpoints.Default(),
        };
        settings.TranslateTarget = TranslateTargets.FromUiLanguage(settings.UiLanguage);
        return settings;
    }

    /// <summary>Deep copy via JSON — used so Settings can edit a draft without mutating live state on Cancel.</summary>
    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    /// <summary>Replace all persisted fields from <paramref name="src"/> (keeps this instance identity).</summary>
    public void CopyFrom(AppSettings src)
    {
        if (src is null || ReferenceEquals(src, this)) return;
        var json = JsonSerializer.Serialize(src, JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        if (copy is null) return;

        TransubInstallPath = copy.TransubInstallPath;
        ModelsPath = copy.ModelsPath;
        AdvancedLlmPath = copy.AdvancedLlmPath;
        AsrBackend = AsrBackends.Normalize(copy.AsrBackend);
        HfEndpoint = copy.HfEndpoint;
        AsrModel = ModelPicker.Normalize(copy.AsrModel);
        Language = copy.Language;
        UiLanguage = copy.UiLanguage;
        PresetId = copy.PresetId;
        SourceLanguage = copy.SourceLanguage;
        AsrQuality = AsrQualities.Normalize(copy.AsrQuality);
        SubtitleMode = copy.SubtitleMode;
        TranslateEnabled = copy.TranslateEnabled;
        TranslateModelId = copy.TranslateModelId;
        TranslateTarget = copy.TranslateTarget;
        TranslateUrl = copy.TranslateUrl;
        AutoStartPreview = copy.AutoStartPreview;
        WaitForFirstZhBeforePlay = copy.WaitForFirstZhBeforePlay;
        PlayImmediatelyOnOpen = copy.PlayImmediatelyOnOpen;
        WaitForZhMinutes = copy.WaitForZhMinutes;
        PreferExternalSubtitle = copy.PreferExternalSubtitle;
        FetchSubtitleFromSubtitleCat = copy.FetchSubtitleFromSubtitleCat;
        TextSanitizeEnabled = copy.TextSanitizeEnabled;
        GlossaryPath = copy.GlossaryPath;
        Volume = copy.Volume;
        Speed = copy.Speed;
        AlwaysOnTop = copy.AlwaysOnTop;
        LockWindowAspectRatio = copy.LockWindowAspectRatio;
        RememberWindowBounds = copy.RememberWindowBounds;
        WindowLeft = copy.WindowLeft;
        WindowTop = copy.WindowTop;
        WindowWidth = copy.WindowWidth;
        WindowHeight = copy.WindowHeight;
        HideChromeInFullscreen = copy.HideChromeInFullscreen;
        FullscreenHideDelaySec = copy.FullscreenHideDelaySec;
        ScreenshotDir = copy.ScreenshotDir;
        RememberRecentFiles = copy.RememberRecentFiles;
        RecentFilesMax = copy.RecentFilesMax;
        RecentFiles = copy.RecentFiles;
        LiveFavorites = copy.LiveFavorites;
        DismissedTips = copy.DismissedTips;
        FullscreenQuietOsd = copy.FullscreenQuietOsd;
        AssociatedExtensions = copy.AssociatedExtensions;
        AutoPlayOnOpen = copy.AutoPlayOnOpen;
        AutoPlayNext = copy.AutoPlayNext;
        PrefetchPlaylistSubtitles = copy.PrefetchPlaylistSubtitles;
        AddSameFolderToPlaylist = copy.AddSameFolderToPlaylist;
        RememberPlaybackPosition = copy.RememberPlaybackPosition;
        SeekStepSeconds = copy.SeekStepSeconds;
        SeekStepFineSeconds = copy.SeekStepFineSeconds;
        SeekStepLargeSeconds = copy.SeekStepLargeSeconds;
        HwDec = copy.HwDec;
        VideoFit = copy.VideoFit;
        SubFont = copy.SubFont;
        SubFontSize = copy.SubFontSize;
        SubBold = copy.SubBold;
        SubBorderSize = copy.SubBorderSize;
        SubMarginY = copy.SubMarginY;
        SubDelaySec = copy.SubDelaySec;
        SubVisibleOnStart = copy.SubVisibleOnStart;
        SubtitleSource = copy.SubtitleSource;
        UpdateSource = AppUpdateEndpoints.Normalize(copy.UpdateSource);
        CheckUpdatesOnStartup = copy.CheckUpdatesOnStartup;
        LastUpdateCheckUtc = copy.LastUpdateCheckUtc;
    }

    /// <summary>Map old <c>sakuraEnabled</c> / <c>sakuraUrl</c> when new keys are absent.</summary>
    private static void MigrateLegacy(string json, AppSettings settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("translateEnabled", out _)
                && root.TryGetProperty("sakuraEnabled", out var legacyEnabled)
                && legacyEnabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.TranslateEnabled = legacyEnabled.GetBoolean();
            }

            if (!root.TryGetProperty("translateUrl", out _)
                && root.TryGetProperty("sakuraUrl", out var legacyUrl)
                && legacyUrl.ValueKind == JsonValueKind.String)
            {
                var url = legacyUrl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    settings.TranslateUrl = url;
            }

            if (!root.TryGetProperty("addSameFolderToPlaylist", out _))
                settings.AddSameFolderToPlaylist = true;

            if (!root.TryGetProperty("sourceLanguage", out _)
                && root.TryGetProperty("presetId", out var presetEl)
                && presetEl.ValueKind == JsonValueKind.String)
            {
                settings.SourceLanguage = SourceLanguageFromPresetId(presetEl.GetString());
            }

            settings.SourceLanguage = SourceLanguages.Normalize(settings.SourceLanguage);
            settings.AsrQuality = AsrQualities.Normalize(settings.AsrQuality);
            settings.AsrModel = ModelPicker.Normalize(settings.AsrModel);
            settings.TranslateModelId = TranslateModels.Normalize(settings.TranslateModelId);
            if (!root.TryGetProperty("translateTarget", out _))
                settings.TranslateTarget = TranslateTargets.FromUiLanguage(settings.UiLanguage);
            else
                settings.TranslateTarget = TranslateTargets.Normalize(settings.TranslateTarget);

            if (!root.TryGetProperty("transubInstallPath", out _))
            {
                if (root.TryGetProperty("engineInstallPath", out var legacyInstall)
                    && legacyInstall.ValueKind == JsonValueKind.String)
                {
                    var path = legacyInstall.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                        settings.TransubInstallPath = path;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string SourceLanguageFromPresetId(string? presetId)
    {
        var id = (presetId ?? "").Trim().ToLowerInvariant();
        if (id.StartsWith("ja-", StringComparison.Ordinal) || id is "game-gal")
            return SourceLanguages.Ja;
        if (id.StartsWith("k-", StringComparison.Ordinal))
            return SourceLanguages.Ko;
        if (id.StartsWith("en-", StringComparison.Ordinal))
            return SourceLanguages.En;
        if (id.StartsWith("zh-", StringComparison.Ordinal))
            return SourceLanguages.Zh;
        return SourceLanguages.Auto;
    }

    private static readonly object DebounceGate = new();
    private static readonly object WriteGate = new();
    private static Timer? _debounceTimer;
    private static AppSettings? _debounceTarget;

    public void Save()
    {
        lock (DebounceGate)
        {
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _debounceTarget = null;
        }

        WriteToDisk();
    }

    /// <summary>Coalesce rapid volume/speed/mode writes (hot playback paths).</summary>
    public void SaveSoon(int delayMs = 500)
    {
        delayMs = Math.Clamp(delayMs, 100, 5000);
        lock (DebounceGate)
        {
            _debounceTarget = this;
            _debounceTimer ??= new Timer(static _ =>
            {
                AppSettings? target;
                lock (DebounceGate)
                {
                    target = _debounceTarget;
                    _debounceTarget = null;
                }

                try { target?.WriteToDisk(); }
                catch { /* ignore */ }
            });
            _debounceTimer.Change(delayMs, Timeout.Infinite);
        }
    }

    private void WriteToDisk()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsPath)!);
        var path = AppPaths.SettingsPath;
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmp = path + ".tmp";
        lock (WriteGate)
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
    }
}
