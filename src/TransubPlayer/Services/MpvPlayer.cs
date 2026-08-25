using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

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
    private int _volume = 100;
    private bool _muted;
    private double _speed = 1.0;
    private bool _subVisible = true;
    private int _videoWidth;
    private int _videoHeight;

    public event Action<double>? TimeChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<int>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<double>? SpeedChanged;
    public event Action<bool>? EofReached;
    public event Action<int, int>? VideoSizeChanged;
    public event Action<string>? Log;

    public bool IsRunning => _process is { HasExited: false };
    public int Volume => _volume;
    public bool Muted => _muted;
    public double Speed => _speed;
    public bool SubVisible => _subVisible;

    public void Start(string mpvPath, IntPtr hwnd, AppSettings? settings = null)
        => StartAsync(mpvPath, hwnd, settings).GetAwaiter().GetResult();

    public async Task StartAsync(string mpvPath, IntPtr hwnd, AppSettings? settings = null, CancellationToken ct = default)
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
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var arg in BuildStartupArgs(hwnd, ipc, settings))
            start.ArgumentList.Add(arg);

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
        Observe("time-pos", 1);
        Observe("duration", 2);
        Observe("pause", 3);
        Observe("volume", 4);
        Observe("mute", 5);
        Observe("speed", 6);
        Observe("eof-reached", 7);
        Observe("width", 8);
        Observe("height", 9);
        Command("set_property", "pause", true);
    }

    public void LoadFile(string mediaPath, bool autoPlay = true)
    {
        _currentSub = null;
        _videoWidth = 0;
        _videoHeight = 0;
        Command("loadfile", mediaPath, "replace");
        SetPause(!autoPlay);
        ShowOsd(MediaSourceHelper.DisplayName(mediaPath));
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

    public void SetSubVisible(bool visible)
    {
        _subVisible = visible;
        Command("set_property", "sub-visibility", visible ? "yes" : "no");
        ShowOsd(visible ? "字幕：开" : "字幕：关");
    }

    public void ToggleSubVisible() => SetSubVisible(!_subVisible);

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

    private static IEnumerable<string> BuildStartupArgs(IntPtr hwnd, string ipc, AppSettings settings)
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
        yield return "--idle=yes";
        yield return "--force-window=yes";
        yield return $"--hwdec={hw}";
        yield return "--vo=gpu";
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
        => Command("set_property", name, value);

    public void ShowOsd(string text, int durationMs = 1200)
        => Command("show-text", text, durationMs);

    public void Dispose() => Stop();

    public void Stop()
    {
        try { Command("quit"); } catch { /* ignore */ }
        try
        {
            if (_process is { HasExited: false })
                _process.WaitForExit(ChildProcessLifetime.MpvQuitWaitMs);
        }
        catch { /* ignore */ }

        _readerCts?.Cancel();
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _pipe = null;
        ChildProcessLifetime.Stop(ref _process, waitMs: 2000);
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

    private void Command(params object[] args)
    {
        if (_writer is null) return;
        var id = Interlocked.Increment(ref _requestId);
        var payload = JsonSerializer.Serialize(new { command = args, request_id = id });
        try
        {
            lock (_writer)
            {
                _writer.WriteLine(payload);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke("mpv IPC: " + ex.Message);
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
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
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
        }
    }

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
