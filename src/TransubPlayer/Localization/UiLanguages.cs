using System.Globalization;

namespace TransubPlayer.Localization;

/// <summary>
/// UI language catalog. Distinct from ASR job language (owned by playback presets).
/// Preference values: <see cref="Auto"/> | catalog tags (<c>zh-Hans</c>, <c>en</c>, …).
/// </summary>
public static class UiLanguages
{
    public const string Auto = "auto";
    public const string FallbackTag = "zh-Hans";

    /// <summary>Shipped UI packs. NativeName is shown as-is in the picker.</summary>
    public static IReadOnlyList<UiLanguageInfo> Catalog { get; } =
    [
        new("zh-Hans", "简体中文", "zh-Hans"),
        new("en", "English", "en"),
        new("ja", "日本語", "ja"),
        new("ko", "한국어", "ko"),
    ];

    public static bool IsKnownTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        if (string.Equals(tag, Auto, StringComparison.OrdinalIgnoreCase)) return true;
        return TryNormalize(tag, out _);
    }

    /// <summary>Resolve preference (<c>auto</c> or a tag) to a catalog tag.</summary>
    public static string Resolve(string? preference)
    {
        if (string.IsNullOrWhiteSpace(preference)
            || string.Equals(preference.Trim(), Auto, StringComparison.OrdinalIgnoreCase))
            return ResolveFromOs();

        return TryNormalize(preference.Trim(), out var tag) ? tag : FallbackTag;
    }

    public static CultureInfo ToCulture(string tag)
    {
        tag = Resolve(tag);
        try
        {
            var info = Catalog.FirstOrDefault(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
            return CultureInfo.GetCultureInfo(info?.CultureName ?? FallbackTag);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(FallbackTag);
        }
    }

    public static string ResolveFromOs()
    {
        // InstalledUICulture is the OS UI language and is unaffected by Loc.Apply
        // (which mutates CurrentUICulture). Using CurrentUICulture here would make
        // "auto" stick to the last manually chosen pack after a switch.
        var ui = CultureInfo.InstalledUICulture;
        var name = ui.Name;
        if (TryNormalize(name, out var tag))
            return tag;

        var two = ui.TwoLetterISOLanguageName;
        if (TryNormalize(two, out tag))
            return tag;

        return FallbackTag;
    }

    public static bool TryNormalize(string raw, out string tag)
    {
        tag = FallbackTag;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim().Replace('_', '-');
        foreach (var item in Catalog)
        {
            if (s.Equals(item.Tag, StringComparison.OrdinalIgnoreCase)
                || s.Equals(item.CultureName, StringComparison.OrdinalIgnoreCase))
            {
                tag = item.Tag;
                return true;
            }
        }

        // Common aliases (OS / BCP-47 prefixes).
        if (s.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            tag = "zh-Hans";
            return true;
        }

        if (s.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            tag = "en";
            return true;
        }

        if (s.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            || s.Equals("jp", StringComparison.OrdinalIgnoreCase))
        {
            tag = "ja";
            return true;
        }

        if (s.StartsWith("ko", StringComparison.OrdinalIgnoreCase)
            || s.Equals("kr", StringComparison.OrdinalIgnoreCase))
        {
            tag = "ko";
            return true;
        }

        return false;
    }
}

public sealed record UiLanguageInfo(string Tag, string NativeName, string CultureName);
