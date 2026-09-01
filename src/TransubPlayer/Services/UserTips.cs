namespace TransubPlayer.Services;

/// <summary>One-time UX tips persisted via <see cref="AppSettings.DismissedTips"/>.</summary>
internal static class UserTips
{
    public const string LagMentalModel = "lag-mental-model";
    public const string OfferWaitFirstZh = "offer-wait-first-zh";
    public const string StartupChecklist = "startup-checklist";
    public const string SetupWizard = "setup-wizard";
    public const string QualityHandoff = "quality-handoff";
    public const string SubDelayHint = "sub-delay-hint";
    public const string FrontierLegend = "frontier-legend";
    public const string FrontierLegendEn = "frontier-legend-en";
    public const string FrontierLegendToEn = "frontier-legend-to-en";
    public const string OfferEnglishSource = "offer-english-source";
    public const string TranslateTargetEn = "translate-target-en";
    public const string ShiftAppendPlaylist = "shift-append-playlist";
    public const string ExternalSubHint = "external-sub-hint";

    public static bool ShouldShow(AppSettings settings, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        return !settings.DismissedTips.Exists(t =>
            string.Equals(t, id, StringComparison.OrdinalIgnoreCase));
    }

    public static void Dismiss(AppSettings settings, string id, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (settings.DismissedTips.Exists(t =>
                string.Equals(t, id, StringComparison.OrdinalIgnoreCase)))
            return;

        settings.DismissedTips.Add(id);
        if (save)
            settings.Save();
    }
}
