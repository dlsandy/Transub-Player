using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TransubPlayer.Services;

internal static class LanShareNet
{
    /// <summary>
    /// Best-effort inbound allow for the LAN share port (no elevation → may silently no-op).
    /// TcpListener already binds Any; firewall is the remaining phone-connect blocker.
    /// </summary>
    public static void TryEnsureFirewallRule(int port, Action<string>? log = null)
    {
        if (port is <= 0 or >= 65536) return;
        try
        {
            var name = $"Transub Player LAN {port}";
            // Delete+add keeps the rule in sync if the port changed.
            RunNetshSilent($"advfirewall firewall delete rule name=\"{name}\"");
            var ok = RunNetshSilent(
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain");
            if (ok)
                log?.Invoke($"LAN firewall · allowed TCP {port}");
            else
                log?.Invoke($"LAN firewall · could not add rule for TCP {port} (allow Transub Player in Windows Firewall if phone cannot connect)");
        }
        catch (Exception ex)
        {
            log?.Invoke("LAN firewall: " + ex.Message);
        }
    }

    private static bool RunNetshSilent(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(4000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string? TryGetLanIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = addr.Address;
                    if (IPAddress.IsLoopback(ip)) continue;
                    var bytes = ip.GetAddressBytes();
                    // Prefer private LAN ranges.
                    if (bytes[0] == 10
                        || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                        || (bytes[0] == 192 && bytes[1] == 168))
                        return ip.ToString();
                }
            }

            // Fallback: any non-loopback IPv4.
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static int FindFreePort(int preferred)
    {
        preferred = preferred is > 0 and < 65536 ? preferred : 8765;
        for (var i = 0; i < 30; i++)
        {
            var port = preferred + i;
            if (port > 65535) port = 8765 + (i % 100);
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
                // try next
            }
        }

        var fallback = new TcpListener(IPAddress.Loopback, 0);
        fallback.Start();
        var assigned = ((IPEndPoint)fallback.LocalEndpoint).Port;
        fallback.Stop();
        return assigned;
    }

    public static string EnsureToken(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LanShareToken)
            && settings.LanShareToken.Trim().Length >= 8)
            return settings.LanShareToken.Trim();

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
        settings.LanShareToken = token;
        settings.SaveSoon();
        return token;
    }

    /// <summary>
    /// If password is empty, generate a short human-readable one and persist it.
    /// Returns true when a new password was created.
    /// </summary>
    public static bool EnsurePassword(AppSettings settings, out string password)
    {
        if (!string.IsNullOrWhiteSpace(settings.LanSharePassword))
        {
            password = settings.LanSharePassword.Trim();
            return false;
        }

        password = GenerateReadablePassword();
        settings.LanSharePassword = password;
        settings.SaveSoon();
        return true;
    }

    public static string GenerateReadablePassword()
    {
        // Avoid ambiguous chars (0/O, 1/l/I).
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}
