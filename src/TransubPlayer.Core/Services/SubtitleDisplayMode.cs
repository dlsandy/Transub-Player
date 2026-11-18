namespace TransubPlayer.Services;

/// <summary>On-screen subtitle layout for preview tracks.</summary>
internal enum SubtitleDisplayMode
{
    /// <summary>Chinese translation (falls back to source until translated).</summary>
    Zh = 0,
    /// <summary>Source / ASR only.</summary>
    Source = 1,
    /// <summary>Chinese then source on the next line.</summary>
    Dual = 2,
    /// <summary>Hide on-screen subtitles.</summary>
    Off = 3,
}

internal static class SubtitleDisplayModeUtil
{
    public static SubtitleDisplayMode Parse(string? raw)
    {
        return (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "src" or "source" or "原文" => SubtitleDisplayMode.Source,
            "dual" or "双语" => SubtitleDisplayMode.Dual,
            "off" or "none" or "关闭" or "关" => SubtitleDisplayMode.Off,
            _ => SubtitleDisplayMode.Zh,
        };
    }

    public static string ToSetting(SubtitleDisplayMode mode) => mode switch
    {
        SubtitleDisplayMode.Source => "src",
        SubtitleDisplayMode.Dual => "dual",
        SubtitleDisplayMode.Off => "off",
        _ => "zh",
    };

    public static string Label(SubtitleDisplayMode mode) => mode switch
    {
        SubtitleDisplayMode.Source => "原文",
        SubtitleDisplayMode.Dual => "双语",
        SubtitleDisplayMode.Off => "关闭",
        _ => "译文",
    };

    public static bool IsContentMode(SubtitleDisplayMode mode)
        => mode is SubtitleDisplayMode.Zh or SubtitleDisplayMode.Source or SubtitleDisplayMode.Dual;
}
