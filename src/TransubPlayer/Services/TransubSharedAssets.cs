using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>
/// Discovers an installed Transub tree (exe / shared / advanced-llm) for Player-side reuse.
/// Prefer live Transub assets over stale bundled copies when both exist.
/// </summary>
internal static class TransubSharedAssets
{
    private static readonly object Gate = new();
    private static string? _cachedInstallKey;
    private static string? _cachedInstallRoot;
    private static DateTime _cachedUtc;

    public static void Invalidate()
    {
        lock (Gate)
        {
            _cachedInstallKey = null;
            _cachedInstallRoot = null;
            _cachedUtc = DateTime.MinValue;
        }

        JaAsrDomainLexicon.Invalidate();
        MtTrainedRemapLexicon.Invalidate();
        AvGlossaryLexicon.Invalidate();
        SubtitleGlossaryStore.Invalidate();
    }

    /// <summary>Directory that contains <c>Transub.exe</c>, if found.</summary>
    public static string? ResolveInstallRoot(AppSettings? settings = null)
    {
        settings ??= TryLoadSettings();
        var key = CacheKey(settings);
        lock (Gate)
        {
            if (_cachedInstallRoot is not null
                && string.Equals(_cachedInstallKey, key, StringComparison.Ordinal)
                && (DateTime.UtcNow - _cachedUtc).TotalSeconds < 30)
                return _cachedInstallRoot;
        }

        string? root = null;
        var exe = TransubInstall.FindExe(settings ?? new AppSettings());
        if (exe is not null)
            root = Path.GetDirectoryName(exe);

        if (string.IsNullOrWhiteSpace(root))
        {
            foreach (var candidate in InstallRootCandidates(settings))
            {
                if (LooksLikeInstallRoot(candidate))
                {
                    root = candidate;
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(root))
        {
            try { root = Path.GetFullPath(root); }
            catch { /* keep raw */ }
        }

        lock (Gate)
        {
            _cachedInstallKey = key;
            _cachedInstallRoot = root;
            _cachedUtc = DateTime.UtcNow;
        }

        return root;
    }

    public static IEnumerable<string> EnumerateSharedDirs(AppSettings? settings = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var full = Path.GetFullPath(dir);
                if (!Directory.Exists(full) || !seen.Add(full)) return;
                list.Add(full);
            }
            catch
            {
                // ignore
            }
        }

        var install = ResolveInstallRoot(settings);
        if (!string.IsNullOrWhiteSpace(install))
        {
            Add(Path.Combine(install, "shared"));
            Add(Path.Combine(install, "resources", "shared"));
        }

        // Sibling repo next to Player (dev).
        try
        {
            var sibling = Path.Combine(Path.GetDirectoryName(AppPaths.ProjectRoot) ?? "", "Transub", "shared");
            Add(sibling);
        }
        catch { /* ignore */ }

        Add(Path.Combine(AppPaths.ProjectRoot, "..", "Transub", "shared"));

        foreach (var d in list)
            yield return d;
    }

    /// <summary>Live Transub shared files first (hot), then bundled Assets / data fallbacks.</summary>
    public static IEnumerable<string> EnumerateAssetCandidates(
        string fileName,
        AppSettings? settings = null,
        params string[] bundledFallbacks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shared in EnumerateSharedDirs(settings))
        {
            var p = Path.Combine(shared, fileName);
            if (!seen.Add(p)) continue;
            yield return p;
        }

