namespace TransubPlayer.Services;

/// <summary>
/// Release-check mirrors: GitHub (global) and GitCode (mainland). Same owner/repo on both.
/// </summary>
internal static class AppUpdateEndpoints
{
    public const string Owner = "dlsandy";
    public const string Repo = "Transub-Player";

    public const string Auto = "auto";
    public const string GitHub = "github";
    public const string GitCode = "gitcode";

    public static string Default()
        => HfEndpoints.IsMainlandChina() ? GitCode : GitHub;

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Auto;
        var s = raw.Trim().ToLowerInvariant();
        return s switch
        {
            Auto or "default" => Auto,
            GitHub or "gh" => GitHub,
            GitCode or "gc" or "cn" => GitCode,
            _ => Auto,
        };
    }

    public static string Resolve(string? preference)
    {
        var n = Normalize(preference);
        return n == Auto ? Default() : n;
    }

    /// <summary>Primary then fallback — prefer the resolved source first.</summary>
    public static IReadOnlyList<AppUpdateSource> OrderedSources(string? preference)
    {
        var primary = Resolve(preference);
        var secondary = primary == GitCode ? GitHub : GitCode;
        return [Describe(primary), Describe(secondary)];
    }

    public static AppUpdateSource Describe(string id)
    {
        id = Normalize(id) == Auto ? Default() : Normalize(id);
        return id == GitCode
            ? new AppUpdateSource(
                GitCode,
                "GitCode",
                $"https://api.gitcode.com/api/v5/repos/{Owner}/{Repo}/releases/latest",
                $"https://gitcode.com/{Owner}/{Repo}/releases")
            : new AppUpdateSource(
                GitHub,
                "GitHub",
                $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest",
                $"https://github.com/{Owner}/{Repo}/releases");
    }
}

internal sealed record AppUpdateSource(
    string Id,
    string DisplayName,
    string LatestApiUrl,
    string ReleasesPageUrl);
