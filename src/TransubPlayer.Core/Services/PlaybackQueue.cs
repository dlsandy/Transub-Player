namespace TransubPlayer.Services;

internal sealed class PlaybackQueue
{
    private static readonly HashSet<string> MediaExt = new(MediaFileTypes.AllExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PlaybackExt = new(MediaFileTypes.PlaybackExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SubtitleExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub",
    };

    private readonly List<string> _items = [];

    public event Action? Changed;

    public IReadOnlyList<string> Items => _items;
    public int Index { get; private set; } = -1;
    public int Count => _items.Count;
    public string? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;
    public bool HasNext => Index >= 0 && Index + 1 < _items.Count;
    public bool HasPrev => Index > 0;

    public void Replace(IEnumerable<string> paths)
    {
        _items.Clear();
        foreach (var path in CollectMedia(paths))
            _items.Add(path);
        Index = _items.Count == 0 ? -1 : 0;
        Changed?.Invoke();
    }

    public void Append(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in CollectMedia(paths))
        {
            if (_items.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            _items.Add(path);
            added++;
        }

        if (Index < 0 && _items.Count > 0)
            Index = 0;
        if (added > 0)
            Changed?.Invoke();
    }

    public bool TryActivate(int index)
    {
        if (index < 0 || index >= _items.Count) return false;
        Index = index;
        Changed?.Invoke();
        return true;
    }

    public bool TryMoveNext()
    {
        if (!HasNext) return false;
        Index++;
        Changed?.Invoke();
        return true;
    }

    public bool TryMovePrev()
    {
        if (!HasPrev) return false;
        Index--;
        Changed?.Invoke();
        return true;
    }

    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items.RemoveAt(index);
        if (_items.Count == 0)
            Index = -1;
        else if (Index >= _items.Count)
            Index = _items.Count - 1;
        else if (Index > index)
            Index--;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        Index = -1;
        Changed?.Invoke();
    }

    public static IReadOnlyList<string> CollectMedia(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (MediaSourceHelper.TryNormalizeMedia(raw, out var remote))
            {
                AddUnique(result, remote);
                continue;
            }

            string path;
            try { path = Path.GetFullPath(raw); }
            catch { continue; }

            if (File.Exists(path) && PlaylistFileParser.IsPlaylist(path))
            {
                foreach (var entry in PlaylistFileParser.Expand(path))
                {
                    if (IsPlayableMedia(entry))
                        AddUnique(result, entry);
                }
                continue;
            }

            if (File.Exists(path) && IsPlayableMedia(path))
            {
                AddUnique(result, path);
                continue;
            }

            if (File.Exists(path) && TryResolveSubtitleSibling(path, out var sibling))
            {
                AddUnique(result, sibling);
                continue;
            }

            if (!Directory.Exists(path)) continue;
            try
            {
                var files = Directory.EnumerateFiles(path)
                    .Select(f =>
                    {
                        try { return Path.GetFullPath(f); }
                        catch { return null; }
                    })
                    .Where(f => f is not null)
                    .Cast<string>()
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var full in files)
                {
                    if (PlaylistFileParser.IsPlaylist(full))
                    {
                        foreach (var entry in PlaylistFileParser.Expand(full))
                        {
                            if (IsPlayableMedia(entry))
                                AddUnique(result, entry);
                        }
                    }
                    else if (IsPlayableMedia(full))
                    {
                        AddUnique(result, full);
                    }
                }
            }
            catch
            {
                // skip unreadable folders
            }
        }

        return result;
    }

    /// <summary>Same-directory video/audio siblings; opened file first, then sorted by file name.</summary>
    public static IReadOnlyList<string> CollectSameFolderPlaylist(string primaryPath)
    {
        string primaryFull;
        try { primaryFull = Path.GetFullPath(primaryPath); }
        catch { return [primaryPath]; }

        if (!File.Exists(primaryFull) || !IsPlaybackMedia(primaryFull))
            return [primaryFull];

        var dir = Path.GetDirectoryName(primaryFull);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return [primaryFull];

        var siblings = new List<string> { primaryFull };
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                string full;
                try { full = Path.GetFullPath(file); }
                catch { continue; }

                if (string.Equals(full, primaryFull, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsPlaybackMedia(full))
                    continue;
                AddUnique(siblings, full);
            }
        }
        catch
        {
            return [primaryFull];
        }

        if (siblings.Count <= 1)
            return siblings;

        var rest = siblings.Skip(1).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList();
        rest.Insert(0, primaryFull);
        return rest;
    }

    private static bool IsPlaybackMedia(string path)
    {
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path);
        return PlaybackExt.Contains(ext) && !PlaylistFileParser.IsPlaylist(path);
    }

    private static bool IsPlayableMedia(string path)
    {
        var ext = Path.GetExtension(path);
        return MediaExt.Contains(ext) && !PlaylistFileParser.IsPlaylist(path);
    }

    private static bool TryResolveSubtitleSibling(string subtitlePath, out string mediaPath)
    {
        mediaPath = "";
        if (!SubtitleExt.Contains(Path.GetExtension(subtitlePath))) return false;

        var dir = Path.GetDirectoryName(subtitlePath);
        if (string.IsNullOrWhiteSpace(dir)) return false;

        var stem = Path.GetFileNameWithoutExtension(subtitlePath);
        foreach (var ext in MediaFileTypes.VideoExtensions)
        {
            if (SubtitleExt.Contains(ext)) continue;
            var candidate = Path.Combine(dir, stem + ext);
            if (!File.Exists(candidate)) continue;
            mediaPath = Path.GetFullPath(candidate);
            return true;
        }

        return false;
    }

    private static void AddUnique(List<string> into, string path)
    {
        if (!into.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            into.Add(path);
    }
}
