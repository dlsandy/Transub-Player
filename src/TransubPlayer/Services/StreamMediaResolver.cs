namespace TransubPlayer.Services;

internal static class StreamMediaResolver
{
    public static bool IsDirectStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        return url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || url.Contains(LiveCamSeal.DoppioToken, StringComparison.OrdinalIgnoreCase)
               || url.Contains(LiveCamSeal.SacToken, StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MayNeedResolve(string? url) => LiveCamStreamResolver.MayResolve(url);

    public static async Task<(string PlayUrl, ResolvedNetworkStream? Meta)> PrepareAsync(string input, CancellationToken ct)
    {
        if (!MediaSourceHelper.IsRemoteUrl(input) || MediaSourceHelper.IsScreenCapture(input))
            return (input, null);

        if (IsDirectStreamUrl(input))
            return (input, null);

        if (!LiveCamStreamResolver.MayResolve(input))
            return (input, null);

        try
        {
            var resolved = await LiveCamStreamResolver.ResolveAsync(input, ct).ConfigureAwait(false);
            return (resolved.StreamUrl, resolved);
        }
        catch (StreamResolveException ex)
            when (!LiveCamStreamResolver.IsLiveCamPage(input)
                  && !input.Contains(LiveCamSeal.BrandHost, StringComparison.OrdinalIgnoreCase)
                  && ex.Kind == StreamResolveKind.Generic)
        {
            PlayerLog.Write("页面解析未命中直播源，尝试直接播放：" + ex.Message);
            return (input, null);
        }
    }
}
