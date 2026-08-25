using System.Text.Json;

namespace TransubPlayer.Services;

internal static class PlaybackPositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static double Load(string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return 0;
        var map = ReadMap();
        return map.TryGetValue(NormalizeKey(mediaPath), out var pos) && pos > 1 ? pos : 0;
    }

    public static void Save(string mediaPath, double seconds)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return;
        var map = ReadMap();
        var key = NormalizeKey(mediaPath);
        if (seconds <= 1 || !double.IsFinite(seconds))
            map.Remove(key);
        else
            map[key] = Math.Round(seconds, 1);
        WriteMap(map);
    }

    private static Dictionary<string, double> ReadMap()
    {
        try
        {
            var path = AppPaths.ResumePath;
            if (!File.Exists(path)) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions)
                   ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteMap(Dictionary<string, double> map)
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
