namespace TransubPlayer.Services;

/// <summary>
/// Disk integrity for ASR model folders. Hugging Face leaves <c>*.incomplete</c> blobs when a
/// download is interrupted; those must not count as installed (Player used to treat any &gt;1MB file as OK).
/// </summary>
internal static class AsrModelIntegrity
{
    private static readonly string[] WeightMarkers =
    [
        "model.bin",
        "model.pt",
        "model.safetensors",
        "pytorch_model.bin",
        "model.onnx",
        "encoder.pt",
        "model.pth.tar",
        "model.safetensors.index.json",
        "encoder-epoch-99-avg-1.onnx",
        "encoder-epoch-99-avg-1.int8.onnx",
    ];

    /// <summary>Hub residue or an in-flight download — must not count as installed.</summary>
    public static bool HasIncompleteArtifacts(string modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories))
            {
                if (IsIncompletePath(file))
                    return true;
            }
        }
        catch
        {
            // unreadable tree — treat as incomplete
            return true;
        }

        return false;
    }

    /// <summary>Folder exists with partial content but fails <see cref="IsComplete"/>.</summary>
    public static bool IsPartiallyPresent(string modelDir, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;
        if (IsComplete(modelDir, modelId))
            return false;
        return HasIncompleteArtifacts(modelDir) || MeaningfulBytes(modelDir) > 0;
    }

    public static bool IsComplete(string modelDir, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir))
            return false;

        if (HasIncompleteArtifacts(modelDir))
            return false;

        if (!TryFindWeight(modelDir, out var weightBytes))
            return false;

        var minTotal = AsrModelCatalog.MinCompleteBytes(modelId);
        if (minTotal > 0)
        {
            var total = MeaningfulBytes(modelDir);
            if (total < minTotal)
                return false;

            // Single-file CT2 / safetensors: the weight file must reach the size floor too.
            // Sharded installs use index.json (small) + sibling shards counted in MeaningfulBytes.
            if (HasShardedIndex(modelDir))
            {
                if (weightBytes < 64 * 1024)
                    return false;
            }
            else if (weightBytes < minTotal)
            {
                return false;
            }
        }
        else if (weightBytes < 1024)
        {
            return false;
        }

        return RequiredSidecarsPresent(modelDir, modelId);
    }

    private static bool TryFindWeight(string modelDir, out long weightBytes)
    {
        weightBytes = 0;
        try
        {
            foreach (var marker in WeightMarkers)
            {
                var path = Path.Combine(modelDir, marker);
                if (!File.Exists(path) || IsIncompletePath(path))
                    continue;
                var len = new FileInfo(path).Length;
                var min = marker.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 64 : 1024;
                if (len < min) continue;
                weightBytes = len;
                return true;
            }

            // FireRed nested layout / NeMo checkpoints.
            var nested = Path.Combine(modelDir, "VAD", "model.pth.tar");
            if (File.Exists(nested) && new FileInfo(nested).Length > 1024)
            {
                weightBytes = new FileInfo(nested).Length;
                return true;
            }

            foreach (var nemo in Directory.EnumerateFiles(modelDir, "*.nemo", SearchOption.TopDirectoryOnly))
            {
                if (IsIncompletePath(nemo)) continue;
                var len = new FileInfo(nemo).Length;
                if (len <= 1024) continue;
                weightBytes = len;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasShardedIndex(string modelDir)
        => File.Exists(Path.Combine(modelDir, "model.safetensors.index.json"));

    private static bool RequiredSidecarsPresent(string modelDir, string modelId)
    {
        // Match Transub engine: Qwen / Cohere need config (+ tokenizer for Cohere).
        if (modelId.Contains("qwen3-asr", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("qwen3-align", StringComparison.OrdinalIgnoreCase))
        {
            return FileLooksPresent(Path.Combine(modelDir, "config.json"), 16);
        }

        if (modelId.Contains("cohere", StringComparison.OrdinalIgnoreCase))
        {
            return FileLooksPresent(Path.Combine(modelDir, "config.json"), 16)
                   && FileLooksPresent(Path.Combine(modelDir, "preprocessor_config.json"), 16)
                   && FileLooksPresent(Path.Combine(modelDir, "tokenizer_config.json"), 16)
                   && FileLooksPresent(Path.Combine(modelDir, "tokenizer.json"), 16);
        }

        return true;
    }

    private static bool FileLooksPresent(string path, long minBytes)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= minBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sum file sizes, skipping Hub incomplete / temp residue and cache metadata.</summary>
    public static long MeaningfulBytes(string modelDir)
        => SumBytes(modelDir, includeInFlight: false);

    /// <summary>
    /// Bytes on disk for download progress UI. Includes <c>*.incomplete</c> / <c>*.partial</c>
    /// so Hugging Face in-flight weights keep the bar moving (MeaningfulBytes freezes after sidecars).
    /// </summary>
    public static long DownloadProgressBytes(string modelDir)
        => SumBytes(modelDir, includeInFlight: true);

    private static long SumBytes(string modelDir, bool includeInFlight)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(modelDir, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (IsIgnorableName(name))
                    continue;
                if (!includeInFlight && IsIncompletePath(file))
                    continue;
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // skip locked/unreadable
                }
            }
        }
        catch
        {
            return total;
        }

        return total;
    }

    public static bool IsIncompletePath(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return true;
        if (name.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsIgnorableName(string name)
    {
        if (name.StartsWith('.')) return true;
        if (name.Equals("CACHEDIR.TAG", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".metadata", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