        foreach (var p in bundledFallbacks)
        {
            if (string.IsNullOrWhiteSpace(p) || !seen.Add(p)) continue;
            yield return p;
        }
    }

    public static string? FindSharedFile(string fileName, AppSettings? settings = null)
    {
        foreach (var p in EnumerateSharedDirs(settings))
        {
            var file = Path.Combine(p, fileName);
            if (File.Exists(file))
                return file;
        }

        return null;
    }

    public static TransubReuseSummary DescribeReuse(AppSettings settings)
    {
        var install = ResolveInstallRoot(settings);

        var adv = AppPaths.ResolveAdvancedLlmRoot(settings);
        var mtFromTransub = false;
        if (!string.IsNullOrWhiteSpace(adv) && !string.IsNullOrWhiteSpace(install))
        {
            try
            {
                var advFull = Path.GetFullPath(adv);
                var installFull = Path.GetFullPath(install);
                mtFromTransub = advFull.StartsWith(installFull, StringComparison.OrdinalIgnoreCase)
                    || advFull.Contains($"{Path.DirectorySeparatorChar}Transub{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
                // Player-owned advanced-llm (default or custom) is not "from Transub"
                var installRoot = AppPaths.ResolveAdvancedLlmInstallRoot(settings);
                if (string.Equals(advFull, Path.GetFullPath(installRoot), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(advFull, Path.GetFullPath(AppPaths.AdvancedLlmDir), StringComparison.OrdinalIgnoreCase))
                    mtFromTransub = false;
            }
            catch
            {
                mtFromTransub = false;
            }
        }

        var glossary = SubtitleGlossaryStore.ResolvePath(settings);
        var glossaryFromTransub = glossary is not null
            && !glossary.StartsWith(AppPaths.AppDataDir, StringComparison.OrdinalIgnoreCase)
            && (install is not null && glossary.StartsWith(install, StringComparison.OrdinalIgnoreCase)
                || glossary.Contains($"{Path.DirectorySeparatorChar}Transub{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var lexiconFromTransub = FindSharedFile("ja-asr-domain-fixes.json", settings) is not null
            || FindSharedFile("mt-trained-remaps.json", settings) is not null
            || FindSharedFile("av-domain-glossary.json", settings) is not null;

        return new TransubReuseSummary(
            InstallRoot: install,
            TranslateModel: mtFromTransub && ManagedLlmInstaller.HasPreferredGguf(settings),
            Glossary: glossaryFromTransub,
            Lexicon: lexiconFromTransub);
    }

    public static string FormatReuseStatus(AppSettings settings)
    {
        var s = DescribeReuse(settings);
        if (string.IsNullOrWhiteSpace(s.InstallRoot) && !s.TranslateModel && !s.Glossary && !s.Lexicon)
            return Loc.Get("Settings.TransubReuse.None");

        var bits = new List<string>();
        if (s.TranslateModel) bits.Add(Loc.Get("Settings.TransubReuse.Mt"));
        if (s.Glossary) bits.Add(Loc.Get("Settings.TransubReuse.Glossary"));
        if (s.Lexicon) bits.Add(Loc.Get("Settings.TransubReuse.Lexicon"));
        if (bits.Count == 0)
            return Loc.Format("Settings.TransubReuse.FoundIdle", s.InstallRoot ?? "");
        return Loc.Format("Settings.TransubReuse.Active", string.Join(" · ", bits));
    }

    private static IEnumerable<string> InstallRootCandidates(AppSettings? settings)
    {
        if (!string.IsNullOrWhiteSpace(settings?.TransubInstallPath))
        {
            var typed = settings.TransubInstallPath.Trim();
            if (Directory.Exists(typed))
                yield return typed;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "Transub");
        yield return Path.Combine(local, "Transub");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Transub");

        string? sibling = null;
        try
        {
            sibling = Path.Combine(Path.GetDirectoryName(AppPaths.ProjectRoot) ?? "", "Transub");
        }
        catch
        {
            sibling = null;
        }

        if (!string.IsNullOrWhiteSpace(sibling))
            yield return sibling;
    }

    private static bool LooksLikeInstallRoot(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Transub.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "shared"))) return true;
            if (Directory.Exists(Path.Combine(dir, "advanced-llm"))) return true;
            if (Directory.Exists(Path.Combine(dir, "transub-engine"))) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string CacheKey(AppSettings? settings)
        => settings?.TransubInstallPath ?? "";

    private static AppSettings? TryLoadSettings()
    {
        try { return AppSettings.Load(); }
        catch { return null; }
    }
}

internal readonly record struct TransubReuseSummary(
    string? InstallRoot,
    bool TranslateModel,
    bool Glossary,
    bool Lexicon);
