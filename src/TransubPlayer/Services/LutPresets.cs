using System.Globalization;
using System.IO;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>
/// Ships a few small 3D LUT presets under <see cref="AppPaths.LutsDir"/> (generated on first use).
/// </summary>
internal static class LutPresets
{
    public sealed record Preset(string Id, string FileName, string LabelKey);

    public static readonly Preset[] Catalog =
    [
        new("warm-cinema", "warm-cinema.cube", "Lut.Preset.WarmCinema"),
        new("cool-night", "cool-night.cube", "Lut.Preset.CoolNight"),
        new("vintage-film", "vintage-film.cube", "Lut.Preset.VintageFilm"),
        new("contrast-punch", "contrast-punch.cube", "Lut.Preset.ContrastPunch"),
        new("soft-pastel", "soft-pastel.cube", "Lut.Preset.SoftPastel"),
    ];

    private const int Size = 17;

    public static string Dir
    {
        get
        {
            EnsureInstalled();
            return AppPaths.LutsDir;
        }
    }

    public static void EnsureInstalled()
    {
        var root = AppPaths.LutsDir;
        foreach (var preset in Catalog)
        {
            var path = Path.Combine(root, preset.FileName);
            if (File.Exists(path))
                continue;
            try
            {
                WriteCube(path, preset.Id, TransformFor(preset.Id));
            }
            catch
            {
                // leave missing; menu will skip
            }
        }
    }

    public static IReadOnlyList<(Preset Preset, string Path)> ListInstalled()
    {
        EnsureInstalled();
        var list = new List<(Preset, string)>(Catalog.Length);
        foreach (var preset in Catalog)
        {
            var path = Path.Combine(AppPaths.LutsDir, preset.FileName);
            if (File.Exists(path))
                list.Add((preset, path));
        }

        return list;
    }

    public static string? PathForId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        EnsureInstalled();
        var preset = Catalog.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return null;
        var path = Path.Combine(AppPaths.LutsDir, preset.FileName);
        return File.Exists(path) ? path : null;
    }

    public static string? IdForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string name;
        try { name = Path.GetFileName(path); }
        catch { return null; }

        foreach (var preset in Catalog)
        {
            if (string.Equals(preset.FileName, name, StringComparison.OrdinalIgnoreCase))
                return preset.Id;
        }

        return null;
    }

    private delegate void RgbTransform(ref double r, ref double g, ref double b);

    private static RgbTransform TransformFor(string id) => id switch
    {
        "warm-cinema" => WarmCinema,
        "cool-night" => CoolNight,
        "vintage-film" => VintageFilm,
        "contrast-punch" => ContrastPunch,
        "soft-pastel" => SoftPastel,
        _ => Identity,
    };

    private static void Identity(ref double r, ref double g, ref double b) { }

    private static void WarmCinema(ref double r, ref double g, ref double b)
    {
        // Soft S-curve + warm midtones (amber lift).
        r = SoftCurve(r);
        g = SoftCurve(g);
        b = SoftCurve(b);
        r = Clamp01(r * 1.06 + 0.02);
        g = Clamp01(g * 1.01 + 0.005);
        b = Clamp01(b * 0.92);
    }

    private static void CoolNight(ref double r, ref double g, ref double b)
    {
        r = SoftCurve(r);
        g = SoftCurve(g);
        b = SoftCurve(b);
        r = Clamp01(r * 0.88);
        g = Clamp01(g * 0.95 + 0.01);
        b = Clamp01(b * 1.08 + 0.02);
        // Slight overall underexposure.
        r = Clamp01(r * 0.94);
        g = Clamp01(g * 0.94);
        b = Clamp01(b * 0.96);
    }

    private static void VintageFilm(ref double r, ref double g, ref double b)
    {
        var l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        // Sepia blend + muted contrast.
        var sepR = Clamp01(l * 1.15 + 0.05);
        var sepG = Clamp01(l * 0.95 + 0.02);
        var sepB = Clamp01(l * 0.70);
        r = Clamp01(r * 0.45 + sepR * 0.55);
        g = Clamp01(g * 0.45 + sepG * 0.55);
        b = Clamp01(b * 0.40 + sepB * 0.60);
        r = SoftCurve(r);
        g = SoftCurve(g);
        b = SoftCurve(b);
    }

    private static void ContrastPunch(ref double r, ref double g, ref double b)
    {
        r = PunchCurve(r);
        g = PunchCurve(g);
        b = PunchCurve(b);
        // Tiny teal/orange push in shadows/highlights.
        var l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (l < 0.45)
        {
            b = Clamp01(b + 0.02);
            r = Clamp01(r - 0.01);
        }
        else
        {
            r = Clamp01(r + 0.02);
            b = Clamp01(b - 0.015);
        }
    }

    private static void SoftPastel(ref double r, ref double g, ref double b)
    {
        // Lift shadows, compress highlights, reduce saturation.
        r = LiftCompress(r);
        g = LiftCompress(g);
        b = LiftCompress(b);
        var l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        const double sat = 0.78;
        r = Clamp01(l + (r - l) * sat);
        g = Clamp01(l + (g - l) * sat);
        b = Clamp01(l + (b - l) * sat);
    }

    private static double SoftCurve(double x)
    {
        x = Clamp01(x);
        // Mild contrast around midtones.
        return Clamp01(x * x * (3 - 2 * x) * 0.35 + x * 0.65);
    }

    private static double PunchCurve(double x)
    {
        x = Clamp01(x);
        // Stronger S-curve.
        var s = x * x * (3 - 2 * x);
        return Clamp01(x * 0.35 + s * 0.65);
    }

    private static double LiftCompress(double x)
    {
        x = Clamp01(x);
        return Clamp01(Math.Pow(x, 0.85) * 0.92 + 0.04);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static void WriteCube(string path, string title, RgbTransform transform)
    {
        var sb = new StringBuilder(Size * Size * Size * 24 + 128);
        sb.AppendLine($"TITLE \"Transub Player / {title}\"");
        sb.AppendLine($"LUT_3D_SIZE {Size}");
        sb.AppendLine("# Generated preset — red varies fastest (IRIDAS)");

        var inv = 1.0 / (Size - 1);
        // Order: blue outer, green middle, red inner (standard .cube).
        for (var bi = 0; bi < Size; bi++)
        for (var gi = 0; gi < Size; gi++)
        for (var ri = 0; ri < Size; ri++)
        {
            var r = ri * inv;
            var g = gi * inv;
            var b = bi * inv;
            transform(ref r, ref g, ref b);
            sb.Append(Format(r)).Append(' ')
              .Append(Format(g)).Append(' ')
              .Append(Format(b)).AppendLine();
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, path, overwrite: true);
    }

    private static string Format(double v)
        => v.ToString("0.######", CultureInfo.InvariantCulture);
}
