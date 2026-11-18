namespace TransubPlayer.Platform;

/// <summary>mpv JSON IPC transport (named pipe on Windows, Unix domain socket elsewhere).</summary>
internal interface IMpvIpcTransport : IAsyncDisposable, IDisposable
{
    Stream Stream { get; }
}

internal interface IMpvIpcFactory
{
    /// <summary>Create a unique IPC endpoint path for <c>--input-ipc-server</c>.</summary>
    string CreateServerEndpoint();

    /// <summary>Connect as client to an endpoint previously passed to mpv.</summary>
    Task<IMpvIpcTransport> ConnectAsync(string endpoint, CancellationToken ct);
}
