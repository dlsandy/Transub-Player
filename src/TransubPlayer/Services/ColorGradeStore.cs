using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TransubPlayer.Services;

/// <summary>Named color-grade presets under <see cref="AppPaths.ColorGradesDir"/>.</summary>
internal static class ColorGradeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed class SavedPreset
    {
        public string Name { get; set; } = "";
        public int Brightness { get; set; }
        public int Contrast { get; set; }
        public int Saturation { get; set; }
        public int Gamma { get; set; }
        public int Hue { get; set; }
        public int Shadows { get; set; }
        public int Sharpness { get; set; }

        public ColorGradeParams ToParams()
        {
            var p = new ColorGradeParams
            {
                Brightness = Brightness,
                Contrast = Contrast,
                Saturation = Saturation,
                Gamma = Gamma,
                Hue = Hue,
                Shadows = Shadows,
                Sharpness = Sharpness,
            };
            p.Normalize();
            return p;
        }

        public static SavedPreset From(string name, ColorGradeParams p) => new()
        {
            Name = name.Trim(),
            Brightness = p.Brightness,
            Contrast = p.Contrast,
            Saturation = p.Saturation,
            Gamma = p.Gamma,
            Hue = p.Hue,
            Shadows = p.Shadows,
            Sharpness = p.Sharpness,
        };
    }

    public static IReadOnlyList<(string Name, string Path, ColorGradeParams Params)> List()
    {
        var dir = AppPaths.ColorGradesDir;
        if (!Directory.Exists(dir))
            return [];

        var list = new List<(string, string, ColorGradeParams)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<SavedPreset>(json, JsonOptions);
                if (preset is null) continue;
                var name = string.IsNullOrWhiteSpace(preset.Name)
                    ? Path.GetFileNameWithoutExtension(file)
                    : preset.Name.Trim();
                list.Add((name, file, preset.ToParams()));
            }
            catch
            {
                // skip corrupt
            }
        }

        list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    public static string Save(string name, ColorGradeParams parameters)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name");

        parameters.Normalize();
        var dir = AppPaths.ColorGradesDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SanitizeFileName(name.Trim()) + ".json");
        var preset = SavedPreset.From(name.Trim(), parameters);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(preset, JsonOptions));
        File.Move(tmp, file, overwrite: true);
        return file;
    }

    public static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "grade";
        if (cleaned.Length > 64)
            cleaned = cleaned[..64].Trim();
        return cleaned;
    }
}
