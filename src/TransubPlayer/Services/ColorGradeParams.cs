namespace TransubPlayer.Services;

/// <summary>mpv video equalizer knobs (−100…100, 0 = neutral).</summary>
public sealed class ColorGradeParams
{
    public const int Min = -100;
    public const int Max = 100;

    public int Brightness { get; set; }
    public int Contrast { get; set; }
    public int Saturation { get; set; }
    public int Gamma { get; set; }
    public int Hue { get; set; }
    /// <summary>Lift / crush dark tones (−100…100). Applied via vf colorlevels.</summary>
    public int Shadows { get; set; }
    /// <summary>mpv <c>sharpen</c> (−100…100 → blur…sharpen).</summary>
    public int Sharpness { get; set; }

    public bool IsDefault
        => Brightness == 0 && Contrast == 0 && Saturation == 0 && Gamma == 0 && Hue == 0
           && Shadows == 0 && Sharpness == 0;

    /// <summary>True when a CPU video filter is required (needs hwdec copy-back).</summary>
    public bool NeedsVideoFilter => Shadows != 0;

    public ColorGradeParams Clone() => new()
    {
        Brightness = Brightness,
        Contrast = Contrast,
        Saturation = Saturation,
        Gamma = Gamma,
        Hue = Hue,
        Shadows = Shadows,
        Sharpness = Sharpness,
    };

    public void CopyFrom(ColorGradeParams src)
    {
        Brightness = Clamp(src.Brightness);
        Contrast = Clamp(src.Contrast);
        Saturation = Clamp(src.Saturation);
        Gamma = Clamp(src.Gamma);
        Hue = Clamp(src.Hue);
        Shadows = Clamp(src.Shadows);
        Sharpness = Clamp(src.Sharpness);
    }

    public void Reset()
    {
        Brightness = Contrast = Saturation = Gamma = Hue = Shadows = Sharpness = 0;
    }

    public void Normalize()
    {
        Brightness = Clamp(Brightness);
        Contrast = Clamp(Contrast);
        Saturation = Clamp(Saturation);
        Gamma = Clamp(Gamma);
        Hue = Clamp(Hue);
        Shadows = Clamp(Shadows);
        Sharpness = Clamp(Sharpness);
    }

    public bool EqualsValues(ColorGradeParams? other)
        => other is not null
           && Brightness == other.Brightness
           && Contrast == other.Contrast
           && Saturation == other.Saturation
           && Gamma == other.Gamma
           && Hue == other.Hue
           && Shadows == other.Shadows
           && Sharpness == other.Sharpness;

    public static int Clamp(int v) => v < Min ? Min : v > Max ? Max : v;

    public static int FromSlider(double value) => Clamp((int)Math.Round(value));

    /// <summary>Map UI sharpness (−100…100) to mpv <c>sharpen</c> (−4…4).</summary>
    public static double ToMpvSharpen(int sharpness)
        => Clamp(sharpness) / 100.0 * 4.0;
}
