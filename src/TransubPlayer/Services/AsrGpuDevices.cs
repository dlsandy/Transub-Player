using System.Globalization;
using TransubPlayer.Localization;

namespace TransubPlayer.Services;

/// <summary>Whisper Vulkan GPU preference: <c>auto</c> or a Vulkan physical-device index string.</summary>
internal static class AsrGpuDevices
{
    public const string Auto = "auto";

    public static string Normalize(string? raw)
    {
        var id = (raw ?? "").Trim();
        if (id.Length == 0 || string.Equals(id, Auto, StringComparison.OrdinalIgnoreCase))
            return Auto;
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) && idx >= 0)
            return idx.ToString(CultureInfo.InvariantCulture);
        return Auto;
    }

    public static string AutoDisplayName()
        => Loc.Get("Settings.AsrGpuDevice.Auto");

    public static string DeviceDisplayName(VulkanDeviceInfo device)
    {
        var vendor = device.IsNvidia ? "NVIDIA"
            : device.IsAmd ? "AMD"
            : device.IsIntel ? "Intel"
            : $"0x{device.VendorId:X}";
        return $"{device.Name} ({vendor})";
    }

    /// <summary>True when the settings UI should offer a GPU picker (2+ Vulkan devices).</summary>
    public static bool ShouldShowPicker()
        => VulkanGpuProbe.Devices.Count > 1;
}
