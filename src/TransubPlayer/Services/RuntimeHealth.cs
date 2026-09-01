using TransubPlayer.Localization;

namespace TransubPlayer.Services;

internal enum RuntimeHealthLevel
{
    Ok,
    Warning,
    Error,
}

internal sealed record RuntimeHealthStatus(
    RuntimeHealthLevel Level,
    string Summary,
    IReadOnlyList<string> Issues);

/// <summary>Lightweight runtime component probe for title-bar health indicator.</summary>
internal static class RuntimeHealth
{
    public static RuntimeHealthStatus Probe(AppSettings settings)
    {
        var issues = new List<string>();
        var hasMpv = MpvLocator.Find() is not null;
        var hasEngine = EngineLocator.Find(settings) is not null;
        if (!hasMpv)
            issues.Add(Loc.Get("Main.Health.Missing.Mpv"));
        if (!hasEngine)
            issues.Add(Loc.Get("Main.Health.Missing.Engine"));
        if (settings.TranslateEnabled && !SetupWizard.IsTranslationReady(settings))
            issues.Add(Loc.Get("Main.Health.Missing.Mt"));

        if (issues.Count == 0)
            return new RuntimeHealthStatus(RuntimeHealthLevel.Ok, Loc.Get("Main.Health.Ready"), issues);

        var level = issues.Count >= 2 || !hasMpv || !hasEngine
            ? RuntimeHealthLevel.Error
            : RuntimeHealthLevel.Warning;
        var summary = level == RuntimeHealthLevel.Error
            ? Loc.Get("Main.Health.Missing")
            : Loc.Get("Main.Health.Warning");
        return new RuntimeHealthStatus(level, summary, issues);
    }

    public static string Tooltip(RuntimeHealthStatus status)
    {
        if (status.Issues.Count == 0)
            return status.Summary;
        return status.Summary + "\n\n" + string.Join("\n", status.Issues.Select(i => "· " + i))
               + "\n\n" + Loc.Get("Main.Health.Fix");
    }
}
