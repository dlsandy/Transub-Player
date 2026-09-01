using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal sealed class MpvPlayer : IDisposable
{
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCts;
    private string _pipeName = "";
    private int _requestId;
    private string? _currentSub;
    private int _volume = 50;
    private bool _muted;
    private double _speed = 1.0;
    private bool _subVisible = true;
    private int _videoWidth;
    private int _videoHeight;
    private DateTime _lastEventUtc = DateTime.UtcNow;
    private volatile bool _ipcFailed;
    private bool _liveLightIpc;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingRequests = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<double?>> _pendingDoubles = new();

    private const int IpcWriteTimeoutMs = 2500;
    private const int IpcPingTimeoutMs = 2000;

    public event Action<double>? TimeChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<int>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<double>? SpeedChanged;
    public event Action<bool>? EofReached;
    public event Action<int, int>? VideoSizeChanged;
    public event Action<bool, double>? BufferingChanged;
    public event Action<string>? Log;

    public bool IsRunning => _process is { HasExited: false };
    public bool IpcFailed => _ipcFailed;
    public int Volume => _volume;
    public bool Muted => _muted;
    public double Speed => _speed;
    public bool SubVisible => _subVisible;
    public bool IsBuffering => _pausedForCache || _demuxerUnderrun;
    public double BufferingPercent => _cacheBufferingPercent;

    private bool _pausedForCache;
    private double _cacheBufferingPercent;
    private bool _demuxerUnderrun;

    public void Start(string mpvPath, IntPtr hwnd, AppSettings? settings = null)
        => StartAsync(mpvPath, hwnd, settings).GetAwaiter().GetResult();

    /// <param name="initialMedia">
    /// When set (Covers Download style), pass URL + HTTP headers on the mpv command line at spawn
    /// instead of idle + IPC loadfile — required for sacdnssedge HLS with Referer.
    /// </param>
    public async Task StartAsync(
        string mpvPath,
        IntPtr hwnd,
        AppSettings? settings = null,
        CancellationToken ct = default,
        string? initialMedia = null,
        IReadOnlyDictionary<string, string>? httpHeaders = null,
        bool autoPlay = true)
    {
        Stop();
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("播放窗口尚未就绪。");

        settings ??= new AppSettings();
        _pipeName = "transub-player-mpv-" + Guid.NewGuid().ToString("N")[..12];
        var ipc = @"\\.\pipe\" + _pipeName;
        var start = new ProcessStartInfo
        {
            FileName = mpvPath,
            WorkingDirectory = Path.GetDirectoryName(mpvPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // Never redirect stdout without a reader — full pipe buffer stalls mpv (~1–2 min).
            // Covers / standalone: stdout ignored. Keep stderr only for diagnostics.
            RedirectStandardError = true,
            RedirectStandardOutput = false,
        };

        var hasInitial = !string.IsNullOrWhiteSpace(initialMedia);
        var isLiveHls = hasInitial
                        && (initialMedia!.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                            || initialMedia.Contains("sacdnssedge", StringComparison.OrdinalIgnoreCase)
                            || initialMedia.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase));

        _liveLightIpc = isLiveHls;

        // Live: same argv style as opening mpv.exe directly / Covers embed (URL on command line).
        foreach (var arg in isLiveHls
                     ? BuildLiveEmbedArgs(hwnd, ipc, settings)
                     : BuildStartupArgs(hwnd, ipc, settings, idle: true, liveHls: false))
            start.ArgumentList.Add(arg);

        if (httpHeaders is not null && httpHeaders.Count > 0)
        {
            foreach (var arg in BuildHttpHeaderArgs(httpHeaders))
                start.ArgumentList.Add(arg);
        }

        if (hasInitial && !autoPlay)
            start.ArgumentList.Add("--pause");

        if (hasInitial)
            start.ArgumentList.Add(initialMedia!);

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data);
        };
        _process.Exited += (_, _) => Log?.Invoke("mpv 已退出");
        if (!_process.Start())
            throw new InvalidOperationException("无法启动 mpv。");
        ChildProcessLifetime.Track(_process);
        _process.BeginErrorReadLine();
        try
        {
            await ConnectPipeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            Stop();
            throw;
        }

        if (isLiveHls)
        {
            // Covers MVP: short command IPC only — never observe_property (event flood stalls demuxer).
            // UI polls time-pos via GetDoubleAsync; pause/volume tracked locally in PreviewController.
        }
        else
        {
            Observe("time-pos", 1);
            Observe("duration", 2);
            Observe("pause", 3);
            Observe("volume", 4);
            Observe("mute", 5);
            Observe("speed", 6);
            Observe("eof-reached", 7);
            Observe("width", 8);
            Observe("height", 9);
            Observe("paused-for-cache", 10);
            Observe("cache-buffering-state", 11);
            Command("set_property", "pause", true);
        }
    }

    public bool LoadFile(string mediaPath, bool autoPlay = true, IReadOnlyDictionary<string, string>? httpHeaders = null, string? osdText = null)
    {
        _currentSub = null;
        _videoWidth = 0;
        _videoHeight = 0;
        ResetBufferingState();
        SetHttpHeaders(httpHeaders);
        if (MediaSourceHelper.IsNonLocalMedia(mediaPath))
            ApplyStreamDemuxerOptions(mediaPath);
        else
            ApplyLocalPlaybackOptions(mediaPath);
        if (!TryCommand("loadfile", mediaPath, "replace"))
        {
            if (_process is { HasExited: true })
                Log?.Invoke("mpv 在 loadfile 时已退出");
            return false;
        }

        SetPause(!autoPlay);
        ShowOsd(osdText ?? MediaSourceHelper.DisplayName(mediaPath));
        return true;
    }

    /// <summary>Covers Download: --referrer + http-header-fields (Referer + Origin).</summary>
    private static IEnumerable<string> BuildHttpHeaderArgs(IReadOnlyDictionary<string, string> headers)
    {
        var list = new List<string>();
        if (headers.TryGetValue("Referer", out var referer) && !string.IsNullOrWhiteSpace(referer))
        {
            var r = referer.Trim();
            list.Add("--referrer=" + r);
            try
            {
                var origin = new Uri(r).GetLeftPart(UriPartial.Authority);
                list.Add("--http-header-fields=Referer: " + r + "\r\nOrigin: " + origin + "\r\n");
            }
            catch
            {
                list.Add("--http-header-fields=Referer: " + r);
            }
        }

        if (headers.TryGetValue("User-Agent", out var ua) && !string.IsNullOrWhiteSpace(ua))
            list.Add("--user-agent=" + ua.Trim());

        return list;
    }

    /// <summary>Live HLS: Covers Download parity (cache + 60s readahead; no lavf overrides).</summary>
    private void ApplyStreamDemuxerOptions(string mediaPath)
    {
        if (!MediaSourceHelper.IsRemoteUrl(mediaPath) && !MediaSourceHelper.IsScreenCapture(mediaPath))
            return;

        try
        {
            var isHls = mediaPath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                        || mediaPath.Contains("sacdnssedge", StringComparison.OrdinalIgnoreCase)
                        || mediaPath.Contains("doppiocdn", StringComparison.OrdinalIgnoreCase);
            if (isHls)
            {
                SetProperty("cache", "yes");
                SetProperty("demuxer-readahead-secs", 60);
                SetProperty("hr-seek", "no");
                SetProperty("cache-pause", "no");
            }
            else
            {
                SetProperty("demuxer-lavf-o", "reconnect=1,reconnect_streamed=1,reconnect_delay_max=5");
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Undo live-stream demuxer tweaks so local files seek normally after a prior stream session.
    /// </summary>
    private void ApplyLocalPlaybackOptions(string mediaPath)
    {
        try
        {
            SetProperty("hr-seek", "default");
            SetProperty("demuxer-readahead-secs", 1);
            SetProperty("cache-pause", "auto");
            SetProperty("demuxer-lavf-o", "");
            SetProperty("demuxer-lavf-analyzeduration", 0);
            SetProperty("demuxer-lavf-probesize", 0);
        }
        catch { /* ignore */ }

        ApplyLocalTsDemuxerOptions(mediaPath);
    }

    /// <summary>
    /// Raw live-record MPEG-TS often needs genpts; PotPlayer is lenient, stock mpv is not.
    /// </summary>
    private void ApplyLocalTsDemuxerOptions(string mediaPath)
    {
        if (MediaSourceHelper.IsRemoteUrl(mediaPath) || MediaSourceHelper.IsScreenCapture(mediaPath))
            return;
        if (!StreamRecord.IsMpegTsPath(mediaPath))
            return;

        try
        {
            SetProperty("demuxer-lavf-o", "fflags=+genpts+igndts+discardcorrupt");
            SetProperty("demuxer-lavf-analyzeduration", 10_000_000);
            SetProperty("demuxer-lavf-probesize", 10_000_000);
        }
        catch { /* ignore */ }
    }

    public void SetHttpHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            SetProperty("http-header-fields", Array.Empty<string>());
            SetProperty("referrer", "");
            return;
        }

        var fields = headers
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => $"{kv.Key}: {kv.Value.Trim()}")
            .ToArray();
        SetProperty("http-header-fields", fields);
        if (headers.TryGetValue("Referer", out var referer) && !string.IsNullOrWhiteSpace(referer))
            SetProperty("referrer", referer.Trim());
    }

    public void SetPause(bool pause) => Command("set_property", "pause", pause);

    public void TogglePause() => Command("cycle", "pause");

    public void Seek(double seconds)
        => Command("set_property", "time-pos", seconds);

    public void SeekRelative(double delta) => Command("seek", delta, "relative");

    public void SeekPercent(double percent) => Command("seek", Math.Clamp(percent, 0, 100), "absolute-percent");

    public void StopPlayback() => Command("stop");

    public void FrameStep(bool forward = true)
        => Command(forward ? "frame-step" : "frame-back-step");

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 130);
        Command("set_property", "volume", _volume);
        if (_muted && _volume > 0)
            SetMute(false);
        VolumeChanged?.Invoke(_volume);
    }

    public void AdjustVolume(int delta) => SetVolume(_volume + delta);

    public void SetMute(bool mute)
    {
        _muted = mute;
        Command("set_property", "mute", mute);
        MuteChanged?.Invoke(mute);
    }

    public void ToggleMute() => SetMute(!_muted);

    public void SetSpeed(double speed)
    {
        _speed = Math.Clamp(Math.Round(speed, 2), 0.25, 4.0);
        Command("set_property", "speed", _speed);
        SpeedChanged?.Invoke(_speed);
        ShowOsd($"{_speed:0.##}x");
    }

    public void CycleSpeed()
    {
        double[] steps = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];
        var i = Array.FindIndex(steps, s => Math.Abs(s - _speed) < 0.01);
        SetSpeed(steps[(i + 1) % steps.Length]);
    }

    public void ResetSpeed() => SetSpeed(1.0);

    public void ClearSubtitle()
    {
        Command("sub-remove");
        _currentSub = null;
    }

    /// <param name="reloadIfSame">
    /// When the path is already selected, whether to <c>sub-reload</c>.
    /// Live preview writes often leave the on-screen cue unchanged; callers should pass
    /// <c>false</c> and schedule a deferred reload to avoid flicker.
    /// </param>
    public void SetSubtitle(string? path, bool reloadIfSame = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        if (string.Equals(_currentSub, path, StringComparison.OrdinalIgnoreCase))
        {
            if (reloadIfSame)
                ReloadSubtitle();
            return;
        }

        Command("sub-remove");
        Command("sub-add", path, "select");
        _currentSub = path;
        // Keep current visibility — display mode / user toggle owns show vs hide.
    }

    public void ReloadSubtitle()
    {
        if (string.IsNullOrWhiteSpace(_currentSub)) return;
        Command("sub-reload");
    }

    public void SetSubVisible(bool visible, bool showOsd = true)
    {
        _subVisible = visible;
        Command("set_property", "sub-visibility", visible ? "yes" : "no");
        if (showOsd)
            ShowOsd(visible ? Loc.Get("Main.Osd.SubVisibleOn") : Loc.Get("Main.Osd.SubVisibleOff"));
    }

    public void ToggleSubVisible() => SetSubVisible(!_subVisible);

    public string? RecordingPath { get; private set; }
    public string? RecordingFinalPath { get; private set; }
    public DateTime? RecordingStartedUtc { get; private set; }

    public bool IsRecording => !string.IsNullOrWhiteSpace(RecordingPath);

    public TimeSpan RecordingElapsed
        => RecordingStartedUtc is { } start
            ? DateTime.UtcNow - start
            : TimeSpan.Zero;

    public async Task StartStreamRecordAsync(string outputPath, CancellationToken ct = default)
    {
        if (!IsRunning)
            throw new InvalidOperationException("mpv 未运行。");
        if (IsRecording)
            throw new InvalidOperationException("已在录制中。");

        var finalPath = StreamRecord.EnsureOutputExtension(outputPath);
        // Always dump to a temp .ts — stream-record raw output is rarely playable until remux/repair.
        var recordPath = StreamRecord.TempTsPath(finalPath);

        var dir = Path.GetDirectoryName(recordPath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        StreamRecord.TryDelete(recordPath);
        StreamRecord.TryDelete(finalPath);

        SetProperty("stream-record", recordPath);
        RecordingPath = recordPath;
        RecordingFinalPath = finalPath;
        RecordingStartedUtc = DateTime.UtcNow;

        for (var i = 0; i < 25; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct).ConfigureAwait(false);
            if (!IsRunning)
            {
                ClearRecordingState();
                throw new InvalidOperationException("mpv 已退出。");
            }

            if (File.Exists(recordPath) && new FileInfo(recordPath).Length > 1024)
            {
                Log?.Invoke("录制 " + finalPath);
                return;
            }
        }

        await StopStreamRecordCoreAsync().ConfigureAwait(false);
        StreamRecord.TryDelete(recordPath);
        throw new InvalidOperationException("未能开始录制，请确认直播仍在线后重试。");
    }

    public async Task<StreamRecordStopResult> StopStreamRecordAsync(CancellationToken ct = default)
    {
        var recordPath = RecordingPath;
        var finalPath = RecordingFinalPath ?? recordPath;
        if (string.IsNullOrWhiteSpace(recordPath) || string.IsNullOrWhiteSpace(finalPath))
            return new StreamRecordStopResult(false, "", 0, "当前没有在录制。");

        await StopStreamRecordCoreAsync().ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);

        if (!File.Exists(recordPath))
            return new StreamRecordStopResult(false, finalPath, 0, "录制文件不存在。");

        var size = new FileInfo(recordPath).Length;
        if (size <= 0)
        {
            await StreamRecord.TryDeleteAsync(recordPath).ConfigureAwait(false);
            return new StreamRecordStopResult(false, finalPath, 0, "录制文件为空。");
        }

        var finalized = await StreamRecord.FinalizeAsync(recordPath, finalPath, ct).ConfigureAwait(false);
        if (finalized.Ok)
            Log?.Invoke($"录制已保存 {finalized.Path} ({finalized.SizeBytes} B)");
        // Ensure temp dump is gone even if Finalize already tried (file locks).
        if (!string.Equals(recordPath, finalized.Path, StringComparison.OrdinalIgnoreCase))
            await StreamRecord.TryDeleteAsync(recordPath).ConfigureAwait(false);
        return finalized;
    }

    private async Task StopStreamRecordCoreAsync()
    {
        ClearRecordingState();
        try { SetProperty("stream-record", ""); } catch { /* ignore */ }
        await Task.CompletedTask;
    }

    private void ClearRecordingState()
    {
        RecordingPath = null;
        RecordingFinalPath = null;
        RecordingStartedUtc = null;
    }

    public string? Screenshot(string? directory = null)
    {
        var dir = directory ?? AppPaths.ScreenshotsDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        Command("screenshot-to-file", file, "video");
        Log?.Invoke("截图 " + file);
        return file;
    }

    public void SetSubDelay(double seconds)
        => SetProperty("sub-delay", Math.Clamp(seconds, -30, 30));

    public void ApplySubtitleSettings(AppSettings settings)
    {
        var font = string.IsNullOrWhiteSpace(settings.SubFont) ? "Microsoft YaHei" : settings.SubFont.Trim();
        var size = Math.Clamp(settings.SubFontSize, 16, 96);
        var border = Math.Clamp(settings.SubBorderSize, 0, 8);
        var margin = Math.Clamp(settings.SubMarginY, 0, 200);
        SetProperty("sub-font", font);
        SetProperty("sub-font-size", size);
        SetProperty("sub-bold", settings.SubBold);
        SetProperty("sub-border-size", border);
        SetProperty("sub-margin-y", margin);
        SetSubDelay(settings.SubDelaySec);
        SetProperty("osd-font", font);
        // Visibility is owned by PreviewController display mode — do not apply SubVisibleOnStart here.
        ReloadSubtitle();
    }

    public void ApplyPlaybackSettings(AppSettings settings)
    {
        var hw = NormalizeHwDec(settings.HwDec);
        SetProperty("hwdec", hw);
        ApplyVideoFit(settings.VideoFit);
        SetVolume(Math.Clamp(settings.Volume, 0, 130));
        if (settings.Speed > 0)
            SetSpeed(settings.Speed);
    }

    /// <summary>
    /// Live embed argv — mirror standalone <c>mpv --referrer=… URL</c> + Covers embed essentials only.
    /// No idle / network-timeout / log-file / lavf-o (those diverge from plain mpv and caused stalls).
    /// </summary>
    private static IEnumerable<string> BuildLiveEmbedArgs(IntPtr hwnd, string ipc, AppSettings settings)
    {
        // Exact Covers Download electron/mpv-launch.js buildMpvArgs (minus video-margin for HTML chrome).
        var hw = NormalizeHwDec(settings.HwDec);
        if (hw == "auto")
            hw = "d3d11va-auto";

        yield return "--no-terminal";
        yield return "--force-window=immediate";
        yield return "--keep-open=yes";
        yield return "--osc=no";
        yield return "--osd-bar=no";
        yield return "--input-default-bindings=no";
        yield return "--input-vo-keyboard=no";
        yield return "--border=no";
        yield return "--title=";
        yield return "--cache=yes";
        yield return "--demuxer-readahead-secs=60";
        yield return "--hr-seek=no";
        yield return "--vo=gpu";
        yield return "--gpu-context=d3d11";
        yield return $"--hwdec={hw}";
        yield return $"--wid={hwnd.ToInt64()}";
        yield return $"--input-ipc-server={ipc}";
        yield return $"--volume={Math.Clamp(settings.Volume, 0, 130)}";
    }

    private static IEnumerable<string> BuildStartupArgs(
        IntPtr hwnd,
        string ipc,
        AppSettings settings,
        bool idle = true,
        bool liveHls = false)
    {
        var font = string.IsNullOrWhiteSpace(settings.SubFont) ? "Microsoft YaHei" : settings.SubFont.Trim();
        var size = Math.Clamp(settings.SubFontSize, 16, 96);
        var border = Math.Clamp(settings.SubBorderSize, 0, 8);
        var margin = Math.Clamp(settings.SubMarginY, 0, 200);
        var hw = NormalizeHwDec(settings.HwDec);
        yield return $"--wid={hwnd.ToInt64()}";
        yield return "--no-border";
        yield return "--no-osc";
        yield return "--no-input-default-bindings";
        yield return "--input-vo-keyboard=no";
        yield return "--keep-open=yes";
        if (idle)
            yield return "--idle=yes";
        yield return "--force-window=immediate";
        yield return "--cache=yes";
        yield return $"--hwdec={hw}";
        yield return "--vo=gpu";
        yield return "--gpu-context=d3d11";
        yield return "--ytdl=no";
        yield return $"--volume={Math.Clamp(settings.Volume, 0, 130)}";
        yield return "--sub-auto=exact";
        yield return "--sub-codepage=utf-8";
        yield return $"--sub-font={font}";
        yield return $"--sub-font-size={size}";
        yield return settings.SubBold ? "--sub-bold=yes" : "--sub-bold=no";
        yield return $"--sub-border-size={border}";
        yield return $"--sub-margin-y={margin}";
        yield return $"--sub-delay={settings.SubDelaySec}";
        yield return "--sub-color=1/1/1/1";
        yield return $"--osd-font={font}";
        yield return "--osd-font-size=32";
        yield return "--osd-level=1";
        yield return "--msg-level=all=warn";
        yield return "--network-timeout=15";
        yield return $"--input-ipc-server={ipc}";
        foreach (var fitArg in VideoFitArgs(settings.VideoFit))
            yield return fitArg;
    }

    private void ApplyVideoFit(string? fit)
    {
        switch ((fit ?? "window").Trim().ToLowerInvariant())
        {
            case "cover":
                SetProperty("keepaspect", true);
                SetProperty("panscan", 1.0);
                break;
            case "stretch":
                SetProperty("keepaspect", false);
                SetProperty("panscan", 0.0);
                break;
            default:
                // window / contain：画面保持比例；window 另由主窗口按视频尺寸调整
                SetProperty("keepaspect", true);
                SetProperty("panscan", 0.0);
                break;
        }
    }

    private static IEnumerable<string> VideoFitArgs(string? fit)
    {
        switch ((fit ?? "window").Trim().ToLowerInvariant())
        {
            case "cover":
                yield return "--keepaspect=yes";
                yield return "--panscan=1.0";
                break;
            case "stretch":
                yield return "--keepaspect=no";
                yield return "--panscan=0";
                break;
            default:
                yield return "--keepaspect=yes";
                yield return "--panscan=0";
                break;
        }
    }

    private static string NormalizeHwDec(string? raw)
    {
        var key = (raw ?? "auto").Trim().ToLowerInvariant();
        return key switch
        {
            "no" or "off" or "关闭" => "no",
            "d3d11va" => "d3d11va",
            "dxva2" => "dxva2",
            "nvdec" or "cuda" => "nvdec",
            _ => "auto",
        };
    }

    private void SetProperty(string name, object value)
        => TryCommand("set_property", name, value);

    public void ShowOsd(string text, int durationMs = 1200)
        => TryCommand("show-text", text, durationMs);

    /// <summary>Low-rate property read (live mode polls time-pos instead of observe_property).</summary>
    public async Task<double?> GetDoubleAsync(string property, int timeoutMs = 900)
    {
        if (_writer is null || _process is { HasExited: true })
            return null;

        var id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDoubles[id] = tcs;
        var payload = JsonSerializer.Serialize(new
        {
            command = new object[] { "get_property", property },
            request_id = id,
        });

        try
        {
            if (!TryWritePayload(payload))
            {
                _pendingDoubles.TryRemove(id, out _);
                return null;
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            await using var reg = cts.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pendingDoubles.TryRemove(id, out _);
            return null;
        }
    }

    public bool LiveLightIpc => _liveLightIpc;

    /// <summary>Ping mpv IPC; kill the process if it does not answer.</summary>
    public async Task<bool> EnsureResponsiveAsync()
    {
        if (_process is null || _process.HasExited)
        {
            CleanupAfterProcessExit();
            _ipcFailed = false;
            return false;
        }

        if (_ipcFailed)
        {
            Log?.Invoke("mpv IPC 故障，强制结束进程");
            HardStop();
            return false;
        }

        if (await TryPingAsync().ConfigureAwait(false))
            return true;

        Log?.Invoke("mpv 无响应，强制结束进程");
        HardStop();
        return false;
    }

    /// <summary>Kill a hung-but-alive mpv so the next <see cref="StartAsync"/> can succeed.</summary>
    public void RecoverIfUnresponsive()
    {
        if (_process is null && _writer is null)
            return;

        if (_process is { HasExited: true })
        {
            CleanupAfterProcessExit();
            _ipcFailed = false;
            return;
        }

        if (!_ipcFailed)
            return;

        Log?.Invoke("mpv 无响应，正在重启播放组件");
        HardStop();
    }

    public void Dispose() => Stop();

    public void Stop()
    {
        TryCommand("set_property", "stream-record", "");
        ClearRecordingState();
        if (!_ipcFailed)
            TryCommand("quit");
        if (!_ipcFailed)
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.WaitForExit(ChildProcessLifetime.MpvQuitWaitMs);
            }
            catch { /* ignore */ }
        }

        HardStop();
    }

    private void HardStop()
    {
        FailAllPending();
        ForceTerminateProcess();
        CleanupAfterProcessExit();
        _ipcFailed = false;
    }

    private void FailAllPending()
    {
        foreach (var id in _pendingRequests.Keys)
        {
            if (_pendingRequests.TryRemove(id, out var tcs))
                tcs.TrySetResult(false);
        }

        foreach (var id in _pendingDoubles.Keys)
        {
            if (_pendingDoubles.TryRemove(id, out var tcs))
                tcs.TrySetResult(null);
        }
    }

    private async Task<bool> TryPingAsync()
    {
        if (_writer is null || _process is { HasExited: true })
            return false;

        var id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;
        var payload = JsonSerializer.Serialize(new
        {
            command = new object[] { "get_property", "idle-active" },
            request_id = id,
        });

        try
        {
            if (!TryWritePayload(payload))
            {
                _pendingRequests.TryRemove(id, out _);
                return false;
            }

            using var cts = new CancellationTokenSource(IpcPingTimeoutMs);
            await using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            return false;
        }
    }

    private void ForceTerminateProcess()
    {
        try
        {
            if (_process is { HasExited: false })
                ChildProcessLifetime.Stop(ref _process, waitMs: 2000);
        }
        catch { /* ignore */ }
        _process = null;
    }

    private void CleanupAfterProcessExit()
    {
        _readerCts?.Cancel();
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _pipe = null;
        _readerCts?.Dispose();
        _readerCts = null;
        _currentSub = null;
    }

    private async Task ConnectPipeAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(400, ct).ConfigureAwait(false);
                pipe.ReadMode = PipeTransmissionMode.Byte;
                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n",
                };
                _readerCts = new CancellationTokenSource();
                _ = Task.Run(() => ReadLoop(_readerCts.Token));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(80, ct).ConfigureAwait(false);
            }
        }

        throw new TimeoutException("无法连接 mpv IPC：" + last?.Message);
    }

    private void Observe(string name, int id) => Command("observe_property", id, name);

    private void Command(params object[] args) => TryCommand(args);

    private bool TryCommand(params object[] args)
    {
        if (_writer is null) return false;
        var id = Interlocked.Increment(ref _requestId);
        var payload = JsonSerializer.Serialize(new { command = args, request_id = id });
        return TryWritePayload(payload);
    }

    private bool TryWritePayload(string payload)
    {
        if (_writer is null) return false;
        var writer = _writer;
        if (!Monitor.TryEnter(writer, IpcWriteTimeoutMs))
        {
            Log?.Invoke("mpv IPC 写入超时");
            _ipcFailed = true;
            return false;
        }

        try
        {
            writer.WriteLine(payload);
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke("mpv IPC: " + ex.Message);
            _ipcFailed = true;
            return false;
        }
        finally
        {
            Monitor.Exit(writer);
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        if (_pipe is null) return;
        using var reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, 4096, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch { break; }
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { HandleLine(line); }
            catch { /* ignore malformed */ }
        }
    }

    private void HandleLine(string line)
    {
        _lastEventUtc = DateTime.UtcNow;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        if (root.TryGetProperty("request_id", out var ridEl) && ridEl.TryGetInt32(out var reqId))
        {
            var ok = root.TryGetProperty("error", out var err) && err.GetString() == "success";
            if (_pendingDoubles.TryRemove(reqId, out var doubleTcs))
            {
                double? value = null;
                if (ok && root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Number)
                    value = dataEl.GetDouble();
                doubleTcs.TrySetResult(value);
            }

            if (_pendingRequests.TryRemove(reqId, out var tcs))
                tcs.TrySetResult(ok);

            if (!root.TryGetProperty("event", out _))
                return;
        }

        if (!root.TryGetProperty("event", out var ev)) return;
        if (ev.GetString() != "property-change") return;
        if (!root.TryGetProperty("name", out var nameEl)) return;
        var name = nameEl.GetString();
        if (!root.TryGetProperty("data", out var data)) return;
        switch (name)
        {
            case "time-pos" when data.ValueKind == JsonValueKind.Number:
                TimeChanged?.Invoke(data.GetDouble());
                break;
            case "duration" when data.ValueKind == JsonValueKind.Number:
                DurationChanged?.Invoke(data.GetDouble());
                break;
            case "pause":
                PauseChanged?.Invoke(IsTruthy(data));
                break;
            case "volume" when data.ValueKind == JsonValueKind.Number:
                _volume = (int)Math.Round(data.GetDouble());
                VolumeChanged?.Invoke(_volume);
                break;
            case "mute":
                _muted = IsTruthy(data);
                MuteChanged?.Invoke(_muted);
                break;
            case "speed" when data.ValueKind == JsonValueKind.Number:
                _speed = data.GetDouble();
                SpeedChanged?.Invoke(_speed);
                break;
            case "eof-reached":
                EofReached?.Invoke(IsTruthy(data));
                break;
            case "width" when data.ValueKind == JsonValueKind.Number:
                UpdateVideoSize((int)Math.Round(data.GetDouble()), _videoHeight);
                break;
            case "height" when data.ValueKind == JsonValueKind.Number:
                UpdateVideoSize(_videoWidth, (int)Math.Round(data.GetDouble()));
                break;
            case "paused-for-cache":
                _pausedForCache = IsTruthy(data);
                NotifyBufferingChanged();
                break;
            case "cache-buffering-state" when data.ValueKind == JsonValueKind.Number:
                _cacheBufferingPercent = data.GetDouble();
                if (IsBuffering)
                    NotifyBufferingChanged();
                break;
            case "demuxer-cache-state" when data.ValueKind == JsonValueKind.Object:
                _demuxerUnderrun = data.TryGetProperty("underrun", out var underrun) && IsTruthy(underrun);
                NotifyBufferingChanged();
                break;
        }
    }

    private void ResetBufferingState()
    {
        _pausedForCache = false;
        _cacheBufferingPercent = 0;
        _demuxerUnderrun = false;
    }

    private void NotifyBufferingChanged()
        => BufferingChanged?.Invoke(IsBuffering, _cacheBufferingPercent);

    private void UpdateVideoSize(int width, int height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (width == _videoWidth && height == _videoHeight) return;
        _videoWidth = width;
        _videoHeight = height;
        if (width > 0 && height > 0)
            VideoSizeChanged?.Invoke(width, height);
    }

    private static bool IsTruthy(JsonElement data)
        => data.ValueKind == JsonValueKind.True
           || (data.ValueKind == JsonValueKind.String && data.GetString() is "true" or "yes" or "1");
}
