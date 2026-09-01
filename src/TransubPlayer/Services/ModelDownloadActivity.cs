namespace TransubPlayer.Services;

/// <summary>Tracks in-flight model pack downloads (ASR / GGUF / llama-server / GPU packs).</summary>
internal static class ModelDownloadActivity
{
    private static int _active;

    public static bool IsActive => Volatile.Read(ref _active) > 0;

    public static async Task RunAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        try
        {
            await work(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
