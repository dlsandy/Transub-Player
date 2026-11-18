using System.IO.Pipes;
using TransubPlayer.Platform;

namespace TransubPlayer.Platform.Windows;

internal sealed class WindowsMpvIpcFactory : IMpvIpcFactory
{
    public string CreateServerEndpoint()
    {
        var name = "transub-player-mpv-" + Guid.NewGuid().ToString("N")[..12];
        return @"\\.\pipe\" + name;
    }

    public async Task<IMpvIpcTransport> ConnectAsync(string endpoint, CancellationToken ct)
    {
        var pipeName = endpoint;
        const string prefix = @"\\.\pipe\";
        if (pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            pipeName = pipeName[prefix.Length..];

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(400, ct).ConfigureAwait(false);
        pipe.ReadMode = PipeTransmissionMode.Byte;
        return new NamedPipeTransport(pipe);
    }

    private sealed class NamedPipeTransport : IMpvIpcTransport
    {
        private readonly NamedPipeClientStream _pipe;

        public NamedPipeTransport(NamedPipeClientStream pipe) => _pipe = pipe;

        public Stream Stream => _pipe;

        public void Dispose()
        {
            try { _pipe.Dispose(); } catch { /* ignore */ }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
