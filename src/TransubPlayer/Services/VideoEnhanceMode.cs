namespace TransubPlayer.Services;

/// <summary>
/// Real-time picture enhance via mpv built-in scalers / deband (no external AI upscaler).
/// Default <see cref="Off"/> keeps the light startup path.
/// </summary>
public enum VideoEnhanceMode
{
    Off = 0,
    Light = 1,
    Enhance = 2,
}

internal static class VideoEnhanceModes
{
    public const string Off = "off";
    public const string Light = "light";
    public const string Enhance = "enhance";

    public static VideoEnhanceMode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return VideoEnhanceMode.Off;

        return raw.Trim().ToLowerInvariant() switch
        {
            Light or "low" or "mild" => VideoEnhanceMode.Light,
            Enhance or "high" or "strong" or "on" => VideoEnhanceMode.Enhance,
            _ => VideoEnhanceMode.Off,
        };
    }

    public static string ToSetting(VideoEnhanceMode mode) => mode switch
    {
        VideoEnhanceMode.Light => Light,
        VideoEnhanceMode.Enhance => Enhance,
        _ => Off,
    };

    public static VideoEnhanceMode Cycle(VideoEnhanceMode mode) => mode switch
    {
        VideoEnhanceMode.Off => VideoEnhanceMode.Light,
        VideoEnhanceMode.Light => VideoEnhanceMode.Enhance,
        _ => VideoEnhanceMode.Off,
    };

    public static string LabelKey(VideoEnhanceMode mode) => mode switch
    {
        VideoEnhanceMode.Light => "Main.Menu.VideoEnhance.Light",
        VideoEnhanceMode.Enhance => "Main.Menu.VideoEnhance.Enhance",
        _ => "Main.Menu.VideoEnhance.Off",
    };

    public static string OsdKey(VideoEnhanceMode mode) => mode switch
    {
        VideoEnhanceMode.Light => "Main.Osd.VideoEnhance.Light",
        VideoEnhanceMode.Enhance => "Main.Osd.VideoEnhance.Enhance",
        _ => "Main.Osd.VideoEnhance.Off",
    };
}
