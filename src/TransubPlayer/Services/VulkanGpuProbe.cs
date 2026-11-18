using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>
/// Enumerates Vulkan physical devices so ASR can avoid Intel Arc while QSV owns it.
/// Soft-fails to an empty list when vulkan-1.dll is missing.
/// </summary>
internal static class VulkanGpuProbe
{
    private const uint VendorNvidia = 0x10DE;
    private const uint VendorAmd = 0x1002;
    private const uint VendorIntel = 0x8086;
    private const int VkStructureTypeInstanceCreateInfo = 1;
    private const int PropsBufferBytes = 8192;

    private static readonly object Gate = new();
    private static bool _probed;
    private static IReadOnlyList<VulkanDeviceInfo> _devices = Array.Empty<VulkanDeviceInfo>();
    private static bool _filterApplied;
    private static int? _appliedVulkanIndex;

    public static IReadOnlyList<VulkanDeviceInfo> Devices
    {
        get
        {
            EnsureProbed();
            return _devices;
        }
    }

    /// <summary>
    /// Default ASR device when preference is <c>auto</c>: NVIDIA, else AMD, else first non-Intel, else first.
    /// </summary>
    public static VulkanDeviceInfo? PreferredAsrDevice
    {
        get
        {
            EnsureProbed();
            return _devices.FirstOrDefault(d => d.VendorId == VendorNvidia)
                   ?? _devices.FirstOrDefault(d => d.VendorId == VendorAmd)
                   ?? _devices.FirstOrDefault(d => d.VendorId != VendorIntel)
                   ?? _devices.FirstOrDefault();
        }
    }

    public static bool HasIntelGpu
    {
        get
        {
            EnsureProbed();
            return _devices.Any(d => d.VendorId == VendorIntel);
        }
    }

    public static bool HasNonIntelGpu
    {
        get
        {
            EnsureProbed();
            return _devices.Any(d => d.VendorId != VendorIntel);
        }
    }

