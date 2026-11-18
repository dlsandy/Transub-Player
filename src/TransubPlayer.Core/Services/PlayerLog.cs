namespace TransubPlayer.Services;

internal static class PlayerLog
{
    private const int RingCapacity = 800;
    private static readonly object Gate = new();
    private static readonly List<string> EngineRing = new(RingCapacity);

    public static string EngineLogPath => Path.Combine(AppPaths.LogsDir, "engine.log");

    public static void Write(string line)
    {
        AppendLine("player.log", line);
    }

  /// <summary>Engine process and ASR session diagnostics (also mirrored to player.log).</summary>
    public static void WriteEngine(string line)
    {
        AppendLine("engine.log", line);
        AppendLine("player.log", "[引擎] " + line);
        PushEngineRing(line);
    }

    /// <summary>In-memory lines for this process only (UI log window). Full history stays in engine.log on disk.</summary>
    public static string ReadEngineTail()
    {
        lock (Gate)
            return FormatRingUnlocked();
    }

    public static void ClearEngine()
    {
        lock (Gate)
        {
            EngineRing.Clear();
            try
            {
                File.WriteAllText(EngineLogPath, "");
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void AppendLine(string fileName, string line)
    {
        lock (Gate)
        {
            try
            {
                var path = Path.Combine(AppPaths.LogsDir, fileName);
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void PushEngineRing(string line)
    {
        lock (Gate)
        {
            var stamped = DateTime.Now.ToString("HH:mm:ss") + " " + line;
            if (EngineRing.Count >= RingCapacity)
                EngineRing.RemoveAt(0);
            EngineRing.Add(stamped);
        }
    }

    private static string FormatRingUnlocked() =>
        string.Join(Environment.NewLine, EngineRing);
}
