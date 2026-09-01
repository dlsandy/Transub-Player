namespace TransubPlayer.Services;

internal static class AppPaths
{
    public const string PortableMarkerFileName = "portable.txt";

    private static string? _projectRoot;
    private static string? _appDataDir;
    private static bool? _isDevTree;
    private static bool? _isPortable;

    /// <summary>
    /// Repo root when running from source (folder that contains native/); otherwise the app base directory.
    /// </summary>
    public static string ProjectRoot
    {
        get
        {
            if (_projectRoot is not null) return _projectRoot;
            _projectRoot = ResolveProjectRoot();
            return _projectRoot;
        }
    }

    /// <summary>Directory that contains TransubPlayer.exe (install / portable root).</summary>
    public static string InstallRoot
        => Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// True when settings/models live next to the exe (portable zip or legacy side-by-side data).
    /// False for Program Files installs (data under LocalAppData).
    /// </summary>
    public static bool IsPortable
    {
        get
        {
            if (_isPortable is not null) return _isPortable.Value;
            _isPortable = DetectPortable();
            return _isPortable.Value;
        }
    }

    /// <summary>Running from the git checkout (dotnet run / bin output under the repo).</summary>
    public static bool IsDevTree
    {
        get
        {
            if (_isDevTree is not null) return _isDevTree.Value;
            _isDevTree = DetectDevTree(ProjectRoot);
            return _isDevTree.Value;
        }
    }

    /// <summary>
    /// Settings / cache / models:
    /// portable &amp; legacy → {InstallRoot}/data/;
    /// installed → %LocalAppData%/Transub Player/data/;
    /// dev tree → {ProjectRoot}/data/.
    /// </summary>
    public static string AppDataDir
    {
        get
        {
            if (_appDataDir is not null) return _appDataDir;
            _appDataDir = ResolveAppDataDir();
            Directory.CreateDirectory(_appDataDir);
            return _appDataDir;
        }
    }

    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string ResumePath => Path.Combine(AppDataDir, "resume.json");

    public static string PreviewDir => EnsureSub("preview");
    public static string LogsDir => EnsureSub("logs");
    public static string ScreenshotsDir => EnsureSub("screenshots");
    public static string RecordingsDir => EnsureSub("recordings");
    public static string CacheDir => EnsureSub("cache");
    public static string ModelsDir => EnsureSub("models");

    public static string NativeRoot
    {
        get
        {
            var nextToExe = Path.Combine(AppContext.BaseDirectory);
            var repoNative = Path.Combine(ProjectRoot, "native");
            return Directory.Exists(repoNative) ? repoNative : nextToExe;
        }
    }

    public static string NativeMpvDir
    {
        get
        {
            var nextToExe = Path.Combine(AppContext.BaseDirectory, "mpv");
            var repoNative = Path.Combine(NativeRoot, "mpv");
            if (File.Exists(Path.Combine(nextToExe, "mpv.exe"))) return nextToExe;
            if (File.Exists(Path.Combine(repoNative, "mpv.exe"))) return repoNative;
            return nextToExe;
        }
    }

    /// <summary>Default Player-owned llama-server + GGUF under data/advanced-llm.</summary>
    public static string AdvancedLlmDir => EnsureSub("advanced-llm");

    public static string AdvancedLlmRuntimeDir
        => ResolveAdvancedLlmRuntimeDir(settings: null);

    public static string AdvancedLlmModelsDir
        => ResolveAdvancedLlmModelsDir(settings: null);

    /// <summary>
    /// Install / primary advanced-llm root: <see cref="AppSettings.AdvancedLlmPath"/> when set,
    /// otherwise <see cref="AdvancedLlmDir"/>.
    /// </summary>
    public static string ResolveAdvancedLlmInstallRoot(AppSettings? settings = null)
    {
        if (!string.IsNullOrWhiteSpace(settings?.AdvancedLlmPath))
        {
            try
            {
                var root = Path.GetFullPath(settings.AdvancedLlmPath.Trim());
                Directory.CreateDirectory(root);
                return root;
            }
            catch
            {
                // fall through to default
            }
        }

        return AdvancedLlmDir;
    }

    public static string ResolveAdvancedLlmRuntimeDir(AppSettings? settings = null)
    {
        var dir = Path.Combine(ResolveAdvancedLlmInstallRoot(settings), "runtime");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ResolveAdvancedLlmModelsDir(AppSettings? settings = null)
    {
        var dir = Path.Combine(ResolveAdvancedLlmInstallRoot(settings), "models");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Prefer a real advanced-llm tree (llama-server / GGUF). When <paramref name="settings"/>
    /// has <c>AdvancedLlmPath</c> / <c>TransubInstallPath</c>, those are considered before empty Player placeholders.
    /// </summary>
    public static string? ResolveAdvancedLlmRoot(AppSettings? settings = null)
        => LlamaServerProcess.FindAdvancedLlmRoot(settings);
    public static bool HasPortableMarker(string? root = null)
    {
        root ??= InstallRoot;
        return File.Exists(Path.Combine(root, PortableMarkerFileName))
               || File.Exists(Path.Combine(root, ".portable"));
    }

    private static string ResolveAppDataDir()
    {
        if (IsDevTree)
            return Path.Combine(ProjectRoot, "data");

        if (IsPortable)
            return Path.Combine(InstallRoot, "data");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Transub Player",
            "data");
    }

    private static bool DetectPortable()
    {
        if (IsDevTree)
            return true;

        var root = InstallRoot;
        if (HasPortableMarker(root))
            return true;

        // Legacy portable / early beta: side-by-side data without marker.
        if (Directory.Exists(Path.Combine(root, "data")))
            return true;

        return false;
    }

    private static bool DetectDevTree(string root)
        => File.Exists(Path.Combine(root, "AGENTS.md"))
           || Directory.Exists(Path.Combine(root, "src", "TransubPlayer"));

    private static string EnsureSub(string name)
    {
        var dir = Path.Combine(AppDataDir, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var native = Path.Combine(dir.FullName, "native");
            var hasMarker = File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                || File.Exists(Path.Combine(dir.FullName, "README.md"))
                || Directory.Exists(Path.Combine(dir.FullName, "src", "TransubPlayer"));
            if (Directory.Exists(native) && hasMarker)
                return dir.FullName;
            dir = dir.Parent;
        }

        return Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
