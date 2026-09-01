using System.Globalization;

namespace TransubPlayer.Services;

/// <summary>Hugging Face download base URL defaults by region.</summary>
internal static class HfEndpoints
{
    public const string Official = "https://huggingface.co";
    public const string ChinaMirror = "https://hf-mirror.com";

    /// <summary>Mainland China → mirror; otherwise official Hub.</summary>
    public static string Default()
        => IsMainlandChina() ? ChinaMirror : Official;

    public static string NormalizeOrDefault(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? Default() : raw.Trim().TrimEnd('/');

    /// <summary>
    /// Prefer system region (CN). Fall back to China Standard Time / Asia/Shanghai
    /// when region is unset but the machine is clearly on China mainland time.
    /// </summary>
    public static bool IsMainlandChina()
    {
        try
        {
            if (string.Equals(RegionInfo.CurrentRegion.TwoLetterISORegionName, "CN", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // ignore
        }

        try
        {
            var id = TimeZoneInfo.Local.Id;
            if (id.Contains("China Standard Time", StringComparison.OrdinalIgnoreCase)
                || id.Equals("Asia/Shanghai", StringComparison.OrdinalIgnoreCase)
                || id.Equals("Asia/Chongqing", StringComparison.OrdinalIgnoreCase)
                || id.Equals("Asia/Harbin", StringComparison.OrdinalIgnoreCase)
                || id.Equals("Asia/Urumqi", StringComparison.OrdinalIgnoreCase))
            {
                // Exclude HK / Macau / Taipei zones if present under similar names.
                if (id.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Macau", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Macao", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("Taipei", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("Asia/Hong_Kong", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("Asia/Macau", StringComparison.OrdinalIgnoreCase)
                    || id.Equals("Asia/Taipei", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }
}
