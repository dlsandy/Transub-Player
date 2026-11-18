using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>ASR / filename-guessed source language. Distinct from <see cref="AppSettings.UiLanguage"/> and <see cref="TranslateTargets"/>.</summary>
internal static class SourceLanguages
{
    public const string Auto = "auto";
    public const string Ja = "ja";
    public const string Ko = "ko";
    public const string En = "en";
    public const string Zh = "zh";

    public static readonly string[] All = [Auto, Ja, Ko, En, Zh];

    public static string Normalize(string? raw)
    {
        var t = raw?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            Ja or "jp" or "japanese" => Ja,
            Ko or "kr" or "korean" => Ko,
            En or "eng" or "english" => En,
            Zh or "cn" or "zh-cn" or "zh-hans" or "chinese" => Zh,
            _ => Auto,
        };
    }

    public static bool IsAuto(string? lang) => Normalize(lang) == Auto;

    public static bool EqualsLang(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x == Auto || y == Auto) return false;
        return x == y;
    }

    public static string DisplayName(string? lang) => Normalize(lang) switch
    {
        Ja => Loc.Get("SourceLang.Ja"),
        Ko => Loc.Get("SourceLang.Ko"),
        En => Loc.Get("SourceLang.En"),
        Zh => Loc.Get("SourceLang.Zh"),
        _ => Loc.Get("SourceLang.Auto"),
    };

    /// <summary>English name used inside MT system prompts.</summary>
    public static string EnglishLabel(string? lang) => Normalize(lang) switch
    {
        Ja => "Japanese",
        Ko => "Korean",
        En => "English",
        Zh => "Chinese",
        _ => "the source language",
    };

    /// <summary>Chinese name used inside MT system prompts targeting zh.</summary>
    public static string ChineseLabel(string? lang) => Normalize(lang) switch
    {
        Ja => "日语",
        Ko => "韩语",
        En => "英语",
        Zh => "中文",
        _ => "原文",
    };
}
