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

/// <summary>Sidecar next to media: path plus optional language/role tag (e.g. zh-cn, en).</summary>
internal readonly record struct SidecarInfo(string Path, string Tag, string Extension);

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

/// <summary>Control-bar / menu entry for one local sidecar file.</summary>
internal sealed class LocalSidecarEntry
{
    public string Id { get; }
    public string Path { get; }
    public string Name { get; }
    public string Tag { get; }

    public LocalSidecarEntry(string path, string name, string tag)
    {
        Path = path;
        Id = path;
        Name = name;
        Tag = tag;
    }

    public override string ToString() => Name;
}
