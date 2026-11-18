namespace TransubPlayer.Services;

/// <summary>
/// General-purpose mpv property bags for <see cref="VideoEnhanceMode"/>.
/// Scalers alone are nearly invisible at 1:1; profiles also enable deband + a tiny unsharp GLSL.
/// </summary>
internal static class VideoEnhanceProfiles
{
    internal sealed record Profile(
        string Scale,
        string CScale,
        string DScale,
        bool Deband,
        int DebandIterations,
        double DebandThreshold,
        double DebandRange,
        double DebandGrain,
        bool SigmoidUpscaling,
        bool CorrectDownscaling,
        bool LinearDownscaling,
        double ScaleAntiring,
        double CScaleAntiring,
        /// <summary>mpv <c>scale-blur</c>; negative = sharper when scaling.</summary>
        double ScaleBlur,
        string DitherDepth,
        /// <summary>Extra mpv <c>sharpen</c> on top of color-grade (clamped later).</summary>
        double SharpenBias);

    public static Profile For(VideoEnhanceMode mode) => mode switch
    {
        VideoEnhanceMode.Light => LightProfile,
        VideoEnhanceMode.Enhance => EnhanceProfile,
        _ => OffProfile,
    };

    /// <summary>mpv defaults for <c>vo=gpu</c> (restore when enhance is off).</summary>
    private static readonly Profile OffProfile = new(
        Scale: "lanczos",
        CScale: "bilinear",
        DScale: "mitchell",
        Deband: false,
        DebandIterations: 1,
        DebandThreshold: 32,
        DebandRange: 16,
        DebandGrain: 48,
        SigmoidUpscaling: false,
        CorrectDownscaling: false,
        LinearDownscaling: false,
        ScaleAntiring: 0,
        CScaleAntiring: 0,
        ScaleBlur: 0,
        DitherDepth: "no",
        SharpenBias: 0);

    /// <summary>Mild deband + light unsharp — visible even at native resolution.</summary>
    private static readonly Profile LightProfile = new(
        Scale: "ewa_lanczos",
        CScale: "ewa_lanczossoft",
        DScale: "mitchell",
        Deband: true,
        DebandIterations: 2,
        DebandThreshold: 64,
        DebandRange: 16,
        DebandGrain: 32,
        SigmoidUpscaling: true,
        CorrectDownscaling: true,
        LinearDownscaling: true,
        ScaleAntiring: 0.4,
        CScaleAntiring: 0.4,
        ScaleBlur: -0.8,
        DitherDepth: "auto",
        SharpenBias: 0.55);

    /// <summary>Stronger deband + unsharp — general live-action / mixed content.</summary>
    private static readonly Profile EnhanceProfile = new(
        Scale: "ewa_lanczossharp",
        CScale: "ewa_lanczossoft",
        DScale: "hermite",
        Deband: true,
        DebandIterations: 4,
        DebandThreshold: 80,
        DebandRange: 20,
        DebandGrain: 48,
        SigmoidUpscaling: true,
        CorrectDownscaling: true,
        LinearDownscaling: true,
        ScaleAntiring: 0.8,
        CScaleAntiring: 0.8,
        ScaleBlur: -1.2,
        DitherDepth: "auto",
        SharpenBias: 1.25);
}
