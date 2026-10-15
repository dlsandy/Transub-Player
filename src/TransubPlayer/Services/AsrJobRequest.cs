namespace TransubPlayer.Services;

/// <summary>Parameters for one preview / prefetch ASR run.</summary>
internal sealed record AsrJobRequest(
    string MediaPath,
    string OutputDir,
    string Language,
    string AsrModel,
    string ContentProfile,
    double StartFromSeconds = 0,
    /// <summary>Optional duration from the playing mpv instance; used if CLI probe fails.</summary>
    double KnownDurationSec = 0,
    IReadOnlyList<Cue>? SeedCues = null,
    /// <summary>
    /// Live player duration while the job runs. mpv often revises length after open;
    /// ASR extends the chunk plan when this reports a longer value.
    /// </summary>
    Func<double>? GetLiveDurationSec = null);
