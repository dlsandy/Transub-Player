using System.Net.Sockets;

namespace TransubPlayer.Platform;

/// <summary>mpv IPC over Unix domain sockets (<c>--input-ipc-server=/tmp/...</c>).</summary>
internal sealed class UnixMpvIpcFactory : IMpvIpcFactory
{
    public string CreateServerEndpoint()
    {
        var name = "transub-player-mpv-" + Guid.NewGuid().ToString("N")[..12];
        var path = Path.Combine(Path.GetTempPath(), name + ".sock");
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }

        return path;
    }

    public async Task<IMpvIpcTransport> ConnectAsync(string endpoint, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            var ep = new UnixDomainSocketEndPoint(endpoint);
            await socket.ConnectAsync(ep, ct).ConfigureAwait(false);
            return new SocketMpvIpcTransport(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class SocketMpvIpcTransport : IMpvIpcTransport
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;

        public SocketMpvIpcTransport(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: true);
        }

        public Stream Stream => _stream;

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { /* ignore */ }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
