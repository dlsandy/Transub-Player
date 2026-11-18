namespace TransubPlayer.Services;

/// <summary>
/// Runtime-decrypted tokens for the live-cam resolver path.
/// Source of sealed blobs: <c>tools/_seal_stripchat.py</c>.
/// </summary>
internal static class LiveCamSeal
{
    private static string? _brandHost;
    private static string? _officialOrigin;
    private static string? _officialOriginSlash;
    private static string? _officialHost;
    private static string? _globalMirrorOrigin;
    private static string? _globalMirrorHost;
    private static string? _doppioToken;
    private static string? _sacToken;
    private static string? _sacPlayReferer;
    private static string? _doppioNetHostTpl;
    private static string? _sacMediaUrlTpl;
    private static string? _sacEdgeAutoTpl;
    private static string? _doppioEdgeAutoTpl;
    private static string? _doppioHostRe;
    private static string? _pageRe;
    private static string? _linkRe;
    private static string? _userRe;
    private static string? _proxyPath;
    private static string? _hint0;
    private static string? _hint1;
    private static string? _hint2;
    private static string? _hint3;

    public static string BrandHost => SealedText.OpenCached(ref _brandHost, "JxktWn+NsOjg");
    public static string OfficialOrigin => SealedText.OpenCached(ref _officialOrigin, "PBkrQ3zU96bnAj1ZdqO5kb0WZ5VM");
    public static string OfficialOriginSlash => SealedText.OpenCached(ref _officialOriginSlash, "PBkrQ3zU96bnAj1ZdqO5kb0WZ5VMRQ==");
    public static string OfficialHost => SealedText.OpenCached(ref _officialHost, "JxktWn+NsOjgWCxfaw==");
    public static string GlobalMirrorOrigin => SealedText.OpenCached(ref _globalMirrorOrigin, "PBkrQ3zU96buAmFDcrK4gKpQZY4PDc5l3hQr");
    public static string GlobalMirrorHost => SealedText.OpenCached(ref _globalMirrorHost, "LhlxQHucsfn3Hi5EKKe9n6tZaA==");
    public static string DoppioToken => SealedText.OpenCached(ref _doppioToken, "MAIvQ2aBu+36");
    public static string SacToken => SealedText.OpenCached(ref _sacToken, "Jww8V2Gdq+zwESo=");
    public static string SacPlayReferer => SealedText.OpenCached(ref _sacPlayReferer, "PBkrQ3zU96b+AzYeZLa7mbBebNRCBc8wjkB2Gi/Z");
    public static string DoppioNetHostTpl => SealedText.OpenCached(ref _doppioNetHostTpl, "L10iHWuBqPn9GSxUaO6/lb0=");
    public static string SacMediaUrlTpl => SealedText.OpenCached(ref _sacMediaUrlTpl, "PBkrQ3zU96b5EytZZ+25nLoWd5tCDsx5zxAjTHLYo/7hsQV1VRiEt5rxQa1io+dc6a4aew==");
    public static string SacEdgeAutoTpl => SealedText.OpenCached(ref _sacEdgeAutoTpl, "PBkrQ3zU96bxEihVK6i9g+dLZZlFBNF52REgTjmVr/yj9gsrAVPJ5c6tXfFt9+hd/60SHF5rnJaK60w1Dg==");
    public static string DoppioEdgeAutoTpl => SealedText.OpenCached(ref _doppioEdgeAutoTpl, "PBkrQ3zU96bxEihVK6i9g+dca4pRA81p2BtpRXKC7/ng7UgjHlXW9YCzSOdrveFC+cIONktxxpSX80c=");
    public static string DoppioHostRe => SealedText.OpenCached(ref _doppioHostRe, "CkVgD2qKv+yqEytXY+25nLpEaZ9FA8Mn1Bk0AkvYpP787g43TUyXxM/oA75t/v5M5/ICP1F7nIXL9BhpEg==");
    public static string PageRe => SealedText.OpenCached(ref _pageRe, "PBkrQ3zR4qa7XnAKXZym3ZQTWNQIQNF+zhw3SH+XtM2ixQZ1VAaks87oA75s4f8AusYubmV/xYOUq0YfG23KqMbSlNIZk01zxJqtIGgZM5ygir4=");
    public static string LinkRe => SealedText.OpenCached(ref _linkRe, "PBkrQ3zR4qa7XnAKXZym3ZQTWNQIQNF+zhw3SH+XtM2ixQZ1VAaks87oA75s4f8AusYubmV/xYOUq0YfG23KqMbSlNIZk01z");
    public static string UserRe => SealedText.OpenCached(ref _userRe, "fFJlaFOZ9dS/KmEZLLOlgqBIZ5JAHv4k5xRqUTmr676koVstXU2LprqBEdh4v+BCqaQwbmI1wQ==");
    public static string ProxyPath => SealedText.OpenCached(ref _proxyPath, "OAQpViGD6/ys");

    public static string[] PreferredPkeys =>
    [
        SealedText.OpenCached(ref _hint0, "GwI0BH6bueDaESZJc6iwmQ=="),
        SealedText.OpenCached(ref _hint1, "Dgg6UGeBveOgFyNVY7O5mQ=="),
        SealedText.OpenCached(ref _hint2, "DgI0Vmrcl+HEHnZbc6e5xA=="),
        SealedText.OpenCached(ref _hint3, "EhxpXj26l7vOEw1bVJC8yQ=="),
    ];

    /// <summary>Sealed (pkey, pdkey) pairs for MOUFLON.</summary>
    public static readonly (string EncPkey, string EncPdkey)[] MouflonKeyPairs =
    [
        ("Dgg6UGeBveOgFyNVY7O5mQ==", "IQ8+W2WPvb7zGR9faaS4xg=="),
        ("DgI0Vmrcl+HEHnZbc6e5xA==", "BRg6UmHau+j9Ty1fTKHkkQ=="),
        ("GwI0BH6bueDaESZJc6iwmQ==", "ETwqVmqpsLv/FypHZ/OymA=="),
        ("EhxpXj26l7vOEw1bVJC8yQ==", "LA9pV2bfls+tMwloTpWmkg=="),
        ("Ex8NXWydt8vtLCJDb5TnvA==", "GgQ4e1aXl82tGntCcIGUkg=="),
        ("ZSklUGzYl+PEQXx8TaKluQ==", "DVtrZlmZgLzGBAZnaI+dgA=="),
        ("Gl8wf2CYjMDMFH9fNPiEmg==", "FS8aBFyE4OP8RSBgS/KwlQ=="),
        ("GjkUCm6fu8X5OAl9UbKhoQ==", "ICI8akCPqL3AD35cP4qrkg=="),
        ("YxgKXW2q6OPZNQ0JQYjiwg==", "OBccYjm/mt36Oj9yNrqctg=="),
        ("GwU2BGq6isvkHQ5FS4zhnA==", "PygnVj3XlrvnOglCToeghQ=="),
        ("GyElRji/tPDHHQgCYJaDng==", "Fx4wRVyNnsGtICZCVYrlqg=="),
    ];
}
