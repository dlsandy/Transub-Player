using System.Runtime.InteropServices;
using Microsoft.Win32;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal static class FileAssociationService
{
    private const string ProgIdPrefix = "TransubPlayer";
    private const string AppName = "TransubPlayer.exe";

    public static string? ExePath
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
            catch
            {
                return null;
            }
        }
    }

    public static string ProgIdFor(string extension) => $"{ProgIdPrefix}{MediaFileTypes.NormalizeExtension(extension)}";

    public static bool IsAssociated(string extension)
    {
        extension = MediaFileTypes.NormalizeExtension(extension);
        if (extension.Length == 0) return false;

        var progId = ProgIdFor(extension);
        try
        {
            using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}");
            var current = extKey?.GetValue(null) as string;
            if (string.Equals(current, progId, StringComparison.OrdinalIgnoreCase))
                return CommandPointsToUs(progId);

            using var openWith = extKey?.OpenSubKey("OpenWithProgids");
            if (openWith?.GetValue(progId) is not null && CommandPointsToUs(progId))
                return true;
        }
        catch
        {
            // ignore registry read errors
        }

        return false;
    }

    public static FileAssociationApplyResult Apply(IEnumerable<string> extensions, bool associate)
    {
        var exe = ExePath;
        if (string.IsNullOrWhiteSpace(exe))
            return new FileAssociationApplyResult(0, 0, Loc.Get("Settings.Association.Error.NoExe"));

        var ok = 0;
        var failed = 0;
        string? lastError = null;

        EnsureApplicationRegistration(exe);

        foreach (var raw in extensions)
        {
            var ext = MediaFileTypes.NormalizeExtension(raw);
            if (ext.Length == 0) continue;

            try
            {
                if (associate)
                {
                    RegisterProgId(ext, exe);
                    SetDefaultHandler(ext, ProgIdFor(ext));
                }
                else
                {
                    RemoveAssociation(ext);
                }

                ok++;
            }
            catch (Exception ex)
            {
                failed++;
                lastError = ex.Message;
            }
        }

        if (ok > 0)
            NotifyShellAssociationChanged();

        return new FileAssociationApplyResult(ok, failed, lastError);
    }

    public static int CountAssociated(IEnumerable<string> extensions)
        => extensions.Count(IsAssociated);

    private static void EnsureApplicationRegistration(string exe)
    {
        using var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{AppName}");
        appKey.SetValue("FriendlyAppName", "Transub Player");
        using var open = appKey.CreateSubKey(@"shell\open\command");
        open.SetValue(null, Quote(exe) + " \"%1\"");
        using var supported = appKey.CreateSubKey("SupportedTypes");
        foreach (var ext in MediaFileTypes.AllExtensions)
            supported.SetValue(ext, "");
    }

    private static void RegisterProgId(string extension, string exe)
    {
        var progId = ProgIdFor(extension);
        var label = Loc.Format("Settings.Association.FileTypeLabel", extension.TrimStart('.').ToUpperInvariant());

        using var progKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
        progKey.SetValue(null, label);
        progKey.SetValue("FriendlyTypeName", label);
        using (var icon = progKey.CreateSubKey("DefaultIcon"))
            icon.SetValue(null, Quote(exe) + ",0");
        using (var open = progKey.CreateSubKey(@"shell\open\command"))
            open.SetValue(null, Quote(exe) + " \"%1\"");
    }

    private static void SetDefaultHandler(string extension, string progId)
    {
        using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
        extKey.SetValue(null, progId);
        using var openWith = extKey.CreateSubKey("OpenWithProgids");
        openWith.SetValue(progId, "");
    }

    private static void RemoveAssociation(string extension)
    {
        var progId = ProgIdFor(extension);

        using (var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}", writable: true))
        {
            if (extKey is not null)
            {
                if (string.Equals(extKey.GetValue(null) as string, progId, StringComparison.OrdinalIgnoreCase))
                    extKey.DeleteValue(string.Empty, throwOnMissingValue: false);
                extKey.DeleteSubKeyTree("OpenWithProgids", throwOnMissingSubKey: false);
            }
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", throwOnMissingSubKey: false);
        }
        catch
        {
            // ignore
        }
    }

    private static bool CommandPointsToUs(string progId)
    {
        var exe = ExePath;
        if (string.IsNullOrWhiteSpace(exe)) return false;

        try
        {
            using var cmdKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{progId}\shell\open\command");
            var cmd = cmdKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(cmd)) return false;
            return cmd.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    private static void NotifyShellAssociationChanged()
    {
        try { SHChangeNotify(ShcneAssocchanged, ShcnfIdlist, IntPtr.Zero, IntPtr.Zero); }
        catch { /* ignore */ }
    }

    private const int ShcneAssocchanged = 0x08000000;
    private const uint ShcnfIdlist = 0x0000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}

internal sealed record FileAssociationApplyResult(int Succeeded, int Failed, string? LastError);
