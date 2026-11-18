using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Whisper.cpp native backend preference for embedded ASR.</summary>
internal static class AsrBackends
{
    public const string Auto = "auto";
    public const string Cpu = "cpu";
    public const string Vulkan = "vulkan";
    public const string CoreMl = "coreml";

    public static IReadOnlyList<string> Selectable { get; } =
        OperatingSystem.IsMacOS() ? [Auto, Cpu, CoreMl] : [Auto, Cpu, Vulkan];

    public static string Normalize(string? raw)
    {
        var id = (raw ?? "").Trim();
        if (string.Equals(id, Cpu, StringComparison.OrdinalIgnoreCase)) return Cpu;
        if (string.Equals(id, Vulkan, StringComparison.OrdinalIgnoreCase)) return Vulkan;
        if (string.Equals(id, CoreMl, StringComparison.OrdinalIgnoreCase)) return CoreMl;
        return Auto;
    }

    public static string DisplayName(string? raw)
        => Normalize(raw) switch
        {
            Cpu => Loc.Get("Settings.AsrBackend.Cpu"),
            Vulkan => Loc.Get("Settings.AsrBackend.Vulkan"),
            CoreMl => Loc.Get("Settings.AsrBackend.CoreMl"),
            _ => Loc.Get("Settings.AsrBackend.Auto"),
        };
}
