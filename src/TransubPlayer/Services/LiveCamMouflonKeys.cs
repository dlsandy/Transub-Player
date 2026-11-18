namespace TransubPlayer.Services;

/// <summary>pkey (keyId in playlist) → pdkey (XOR material). Values stored sealed.</summary>
internal static class LiveCamMouflonKeys
{
    private static readonly Lazy<Dictionary<string, string>> KnownLazy = new(BuildKnown);

    private static Dictionary<string, string> Known => KnownLazy.Value;

    private static Dictionary<string, string> BuildKnown()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (encPkey, encPdkey) in LiveCamSeal.MouflonKeyPairs)
            map[SealedText.Open(encPkey)] = SealedText.Open(encPdkey);
        return map;
    }

    public static string? GetPdkey(string pkey)
        => Known.TryGetValue(pkey, out var pdkey) ? pdkey : null;

    public static IEnumerable<string> CandidatePdkeys(IEnumerable<string> pkeys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pkey in pkeys)
        {
            var pdkey = GetPdkey(pkey);
            if (!string.IsNullOrWhiteSpace(pdkey) && seen.Add(pdkey))
                yield return pdkey;
        }

        foreach (var pdkey in Known.Values)
        {
            if (seen.Add(pdkey))
                yield return pdkey;
        }
    }
}
