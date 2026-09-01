using System.Net.Http;

namespace TransubPlayer.Services;

/// <summary>Shared short-timeout client for local engine / llama health probes.</summary>
internal static class LocalHttp
{
    private static readonly HttpClient Shared = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    public static HttpClient Client => Shared;
}
