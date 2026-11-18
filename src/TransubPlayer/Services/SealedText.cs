using System.Text;

namespace TransubPlayer.Services;

/// <summary>Lightweight XOR seal for sensitive literals that must not sit as plaintext in the binary.</summary>
internal static class SealedText
{
    // App-local material — not the sealed payload itself.
    private static readonly byte[] Material =
    [
        0x54, 0x72, 0x61, 0x6E, 0x73, 0x75, 0x62, 0x50,
        0x6C, 0x61, 0x79, 0x65, 0x72, 0x53, 0x63, 0x21,
        0x39, 0x37, 0x2A, 0xB7, 0x4D, 0xE1, 0x08, 0xC3,
    ];

    public static string Open(string sealedB64)
    {
        var data = Convert.FromBase64String(sealedB64);
        var plain = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            plain[i] = (byte)(data[i] ^ Material[i % Material.Length] ^ (byte)(i * 31));
        return Encoding.UTF8.GetString(plain);
    }

    public static string OpenCached(ref string? slot, string sealedB64)
        => slot ??= Open(sealedB64);
}
