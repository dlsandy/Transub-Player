namespace TransubPlayer.Services;

internal static class StripchatMouflonKeys
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["Zeechoej4aleeshi"] = "ubahjae7goPoodi6",
        ["Zokee2OhPh9kugh4"] = "Zokee2OhPh9kugh4",
        ["Ook7quaiNgiyuhai"] = "Ook7quaiNgiyuhai",
    };

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
