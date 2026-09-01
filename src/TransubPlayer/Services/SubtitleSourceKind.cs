namespace TransubPlayer.Services;

/// <summary>Control-bar subtitle source (not 译文/原文/双语 layout).</summary>
internal enum SubtitleSourceKind
{
    Off,
    Online,
    Local,
    Live,
}

internal enum ExternalSubOrigin
{
    None,
    Local,
    Online,
}

internal readonly record struct SidecarFingerprint(long Length, long LastWriteTicks);

internal sealed class SubtitleSourceEntry
{
    public SubtitleSourceKind Kind { get; }
    public string Id { get; }
    public string Name { get; }
    public bool Available { get; }

    public SubtitleSourceEntry(SubtitleSourceKind kind, string name, bool available = true)
    {
        Kind = kind;
        Id = kind.ToString();
        Name = name;
        Available = available;
    }
}
