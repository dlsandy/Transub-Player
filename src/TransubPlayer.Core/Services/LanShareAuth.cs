using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>Invite / password / session-token checks for LAN share HTTP.</summary>
internal sealed class LanShareAuth
{
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string _invite = "";

    public void ResetInvite(string invite)
    {
        lock (_gate)
            _invite = invite?.Trim() ?? "";
        _sessions.Clear();
    }

    public bool PasswordRequired(AppSettings settings)
        => !string.IsNullOrEmpty(NormalizedPassword(settings));

    public string IssueSession()
    {
        Prune();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _sessions[token] = DateTime.UtcNow.AddHours(24);
        return token;
    }

    public bool TryLogin(AppSettings settings, string? password, string? invite, out string session, out string error)
    {
        session = "";
        error = "";
        var required = NormalizedPassword(settings);
        if (required.Length > 0)
        {
            if (!FixedEquals(required, password ?? ""))
            {
                error = "bad password";
                return false;
            }
        }
        else
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_invite)
                    || !FixedEquals(_invite, (invite ?? "").Trim()))
                {
                    error = "bad invite";
                    return false;
                }
            }
        }

        session = IssueSession();
        return true;
    }

    public bool IsSessionOrInviteAuthorized(AppSettings settings, string? bearerOrQuery)
    {
        var raw = (bearerOrQuery ?? "").Trim();
        if (raw.Length == 0) return false;

        Prune();
        if (_sessions.TryGetValue(raw, out var exp))
        {
            if (exp > DateTime.UtcNow) return true;
            _sessions.TryRemove(raw, out _);
            return false;
        }

        // Invite in URL only when no password is configured (QR pairing).
        if (PasswordRequired(settings))
            return false;

        lock (_gate)
            return FixedEquals(_invite, raw);
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _sessions)
        {
            if (kv.Value <= now)
                _sessions.TryRemove(kv.Key, out _);
        }
    }

    private static string NormalizedPassword(AppSettings settings)
        => (settings.LanSharePassword ?? "").Trim();

    private static bool FixedEquals(string expected, string actual)
    {
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(actual);
        if (a.Length != b.Length)
        {
            // Still compare to reduce trivial timing leak on length.
            CryptographicOperations.FixedTimeEquals(a, a);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
