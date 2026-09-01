using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Preview MT output language. Distinct from UI language and ASR source language.</summary>
internal static class TranslateTargets
{
    public const string Zh = "zh";
    /// <summary>Traditional Chinese (written form). Distinct from <see cref="Zh"/> (Simplified).</summary>
    public const string ZhHant = "zh-Hant";
    public const string En = "en";
    public const string Ja = "ja";
    public const string Ko = "ko";

    public static readonly string[] All = [Zh, ZhHant, En, Ja, Ko];

    public static string Normalize(string? raw)
    {
        var t = raw?.Trim() ?? "";
        if (t.Length == 0) return Zh;
        var lower = t.ToLowerInvariant();
        return lower switch
        {
            En or "eng" or "english" => En,
            Ja or "jp" or "japanese" => Ja,
            Ko or "kr" or "korean" => Ko,
            "zh-hant" or "zh-tw" or "zh-hk" or "zh-mo" or "zht" or "hant"
                or "traditional" or "traditional-chinese" or "zh_hant" or "zh_tw" => ZhHant,
            Zh or "cn" or "zh-cn" or "zh-hans" or "zh_hans" or "zh_cn"
                or "chinese" or "simplified" or "simplified-chinese" => Zh,
            _ => Zh,
        };
    }

    public static bool IsEnglish(string? target) => Normalize(target) == En;
    public static bool IsChinese(string? target)
    {
        var t = Normalize(target);
        return t is Zh or ZhHant;
    }

    public static bool IsSimplifiedChinese(string? target) => Normalize(target) == Zh;
    public static bool IsTraditionalChinese(string? target) => Normalize(target) == ZhHant;
    public static bool IsJapanese(string? target) => Normalize(target) == Ja;
    public static bool IsKorean(string? target) => Normalize(target) == Ko;

    public static bool IsEnglish(AppSettings settings) => IsEnglish(settings.TranslateTarget);
    public static bool IsChinese(AppSettings settings) => IsChinese(settings.TranslateTarget);
    public static bool IsSimplifiedChinese(AppSettings settings) => IsSimplifiedChinese(settings.TranslateTarget);
    public static bool IsTraditionalChinese(AppSettings settings) => IsTraditionalChinese(settings.TranslateTarget);
    public static bool IsJapanese(AppSettings settings) => IsJapanese(settings.TranslateTarget);
    public static bool IsKorean(AppSettings settings) => IsKorean(settings.TranslateTarget);

    /// <summary>Sidecar / cache filename segment (safe on Windows).</summary>
    public static string FileSuffix(string? target) => Normalize(target) switch
    {
        ZhHant => "zh-Hant",
        En => En,
        Ja => Ja,
        Ko => Ko,
        _ => Zh,
    };

    public static string EnglishLabel(string? target) => Normalize(target) switch
    {
        En => "English",
        Ja => "Japanese",
        Ko => "Korean",
        ZhHant => "Traditional Chinese",
        _ => "Simplified Chinese",
    };

    public static string ChineseLabel(string? target) => Normalize(target) switch
    {
        En => "英语",
        Ja => "日语",
        Ko => "韩语",
        ZhHant => "繁体中文",
        _ => "简体中文",
    };

    /// <summary>
    /// Default translation target from UI preference (<c>auto</c> / catalog tag).
    /// zh-Hans → zh; zh-Hant → zh-Hant; en → en; ja → ja; ko → ko; unknown → zh.
    /// </summary>
    public static string FromUiLanguage(string? uiLanguagePreference)
    {
        var tag = UiLanguages.Resolve(uiLanguagePreference);
        if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return En;
        if (tag.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return Ja;
        if (tag.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return Ko;
        if (tag.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase))
            return ZhHant;
        return Zh;
    }
}
