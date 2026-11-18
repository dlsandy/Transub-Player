using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace TransubPlayer.Services;

/// <summary>
/// Opens Stripchat TCP connections IPv4-first.
/// On networks without a working IPv6 route, the default client tries AAAA first and
/// burns the whole connect timeout ("The operation was canceled") even when IPv4 is fine.
/// A configured HTTP proxy is still honored: the callback then receives the proxy endpoint.
/// </summary>
internal static class StripchatConnect
{
    private static readonly TimeSpan PerAddressTimeout = TimeSpan.FromSeconds(4);

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var ep = context.DnsEndPoint;
        if (IPAddress.TryParse(ep.Host, out var literal))
            return await ConnectOneAsync(literal, ep.Port, cancellationToken, PerAddressTimeout).ConfigureAwait(false);

        var (v4Stream, v4Error) = await TryAnyAsync(
            await ResolveAsync(ep.Host, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false),
            ep.Port,
            cancellationToken).ConfigureAwait(false);
        if (v4Stream is not null)
            return v4Stream;

        var any = await ResolveAsync(ep.Host, AddressFamily.Unspecified, cancellationToken).ConfigureAwait(false);
        var rest = any.Where(a => a.AddressFamily != AddressFamily.InterNetwork).ToArray();
        var (otherStream, otherError) = await TryAnyAsync(rest, ep.Port, cancellationToken).ConfigureAwait(false);
        if (otherStream is not null)
            return otherStream;

        throw otherError ?? v4Error ?? new HttpRequestException($"无法连接 {ep.Host}");
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, AddressFamily family, CancellationToken ct)
    {
        try
        {
            return family == AddressFamily.Unspecified
                ? await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false)
                : await Dns.GetHostAddressesAsync(host, family, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    private static async Task<(Stream? Stream, Exception? Error)> TryAnyAsync(
        IPAddress[] addrs,
        int port,
        CancellationToken ct)
    {
        Exception? last = null;
        foreach (var addr in addrs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return (await ConnectOneAsync(addr, port, ct, PerAddressTimeout).ConfigureAwait(false), null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        return (null, last);
    }

    private static async Task<Stream> ConnectOneAsync(IPAddress addr, int port, CancellationToken ct, TimeSpan timeout)
    {
        var socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
            socket.DualMode = false;

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            await socket.ConnectAsync(new IPEndPoint(addr, port), linked.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