    /// <summary>Resolve settings preference (<c>auto</c> or Vulkan index) to a physical device.</summary>
    public static VulkanDeviceInfo? ResolveDevice(string? preference)
    {
        EnsureProbed();
        if (_devices.Count == 0)
            return null;

        var pref = AsrGpuDevices.Normalize(preference);
        if (pref != AsrGpuDevices.Auto
            && int.TryParse(pref, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
        {
            var hit = _devices.FirstOrDefault(d => d.Index == idx);
            if (hit is not null)
                return hit;
        }

        return PreferredAsrDevice;
    }

    /// <summary>
    /// Restrict ggml-vulkan to the chosen device via <c>GGML_VK_VISIBLE_DEVICES</c>.
    /// Call before first Whisper GPU factory. After filtering, Whisper <c>GpuDevice</c> is 0.
    /// Sticky for the process — changing preference later needs a player restart.
    /// </summary>
    public static int ApplyAsrDeviceFilter(string? preference = null)
    {
        EnsureProbed();
        var target = ResolveDevice(preference);
        if (target is null)
            return 0;

        if (!_filterApplied)
        {
            Environment.SetEnvironmentVariable(
                "GGML_VK_VISIBLE_DEVICES",
                target.Index.ToString(CultureInfo.InvariantCulture));
            _filterApplied = true;
            _appliedVulkanIndex = target.Index;
        }

        return 0;
    }

    /// <summary>True when env filter already pinned a different Vulkan index than <paramref name="preference"/>.</summary>
    public static bool NeedsRestartForDevice(string? preference)
    {
        if (!_filterApplied || _appliedVulkanIndex is null)
            return false;
        var target = ResolveDevice(preference);
        return target is not null && target.Index != _appliedVulkanIndex.Value;
    }

    public static string DescribeForLog(string? preference = null)
    {
        EnsureProbed();
        if (_devices.Count == 0)
            return "Vulkan devices: (none / probe failed)";

        var sb = new StringBuilder("Vulkan devices:");
        for (var i = 0; i < _devices.Count; i++)
        {
            var d = _devices[i];
            sb.Append(' ').Append(d.Index).Append('=')
                .Append(d.Name)
                .Append("[0x").Append(d.VendorId.ToString("X", CultureInfo.InvariantCulture)).Append(']');
        }

        var best = ResolveDevice(preference);
        if (best is not null)
        {
            sb.Append(" · ASR prefer=").Append(best.Name).Append("(#").Append(best.Index).Append(')');
            var pref = AsrGpuDevices.Normalize(preference);
            if (pref != AsrGpuDevices.Auto)
                sb.Append(" · setting=").Append(pref);
            else
                sb.Append(" · setting=auto");
        }

        if (_filterApplied && _appliedVulkanIndex is not null)
            sb.Append(" · pinned=#").Append(_appliedVulkanIndex.Value);
        return sb.ToString();
    }

    private static void EnsureProbed()
    {
        if (_probed)
            return;
        lock (Gate)
        {
            if (_probed)
                return;
            try
            {
                _devices = EnumerateDevices();
            }
            catch
            {
                _devices = Array.Empty<VulkanDeviceInfo>();
            }

            _probed = true;
        }
    }

    private static IReadOnlyList<VulkanDeviceInfo> EnumerateDevices()
    {
        var createInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureTypeInstanceCreateInfo,
        };
        var hr = vkCreateInstance(ref createInfo, IntPtr.Zero, out var instance);
        if (hr != 0 || instance == IntPtr.Zero)
            return Array.Empty<VulkanDeviceInfo>();

        try
        {
            uint count = 0;
            hr = vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero);
            if (hr != 0 || count == 0)
                return Array.Empty<VulkanDeviceInfo>();

            var handlesPtr = Marshal.AllocHGlobal(IntPtr.Size * (int)count);
            var propsBuf = Marshal.AllocHGlobal(PropsBufferBytes);
            try
            {
                hr = vkEnumeratePhysicalDevices(instance, ref count, handlesPtr);
                if (hr != 0)
                    return Array.Empty<VulkanDeviceInfo>();

                var list = new List<VulkanDeviceInfo>((int)count);
                for (var i = 0; i < (int)count; i++)
                {
                    var handle = Marshal.ReadIntPtr(handlesPtr, i * IntPtr.Size);
                    for (var z = 0; z < PropsBufferBytes; z += 8)
                        Marshal.WriteInt64(propsBuf, z, 0);

                    vkGetPhysicalDeviceProperties(handle, propsBuf);
                    var vendorId = unchecked((uint)Marshal.ReadInt32(propsBuf, 8));
                    var deviceId = unchecked((uint)Marshal.ReadInt32(propsBuf, 12));
                    var deviceType = unchecked((uint)Marshal.ReadInt32(propsBuf, 16));
                    var name = Marshal.PtrToStringAnsi(propsBuf + 20) ?? "";
                    if (string.IsNullOrWhiteSpace(name))
                        name = $"GPU-{i}";
                    list.Add(new VulkanDeviceInfo(i, name.Trim(), vendorId, deviceId, deviceType));
                }

                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(propsBuf);
                Marshal.FreeHGlobal(handlesPtr);
            }
        }
        finally
        {
            vkDestroyInstance(instance, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public int sType;
        public IntPtr pNext;
        public uint flags;
        public IntPtr pApplicationInfo;
        public uint enabledLayerCount;
        public IntPtr ppEnabledLayerNames;
        public uint enabledExtensionCount;
        public IntPtr ppEnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int vkCreateInstance(
        ref VkInstanceCreateInfo pCreateInfo,
        IntPtr pAllocator,
        out IntPtr pInstance);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr pAllocator);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int vkEnumeratePhysicalDevices(
        IntPtr instance,
        ref uint pPhysicalDeviceCount,
        IntPtr pPhysicalDevices);

    [DllImport("vulkan-1.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void vkGetPhysicalDeviceProperties(
        IntPtr physicalDevice,
        IntPtr pProperties);
}

/// <summary>One Vulkan physical device (unfiltered enumerate order).</summary>
internal sealed record VulkanDeviceInfo(
    int Index,
    string Name,
    uint VendorId,
    uint DeviceId,
    uint DeviceType)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public bool IsAmd => VendorId == 0x1002;
    public bool IsIntel => VendorId == 0x8086;
}
