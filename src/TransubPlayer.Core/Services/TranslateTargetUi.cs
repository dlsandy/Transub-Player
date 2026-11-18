using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>User-visible labels that depend on <see cref="TranslateTargets"/>.</summary>
internal static class TranslateTargetUi
{
    public static string ModeTranslationLabel(AppSettings settings)
    {
        if (TranslateTargets.IsChinese(settings))
            return Loc.Get("Main.Mode.Zh");
        if (TranslateTargets.IsEnglish(settings))
            return Loc.Get("Main.Mode.En");
        return Loc.Format("Main.Mode.Translation", TargetDisplayName(settings));
    }

    public static string ModeDualLabel(AppSettings settings)
    {
        if (TranslateTargets.IsChinese(settings))
            return Loc.Get("Main.Mode.Dual");
        if (TranslateTargets.IsEnglish(settings))
            return Loc.Get("Main.Mode.Dual.EnTarget");
        return Loc.Format("Main.Mode.Dual.Translation", TargetDisplayName(settings));
    }

    public static string SubProgressLegendKey(AppSettings settings, bool englishSource)
    {
        if (englishSource && TranslateTargets.IsChinese(settings))
            return "Main.SubProgressLegend.En";
        if (TranslateTargets.IsEnglish(settings) && !englishSource)
            return "Main.SubProgressLegend.ToEn";
        return "Main.SubProgressLegend";
    }

    public static string FrontierLegendKey(AppSettings settings, bool englishSource)
    {
        if (englishSource && TranslateTargets.IsChinese(settings))
            return "Main.Osd.FrontierLegend.En";
        if (TranslateTargets.IsEnglish(settings) && !englishSource)
            return "Main.Osd.FrontierLegend.ToEn";
        return "Main.Osd.FrontierLegend";
    }

    public static string FrontierLegendTipId(AppSettings settings, bool englishSource)
    {
        if (englishSource && TranslateTargets.IsChinese(settings))
            return UserTips.FrontierLegendEn;
        if (TranslateTargets.IsEnglish(settings) && !englishSource)
            return UserTips.FrontierLegendToEn;
        return UserTips.FrontierLegend;
    }

    public static string ModeTranslationTip(AppSettings settings, bool englishSource)
    {
        if (englishSource && TranslateTargets.IsChinese(settings))
            return Loc.Get("Main.Mode.Zh.Tip.En");
        if (TranslateTargets.IsEnglish(settings))
            return Loc.Get("Main.Mode.Zh.Tip.EnTarget");
        if (TranslateTargets.IsJapanese(settings) || TranslateTargets.IsKorean(settings)
            || TranslateTargets.IsTraditionalChinese(settings))
            return Loc.Format("Main.Mode.Translation.Tip", TargetDisplayName(settings));
        return Loc.Get("Main.Mode.Zh.Tip");
    }

    public static string ModeSourceTip(AppSettings settings, bool englishSource)
    {
        if (englishSource && TranslateTargets.IsChinese(settings))
            return Loc.Get("Main.Mode.Src.Tip.En");
        return Loc.Get("Main.Mode.Src.Tip");
    }

    public static string ModeDualTip(AppSettings settings, bool englishSource)
    {
        if (englishSource && TranslateTargets.IsChinese(settings))
            return Loc.Get("Main.Mode.Dual.Tip.En");
        if (TranslateTargets.IsEnglish(settings))
            return Loc.Get("Main.Mode.Dual.Tip.EnTarget");
        if (TranslateTargets.IsJapanese(settings) || TranslateTargets.IsKorean(settings)
            || TranslateTargets.IsTraditionalChinese(settings))
            return Loc.Format("Main.Mode.Dual.Translation.Tip", TargetDisplayName(settings));
        return Loc.Get("Main.Mode.Dual.Tip");
    }

    public static string TargetDisplayName(AppSettings settings)
        => TranslateTargets.Normalize(settings.TranslateTarget) switch
        {
            TranslateTargets.En => Loc.Get("Settings.TranslateTarget.En"),
            TranslateTargets.Ja => Loc.Get("Settings.TranslateTarget.Ja"),
            TranslateTargets.Ko => Loc.Get("Settings.TranslateTarget.Ko"),
            TranslateTargets.ZhHant => Loc.Get("Settings.TranslateTarget.ZhHant"),
            _ => Loc.Get("Settings.TranslateTarget.Zh"),
        };
}
