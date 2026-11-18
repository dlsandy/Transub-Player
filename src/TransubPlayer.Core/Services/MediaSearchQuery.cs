using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>Search keywords derived from a media filename (JAV code or cleaned title).</summary>
internal sealed class MediaSearchQuery
{
    private static readonly Regex Fc2Pattern = new(
        @"(?i)\bfc2[-_ ]?ppv[-_ ]?(\d{6,7})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JavHyphenPattern = new(
        @"(?i)\b([a-z]{2,5})[-_ ](\d{2,5}[a-z]?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex JavCompactPattern = new(
        @"(?i)\b([a-z]{2,5})(\d{3,5}[a-z]?)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SeasonEpisodePattern = new(
        @"(?i)\bS(\d{1,2})E(\d{1,2})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] NoiseTokens =
    [
        "1080p", "720p", "2160p", "4k", "8k", "uhd", "fhd", "hd", "sd",
        "bluray", "blu-ray", "webrip", "web-dl", "webdl", "hdtv", "dvdrip",
        "x264", "x265", "h264", "h265", "hevc", "avc", "10bit", "aac", "dts",
        "remux", "repack", "proper", "extended", "uncut", "director",
        "yify", "yts", "rarbg", "hmax", "amzn", "nf", "web",
        "chinese", "chs", "cht", "eng", "japanese", "jp", "ja",
        "whisperjav", "mosaic", "uncensored", "censored",
    ];

    public required string Primary { get; init; }
    public IReadOnlyList<string> Alternates { get; init; } = [];
    public string? CatalogCode { get; init; }
    public bool IsCatalogCode { get; init; }

    public static MediaSearchQuery BuildFromPath(string mediaPath)
    {
        var stem = Path.GetFileNameWithoutExtension(mediaPath);
        if (string.IsNullOrWhiteSpace(stem))
            return new MediaSearchQuery { Primary = "" };

        var fc2 = Fc2Pattern.Match(stem);
        if (fc2.Success)
        {
            var code = $"FC2-PPV-{fc2.Groups[1].Value}";
            return new MediaSearchQuery
            {
                Primary = code,
                CatalogCode = code,
                IsCatalogCode = true,
            };
        }

        var hyphen = JavHyphenPattern.Match(stem);
        if (hyphen.Success)
        {
            var code = $"{hyphen.Groups[1].Value.ToUpperInvariant()}-{hyphen.Groups[2].Value.ToUpperInvariant()}";
            return new MediaSearchQuery
            {
                Primary = code,
                CatalogCode = code,
                IsCatalogCode = true,
            };
        }

        var compact = JavCompactPattern.Match(stem);
        if (compact.Success)
        {
            var code = $"{compact.Groups[1].Value.ToUpperInvariant()}-{compact.Groups[2].Value.ToUpperInvariant()}";
            return new MediaSearchQuery
            {
                Primary = code,
                CatalogCode = code,
                IsCatalogCode = true,
            };
        }

        var cleaned = CleanGeneralTitle(stem);
        var alternates = new List<string>();
        var se = SeasonEpisodePattern.Match(stem);
        if (se.Success)
            alternates.Add($"S{se.Groups[1].Value}E{se.Groups[2].Value}");

        return new MediaSearchQuery
        {
            Primary = cleaned,
            Alternates = alternates,
            IsCatalogCode = false,
        };
    }

    private static string CleanGeneralTitle(string stem)
    {
        var text = stem;
        text = text.Replace('_', ' ').Replace('.', ' ');
        text = Regex.Replace(text, @"\[[^\]]*\]", " ");
        text = Regex.Replace(text, @"\([^\)]*\)", " ");
        text = Regex.Replace(text, @"\{[^\}]*\}", " ");

        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(tokens.Length);
        foreach (var raw in tokens)
        {
            var t = raw.Trim('-', '+');
            if (t.Length == 0) continue;
            if (NoiseTokens.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(t, @"^\d{3,4}p$", RegexOptions.IgnoreCase)) continue;
            kept.Add(t);
        }

        var joined = string.Join(' ', kept).Trim();
        return joined.Length > 0 ? joined : stem.Trim();
    }
}
