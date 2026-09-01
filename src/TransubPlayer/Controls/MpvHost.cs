using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TransubPlayer.Controls;

/// <summary>
/// Native HWND host for mpv <c>--wid</c>. Avoids WindowsFormsHost, which flickers badly
/// with WindowChrome during live resize (airspace + SWP_NOCOPYBITS).
/// <para>
/// Mouse/keyboard messages land on this HWND (or mpv's child), not WPF — so we forward
/// right-click / left-click / wheel / move / keys to the app.
/// </para>
/// </summary>
internal sealed class MpvHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WmEraseBkgnd = 0x0014;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseWheel = 0x020A;
    private const int WmParentNotify = 0x0210;
    private const int WmCreate = 0x0001;
    private const int WmDestroy = 0x0002;
    private const long KeyPrevDownBit = 1L << 30;
    private const int GwlWndProc = -4;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpAsyncWindowPos = 0x4000;
    private const string ClassName = "TransubPlayer.MpvHost";

    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private IntPtr _hwnd;

    private readonly Dictionary<IntPtr, IntPtr> _childOldProcs = new();
    private WndProcDelegate? _childWndProcKeepAlive;
    private DispatcherTimer? _childHookTimer;

    public IntPtr Hwnd => _hwnd;

    /// <summary>Right mouse button released over the video surface.</summary>
    public event Action? NativeRightButtonUp;

    /// <summary>Left mouse button pressed; argument is WPF-style click count (1 or 2).</summary>
    public event Action<int>? NativeLeftButtonDown;

    /// <summary>Mouse wheel delta (multiples of 120).</summary>
    public event Action<int>? NativeMouseWheel;

    /// <summary>Mouse moved over the video surface (throttled by caller if needed).</summary>
    public event Action? NativeMouseMove;

    /// <summary>Key down on the video surface (WPF never sees these while the HWND has focus).</summary>
    public event Action<Key, bool>? NativeKeyDown;

    public void EnsureHandle()
    {
        if (_hwnd != IntPtr.Zero)
            return;
        _ = Handle;
    }

    /// <summary>mpv may create children after start — re-hook so clicks keep reaching us.</summary>
    public void HookEmbeddedChildren()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        EnumChildWindows(_hwnd, EnumChildCallback, IntPtr.Zero);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureWindowClass();
        _hwnd = CreateWindowExW(
            0,
            ClassName,
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren,
            0, 0, 1, 1,
            hwndParent.Handle,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("无法创建 mpv 宿主窗口。");

        _childWndProcKeepAlive = ChildWndProc;
        StartChildHookRetry();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        StopChildHookRetry();
        UnhookAllChildren();
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmEraseBkgnd)
        {
            handled = true;
            return new IntPtr(1);
        }

        if (msg == WmParentNotify)
        {
            var childMsg = (int)(wParam.ToInt64() & 0xFFFF);
            if (childMsg is WmCreate or WmDestroy)
                Dispatcher.BeginInvoke(() => HookEmbeddedChildren());
        }

        if (TryRaiseMouse(msg, wParam, ref handled))
            return IntPtr.Zero;

        if (TryRaiseKey(msg, wParam, lParam, ref handled))
            return IntPtr.Zero;

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        // Default HwndHost sets SWP_NOCOPYBITS, which discards pixels and flashes black.
        SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            (int)rcBoundingBox.X,
            (int)rcBoundingBox.Y,
            Math.Max(1, (int)rcBoundingBox.Width),
            Math.Max(1, (int)rcBoundingBox.Height),
            SwpAsyncWindowPos | SwpNoZOrder | SwpNoActivate);

        HookEmbeddedChildren();
    }

    private bool TryRaiseMouse(int msg, IntPtr wParam, ref bool handled)
    {
        switch (msg)
        {
            case WmRButtonUp:
                RaiseOnUi(() => NativeRightButtonUp?.Invoke());
                handled = true;
                return true;
            case WmLButtonDown:
                RaiseOnUi(() => NativeLeftButtonDown?.Invoke(1));
                handled = true;
                return true;
            case WmLButtonDblClk:
                RaiseOnUi(() => NativeLeftButtonDown?.Invoke(2));
                handled = true;
                return true;
            case WmMouseWheel:
                var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                RaiseOnUi(() => NativeMouseWheel?.Invoke(delta));
                handled = true;
                return true;
            case WmMouseMove:
                // Sync: fullscreen chrome hide timer; avoid flooding BeginInvoke.
                NativeMouseMove?.Invoke();
                return false;
            default:
                return false;
        }
    }

    private bool TryRaiseKey(int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is not (WmKeyDown or WmSysKeyDown))
            return false;

        var vk = unchecked((int)wParam.ToInt64());
        var key = KeyInterop.KeyFromVirtualKey(vk);
        if (key == Key.None)
            return false;

        var isRepeat = (lParam.ToInt64() & KeyPrevDownBit) != 0;
        RaiseOnUi(() => NativeKeyDown?.Invoke(key, isRepeat));
        handled = true;
        return true;
    }

    private void RaiseOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action);
    }

    private bool EnumChildCallback(IntPtr child, IntPtr lParam)
    {
        SubclassChild(child);
        return true;
    }

    private void SubclassChild(IntPtr child)
    {
        if (child == IntPtr.Zero || child == _hwnd)
            return;
        if (_childOldProcs.ContainsKey(child))
            return;
        if (_childWndProcKeepAlive is null)
            return;

        var procPtr = Marshal.GetFunctionPointerForDelegate(_childWndProcKeepAlive);
        var old = SetWindowLongPtr(child, GwlWndProc, procPtr);
        if (old == IntPtr.Zero && Marshal.GetLastWin32Error() != 0)
            return;
        _childOldProcs[child] = old;
    }

    private IntPtr ChildWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        var handled = false;
        if (TryRaiseMouse(msg, wParam, ref handled) && handled)
            return IntPtr.Zero;
        if (TryRaiseKey(msg, wParam, lParam, ref handled) && handled)
            return IntPtr.Zero;

        if (msg == WmDestroy)
        {
            if (_childOldProcs.TryGetValue(hwnd, out var old) && old != IntPtr.Zero)
            {
                SetWindowLongPtr(hwnd, GwlWndProc, old);
                _childOldProcs.Remove(hwnd);
                return CallWindowProc(old, hwnd, msg, wParam, lParam);
            }
        }

        if (_childOldProcs.TryGetValue(hwnd, out var prev) && prev != IntPtr.Zero)
            return CallWindowProc(prev, hwnd, msg, wParam, lParam);
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void UnhookAllChildren()
    {
        foreach (var (child, old) in _childOldProcs)
        {
            if (old != IntPtr.Zero && IsWindow(child))
                SetWindowLongPtr(child, GwlWndProc, old);
        }
        _childOldProcs.Clear();
    }

    private void StartChildHookRetry()
    {
        StopChildHookRetry();
        // mpv often creates the VO child a few hundred ms after --wid attach.
        var attempts = 0;
        _childHookTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _childHookTimer.Tick += (_, _) =>
        {
            HookEmbeddedChildren();
            attempts++;
            if (attempts >= 15 || _childOldProcs.Count > 0)
                StopChildHookRetry();
        };
        _childHookTimer.Start();
    }

    private void StopChildHookRetry()
    {
        if (_childHookTimer is null) return;
        _childHookTimer.Stop();
        _childHookTimer = null;
    }

    private static void EnsureWindowClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered)
                return;

            var hInstance = GetModuleHandleW(null);
            var user32 = GetModuleHandleW("user32.dll");
            var defWndProc = GetProcAddress(user32, "DefWindowProcW");
            if (defWndProc == IntPtr.Zero)
                throw new InvalidOperationException("无法解析 DefWindowProcW。");

            var wc = new WndClassExW
            {
                cbSize = (uint)Marshal.SizeOf<WndClassExW>(),
                // No CS_HREDRAW|CS_VREDRAW — those force full client invalidation on resize.
                style = 0,
                lpfnWndProc = defWndProc,
                hInstance = hInstance,
                hbrBackground = GetStockObject(4 /* BLACK_BRUSH */),
                lpszClassName = ClassName,
            };

            if (RegisterClassExW(ref wc) == 0)
            {
                const int errorClassAlreadyExists = 1410;
                if (Marshal.GetLastWin32Error() != errorClassAlreadyExists)
                    throw new InvalidOperationException("无法注册 mpv 宿主窗口类。");
            }

            _classRegistered = true;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassExW lpwcx);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int i);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassExW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }
}
