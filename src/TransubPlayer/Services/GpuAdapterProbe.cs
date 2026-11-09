using System.Runtime.InteropServices;
using System.Text;

namespace TransubPlayer.Services;

/// <summary>
/// Enumerates DXGI adapters and prefers a discrete GPU for mpv D3D11 / Whisper Vulkan.
/// Soft-fails to null / device 0 when DXGI is unavailable.
/// </summary>
internal static class GpuAdapterProbe
{
    private const uint VendorNvidia = 0x10DE;
    private const uint VendorAmd = 0x1002;
    private const uint VendorIntel = 0x8086;
    private const uint DxgiAdapterFlagSoftware = 2;

    private static readonly object Gate = new();
    private static bool _probed;
    private static GpuAdapterInfo? _preferredDiscrete;
    private static IReadOnlyList<GpuAdapterInfo> _adapters = Array.Empty<GpuAdapterInfo>();

    /// <summary>First discrete (NVIDIA/AMD) hardware adapter, or null if none / probe failed.</summary>
    public static GpuAdapterInfo? PreferredDiscrete
    {
        get
        {
            EnsureProbed();
            return _preferredDiscrete;
        }
    }

    /// <summary>All hardware adapters from the last probe (empty on failure).</summary>
    public static IReadOnlyList<GpuAdapterInfo> Adapters
    {
        get
        {
            EnsureProbed();
            return _adapters;
        }
    }

    /// <summary>
    /// Adapter description suitable for mpv <c>--d3d11-adapter=</c>, or null to leave system default.
    /// Prefers a short vendor token (NVIDIA / AMD) when the full description is awkward.
    /// </summary>
    public static string? PreferredMpvD3d11Adapter()
    {
        var discrete = PreferredDiscrete;
        if (discrete is null)
            return null;

        // mpv matches from the start of the DXGI description; vendor token is enough and stable.
        if (discrete.VendorId == VendorNvidia)
            return "NVIDIA";
        if (discrete.VendorId == VendorAmd)
        {
            // AMD descriptions vary (AMD / Radeon / ATI); prefer a substring that usually matches.
            if (discrete.Description.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                return "AMD";
            if (discrete.Description.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                return "Radeon";
            return discrete.Description;
        }

        return discrete.Description;
    }

    /// <summary>
    /// Whisper.net <see cref="Whisper.net.WhisperFactoryOptions.GpuDevice"/> index.
    /// ggml-vulkan lists discrete devices first, then iGPU — so 0 is the preferred discrete when present.
    /// </summary>
    public static int PreferredWhisperGpuDevice()
    {
        // Discrete present → ggml device 0 is that GPU. Intel-only → still 0 (only choice).
        _ = PreferredDiscrete;
        return 0;
    }

    /// <summary>One-line diagnostic for logs (adapters + preferred).</summary>
    public static string DescribeForLog()
    {
        EnsureProbed();
        if (_adapters.Count == 0)
            return "DXGI adapters: (none / probe failed)";

        var sb = new StringBuilder("DXGI adapters:");
        for (var i = 0; i < _adapters.Count; i++)
        {
            var a = _adapters[i];
            sb.Append(' ').Append(i).Append('=')
                .Append(a.Description)
                .Append(a.IsDiscrete ? "[d]" : a.IsIntel ? "[i]" : "");
        }

        if (_preferredDiscrete is not null)
            sb.Append(" · prefer=").Append(_preferredDiscrete.Description);
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
                _adapters = EnumerateAdapters();
                _preferredDiscrete = _adapters.FirstOrDefault(a => a.IsDiscrete);
            }
            catch
            {
                _adapters = Array.Empty<GpuAdapterInfo>();
                _preferredDiscrete = null;
            }

            _probed = true;
        }
    }

    private static IReadOnlyList<GpuAdapterInfo> EnumerateAdapters()
    {
        var hr = CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factoryObj);
        if (hr < 0 || factoryObj is null)
            return Array.Empty<GpuAdapterInfo>();

        try
        {
            var factory = (IDXGIFactory1)factoryObj;
            var list = new List<GpuAdapterInfo>();
            for (uint i = 0; ; i++)
            {
                hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == unchecked((int)0x887A0002) /* DXGI_ERROR_NOT_FOUND */)
                    break;
                if (hr < 0 || adapter is null)
                    break;

                try
                {
                    adapter.GetDesc1(out var desc);
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0)
                        continue;
                    // Skip the Microsoft Basic Render Driver even if flag is missing on odd drivers.
                    var name = desc.Description?.Trim() ?? "";
                    if (name.Length == 0
                        || name.Contains("Microsoft Basic Render", StringComparison.OrdinalIgnoreCase))
                        continue;

                    list.Add(new GpuAdapterInfo(name, desc.VendorId, desc.DeviceId));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            return list;
        }
        finally
        {
            Marshal.ReleaseComObject(factoryObj);
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object? ppFactory);

    private static int CreateDXGIFactory1(Guid riid, out object? ppFactory)
        => CreateDXGIFactory1(ref riid, out ppFactory);

    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIFactory / IDXGIObject helpers we do not call — keep vtable slots.
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();

        [PreserveSig]
        int EnumAdapters1(uint adapter, out IDXGIAdapter1? ppAdapter);

        [PreserveSig]
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();

        [PreserveSig]
        int GetDesc1(out DxgiAdapterDesc1 pDesc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}

/// <summary>One DXGI hardware adapter.</summary>
internal sealed record GpuAdapterInfo(string Description, uint VendorId, uint DeviceId)
{
    public bool IsNvidia => VendorId == 0x10DE;
    public bool IsAmd => VendorId == 0x1002;
    public bool IsIntel => VendorId == 0x8086;
    public bool IsDiscrete => IsNvidia || IsAmd;
}
