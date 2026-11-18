namespace TransubPlayer.Services;

internal enum PrefetchUiKind
{
    Queued,
    Running,
    Ready,
    Failed,
    Idle,
}

internal sealed record PrefetchUiState(PrefetchUiKind Kind, string? Path = null, int QueueCount = 0);
