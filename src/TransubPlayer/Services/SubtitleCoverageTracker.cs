namespace TransubPlayer.Services;

/// <summary>
/// Prefix coverage helpers for streaming ASR: ready frontier, first-cue ETA, catch-up ETA.
/// No UI; PreviewController owns an instance.
/// </summary>
internal sealed class SubtitleCoverageTracker
{
    private DateTime? _genStartedUtc;
    private bool _gotFirstCue;
    private string _asrModel = "";
    private double _mediaDurationSec;
    private double _lastFrontier;
    private DateTime _lastSampleUtc;
    private double _mediaPerWallSec;

    public bool AwaitingFirstCue => _genStartedUtc.HasValue && !_gotFirstCue;

    public void Reset()
    {
        _genStartedUtc = null;
        _gotFirstCue = false;
        _asrModel = "";
        _mediaDurationSec = 0;
        _lastFrontier = 0;
        _lastSampleUtc = default;
        _mediaPerWallSec = 0;
    }

    public void OnAsrJobStarted(string? asrModel, double mediaDurationSec = 0)
    {
        _genStartedUtc = DateTime.UtcNow;
        _gotFirstCue = false;
        _asrModel = asrModel?.Trim() ?? "";
        _mediaDurationSec = mediaDurationSec > 1 ? mediaDurationSec : 0;
        _lastFrontier = 0;
        _lastSampleUtc = DateTime.UtcNow;
        _mediaPerWallSec = 0;
    }

    /// <summary>mpv often reports duration after the ASR job starts — keep the ETA scale current.</summary>
    public void SetMediaDuration(double mediaDurationSec)
    {
        if (mediaDurationSec > 1)
            _mediaDurationSec = mediaDurationSec;
    }

    public void OnCoverage(double subFrontier, int cueCount)
    {
        if (cueCount > 0 || subFrontier > 0.05)
            _gotFirstCue = true;

        var now = DateTime.UtcNow;
        if (_lastSampleUtc != default && subFrontier > _lastFrontier + 0.05)
        {
            var wall = (now - _lastSampleUtc).TotalSeconds;
            if (wall >= 0.4)
            {
                var rate = (subFrontier - _lastFrontier) / wall;
                _mediaPerWallSec = _mediaPerWallSec <= 0
                    ? rate
                    : (_mediaPerWallSec * 0.65) + (rate * 0.35);
            }
        }

        if (subFrontier > _lastFrontier)
        {
            _lastFrontier = subFrontier;
            _lastSampleUtc = now;
        }
    }

    /// <summary>Wall-clock seconds until first source cue, or null if done / not started.</summary>
    public int? EstimateSecondsToFirstCue()
    {
        if (!AwaitingFirstCue || _genStartedUtc is null)
            return null;

        var elapsed = (DateTime.UtcNow - _genStartedUtc.Value).TotalSeconds;
        var baseline = BaselineFirstCueSeconds(_asrModel, _mediaDurationSec);
        var remain = baseline - elapsed;
        if (remain > 1)
            return (int)Math.Ceiling(remain);

        // Past the baseline: never stick at "约 2 秒" — grow a modest pad until the cue arrives.
        var overdue = Math.Max(0, elapsed - baseline);
        return (int)Math.Ceiling(Math.Clamp(10 + overdue * 0.4, 10, 90));
    }

    /// <summary>Wall-clock seconds for ASR frontier to reach <paramref name="targetMediaSec"/>.</summary>
    public int? EstimateSecondsToReach(double targetMediaSec, double currentFrontier)
    {
        var gap = targetMediaSec - currentFrontier;
        if (gap <= 0.5)
            return 0;
        if (_mediaPerWallSec >= 0.08)
            return (int)Math.Ceiling(Math.Clamp(gap / _mediaPerWallSec, 1, 600));
        return null;
    }

    public static bool IsReadyAt(double position, double readyFrontier, double slack = 0.75)
        => readyFrontier <= 0 || position <= readyFrontier + slack;

    public static double GapPastReady(double position, double readyFrontier)
        => readyFrontier <= 0 ? Math.Max(0, position) : Math.Max(0, position - readyFrontier);

    /// <summary>
    /// First-cue wall time is dominated by model warmup + whole-file audio/VAD prep,
    /// which scales with media length (observed ~22s turbo on a 2h+ file).
    /// </summary>
    private static double BaselineFirstCueSeconds(string asrModel, double mediaDurationSec)
    {
        double warmup;
        double secPerHour;
        if (asrModel.Contains("turbo", StringComparison.OrdinalIgnoreCase))
        {
            warmup = 10;
            secPerHour = 6;
        }
        else
        {
            warmup = 12;
            secPerHour = 8;
        }

        // Unknown duration → assume ~1h of prep cost so we do not under-promise.
        var hours = mediaDurationSec > 1 ? mediaDurationSec / 3600.0 : 1.0;
        var durationPart = secPerHour * Math.Min(hours, 4.0);
        return Math.Clamp(warmup + durationPart, 12, 180);
    }
}
