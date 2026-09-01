namespace TransubPlayer.Services;

/// <summary>Parameters for one preview / prefetch ASR run.</summary>
internal sealed record AsrJobRequest(
    string MediaPath,
    string OutputDir,
    string Language,
    string AsrModel,
    string ContentProfile,
    double StartFromSeconds = 0,
    IReadOnlyList<Cue>? SeedCues = null);
