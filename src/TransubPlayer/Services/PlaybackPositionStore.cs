using System.Text.Json;
using System.Text.Json.Serialization;

namespace TransubPlayer.Services;

/// <summary>Per-media resume + how the user last watched (mode / delay / source lang).</summary>
internal sealed class MediaSessionPrefs
{
    public double Position { get; set; }
    public string? SourceLanguage { get; set; }
    /// <summary>Last short-window / folder-inherited source lang while settings stayed <c>auto</c>.</summary>
    public string? SensedSourceLanguage { get; set; }
    public string? SubtitleMode { get; set; }
    public double? SubDelaySec { get; set; }

    public bool HasViewPrefs
        => !string.IsNullOrWhiteSpace(SourceLanguage)
           || !string.IsNullOrWhiteSpace(SensedSourceLanguage)
           || !string.IsNullOrWhiteSpace(SubtitleMode)
           || SubDelaySec is not null;

    public bool IsEmpty
        => Position <= 1 && !HasViewPrefs;
}

/// <summary>
/// <c>data/resume.json</c>: path → position (legacy number) or rich <see cref="MediaSessionPrefs"/>.
/// </summary>
internal static class PlaybackPositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static double Load(string mediaPath)
        => LoadPrefs(mediaPath).Position;

    public static MediaSessionPrefs LoadPrefs(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            return new MediaSessionPrefs();
        var map = ReadMap();
        return map.TryGetValue(NormalizeKey(mediaPath), out var prefs)
            ? prefs
            : new MediaSessionPrefs();
    }

    public static void Save(string mediaPath, double seconds)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return;
        var prefs = LoadPrefs(mediaPath);
        if (seconds <= 1 || !double.IsFinite(seconds))
            prefs.Position = 0;
        else
            prefs.Position = Math.Round(seconds, 1);
        WritePrefs(mediaPath, prefs);
    }

    /// <summary>Drop the whole resume entry for a media path (position + view prefs).</summary>
    public static void Remove(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return;
        var map = ReadMap();
        if (!map.Remove(NormalizeKey(mediaPath)))
            return;
        WriteMap(map);
    }

    public static void SavePrefs(string mediaPath, MediaSessionPrefs prefs)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || prefs is null) return;
        WritePrefs(mediaPath, prefs);
    }

    /// <summary>Merge view fields onto existing entry (keeps position unless overwritten).</summary>
    public static void UpdateViewPrefs(
        string mediaPath,
        string? sourceLanguage,
        string? subtitleMode,
        double? subDelaySec,
        string? sensedSourceLanguage = null)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return;
        var prefs = LoadPrefs(mediaPath);
        if (!string.IsNullOrWhiteSpace(sourceLanguage))
            prefs.SourceLanguage = SourceLanguages.Normalize(sourceLanguage);
        if (!string.IsNullOrWhiteSpace(subtitleMode))
            prefs.SubtitleMode = SubtitleDisplayModeUtil.ToSetting(
                SubtitleDisplayModeUtil.Parse(subtitleMode));
        if (subDelaySec is double d && double.IsFinite(d))
            prefs.SubDelaySec = Math.Clamp(Math.Round(d, 1), -30, 30);
        if (sensedSourceLanguage is not null)
        {
            var n = SourceLanguages.Normalize(sensedSourceLanguage);
            prefs.SensedSourceLanguage = SourceLanguages.IsAuto(n) ? null : n;
        }

        WritePrefs(mediaPath, prefs);
    }

    /// <summary>
    /// If siblings in the same folder share one concrete sensed language, return it; else null.
    /// </summary>
    public static string? FindFolderSensedLanguage(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath) || MediaSourceHelper.IsNonLocalMedia(mediaPath))
            return null;

        string dir;
        string selfKey;
        try
        {
            selfKey = NormalizeKey(mediaPath);
            dir = Path.GetDirectoryName(selfKey) ?? "";
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dir))
            return null;

        string? agreed = null;
        foreach (var (key, prefs) in ReadMap())
        {
            if (string.Equals(key, selfKey, StringComparison.OrdinalIgnoreCase))
                continue;
            string? otherDir;
            try { otherDir = Path.GetDirectoryName(key); }
            catch { continue; }
            if (!string.Equals(otherDir, dir, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(prefs.SensedSourceLanguage))
                continue;
            var n = SourceLanguages.Normalize(prefs.SensedSourceLanguage);
            if (SourceLanguages.IsAuto(n))
                continue;

            if (agreed is null)
                agreed = n;
            else if (!SourceLanguages.EqualsLang(agreed, n))
                return null;
        }

        return agreed;
    }

    private static void WritePrefs(string mediaPath, MediaSessionPrefs prefs)
    {
        var map = ReadMap();
        var key = NormalizeKey(mediaPath);
        if (prefs.IsEmpty)
            map.Remove(key);
        else
            map[key] = prefs;
        WriteMap(map);
    }

    private static Dictionary<string, MediaSessionPrefs> ReadMap()
    {
        try
        {
            var path = AppPaths.ResumePath;
            if (!File.Exists(path))
                return new Dictionary<string, MediaSessionPrefs>(StringComparer.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, MediaSessionPrefs>(StringComparer.OrdinalIgnoreCase);

            var map = new Dictionary<string, MediaSessionPrefs>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number
                    && prop.Value.TryGetDouble(out var pos))
                {
                    map[prop.Name] = new MediaSessionPrefs { Position = pos };
                    continue;
                }

                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;

                var prefs = new MediaSessionPrefs();
                if (prop.Value.TryGetProperty("position", out var p) && p.TryGetDouble(out var pos2))
                    prefs.Position = pos2;
                // Legacy camelCase / PascalCase
                if (prefs.Position <= 0
                    && prop.Value.TryGetProperty("Position", out var p2)
                    && p2.TryGetDouble(out var pos3))
                    prefs.Position = pos3;

                if (TryGetString(prop.Value, "sourceLanguage", "SourceLanguage", out var lang))
                    prefs.SourceLanguage = lang;
                if (TryGetString(prop.Value, "sensedSourceLanguage", "SensedSourceLanguage", out var sensed))
                    prefs.SensedSourceLanguage = sensed;
                if (TryGetString(prop.Value, "subtitleMode", "SubtitleMode", out var mode))
                    prefs.SubtitleMode = mode;
                if (TryGetDouble(prop.Value, "subDelaySec", "SubDelaySec", out var delay))
                    prefs.SubDelaySec = delay;

                map[prop.Name] = prefs;
            }

            return map;
        }
        catch
        {
            return new Dictionary<string, MediaSessionPrefs>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool TryGetString(JsonElement obj, string camel, string pascal, out string? value)
    {
        value = null;
        if (obj.TryGetProperty(camel, out var a) && a.ValueKind == JsonValueKind.String)
        {
            value = a.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        if (obj.TryGetProperty(pascal, out var b) && b.ValueKind == JsonValueKind.String)
        {
            value = b.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string camel, string pascal, out double value)
    {
        value = 0;
        if (obj.TryGetProperty(camel, out var a) && a.TryGetDouble(out value))
            return true;
        if (obj.TryGetProperty(pascal, out var b) && b.TryGetDouble(out value))
            return true;
        return false;
    }

    private static void WriteMap(Dictionary<string, MediaSessionPrefs> map)
    {
        try
        {
            var path = AppPaths.ResumePath;
            File.WriteAllText(path, JsonSerializer.Serialize(map, JsonOptions));
        }
        catch
        {
            // ignore
        }
    }

    private static string NormalizeKey(string mediaPath)
    {
        if (MediaSourceHelper.IsNonLocalMedia(mediaPath))
        {
            if (MediaSourceHelper.TryNormalizeMedia(mediaPath, out var normalized))
                return normalized;
            return mediaPath.Trim();
        }

        return Path.GetFullPath(mediaPath);
    }
}
