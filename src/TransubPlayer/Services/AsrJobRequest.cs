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
    IReadOnlyList<Cue>? SeedCues = null);
