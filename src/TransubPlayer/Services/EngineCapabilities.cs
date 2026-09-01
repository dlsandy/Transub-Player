using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Snapshot of embedded ASR capabilities for settings display.</summary>
internal sealed class EngineCapabilities
{
    public EngineKind Kind { get; init; } = EngineKind.Embedded;
    public string? EngineRoot { get; init; }
    public string Label { get; init; } = "";
    public bool SileroVad { get; init; }
    public bool DetectLanguage { get; init; } = true;
    public bool LiveProbed { get; init; }
    public bool AsrGpuReady { get; init; }
    public bool HasCuda { get; init; }
    public string? EngineVersion { get; init; }
    public string? GpuName { get; init; }
    public IReadOnlyList<string> VadBackends { get; init; } = [];
    public IReadOnlyList<string> AsrBackends { get; init; } = [];

    public bool IsEmbedded => Kind == EngineKind.Embedded;

    public static EngineCapabilities ForEmbedded(string modelsRoot)
        => new()
        {
            Kind = EngineKind.Embedded,
            EngineRoot = modelsRoot,
            Label = Loc.Get("Settings.Engine.Embedded"),
            SileroVad = false,
            DetectLanguage = true,
            LiveProbed = true,
            AsrGpuReady = false,
            HasCuda = false,
        };

    public string FormatStatusLine()
    {
        var bits = new List<string> { Label };
        bits.Add(SileroVad
            ? Loc.Get("Settings.EngineCaps.VadOn")
            : Loc.Get("Settings.EngineCaps.VadOffLite"));

        if (LiveProbed)
        {
            bits.Add(AsrGpuReady
                ? Loc.Get("Settings.EngineCaps.GpuReady")
                : Loc.Get("Settings.EngineCaps.GpuCpu"));
            if (!string.IsNullOrWhiteSpace(EngineVersion))
                bits.Add("v" + EngineVersion);
        }

        return string.Join(" · ", bits);
    }
}
