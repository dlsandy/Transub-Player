using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;

namespace TransubPlayer.Services;

internal static class WindowChromeUtil
{
    private const int GwlStyle = -16;
    private const int WsClipChildren = 0x02000000;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSizing = 0x0214;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;
    private const int SwRestore = 9;
    private const int SwShow = 5;
    private const uint MonitorDefaultToNearest = 2;
    private static readonly HashSet<IntPtr> MinMaxHooked = new();

    /// <summary>
    /// When true, <see cref="WindowState.Maximized"/> fills the entire monitor (taskbar covered).
    /// Used for borderless video fullscreen; ordinary maximize keeps the work area.
    /// </summary>
    public static bool VideoFullscreen { get; set; }

    /// <summary>
    /// Optional live-resize aspect lock. Return null to allow free resize.
    /// Called from the window procedure on the UI thread during WM_SIZING.
    /// </summary>
    public static Func<WindowAspectLock?>? AspectLockProvider { get; set; }

    /// <summary>Constraint for keeping content aspect while the user drags a resize border.</summary>
    public readonly struct WindowAspectLock
    {
        public WindowAspectLock(double contentAspect, int chromeWidthPx, int chromeHeightPx, int minWidthPx, int minHeightPx)
        {
            ContentAspect = contentAspect;
            ChromeWidthPx = chromeWidthPx;
            ChromeHeightPx = chromeHeightPx;
            MinWidthPx = minWidthPx;
            MinHeightPx = minHeightPx;
        }

        /// <summary>Desired content (video area) width / height.</summary>
        public double ContentAspect { get; }
        public int ChromeWidthPx { get; }
        public int ChromeHeightPx { get; }
        public int MinWidthPx { get; }
        public int MinHeightPx { get; }
    }

    /// <summary>
    /// Raise the window when opened from Explorer / a second instance. Retries after layout
    /// because HWND / foreground rights may not be ready on the first call.
    /// </summary>
    public static void ScheduleBringToFront(Window window)
    {
        BringToFront(window);

        void Retry()
        {
            if (window.IsVisible)
                BringToFront(window);
        }

        // Prefer Action lambdas so overload resolution never passes priority as a
        // DynamicInvoke argument (TargetParameterCountException).
        window.Dispatcher.BeginInvoke(() => Retry(), DispatcherPriority.Loaded);
        window.Dispatcher.BeginInvoke(() => Retry(), DispatcherPriority.Render);
        window.Dispatcher.BeginInvoke(() => Retry(), DispatcherPriority.ApplicationIdle);

        if (window.IsLoaded)
            return;

        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            window.Loaded -= onLoaded!;
            BringToFront(window);
            window.Dispatcher.BeginInvoke(() => Retry(), DispatcherPriority.Loaded);
        };
        window.Loaded += onLoaded;
    }

    /// <summary>
    /// Best-effort foreground. Uses AttachThreadInput because SetForegroundWindow alone is
    /// blocked when activated from another process (file association / pipe forward).
    /// </summary>
    public static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Show();
        window.Activate();

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.SourceInitialized += OnSourceInitializedBringToFront;
            return;
        }

        ForceForeground(hwnd);

        // Toggle Topmost to bump Z-order even when AlwaysOnTop is already enabled.
        var topmost = window.Topmost;
        window.Topmost = false;
        window.Topmost = topmost;

        window.Focus();
    }

    private static void OnSourceInitializedBringToFront(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.SourceInitialized -= OnSourceInitializedBringToFront;
        BringToFront(window);
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        else
            ShowWindow(hwnd, SwShow);

        var foreground = GetForegroundWindow();
        if (foreground == hwnd)
            return;

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, out _)
            : 0u;
        var targetThread = GetWindowThreadProcessId(hwnd, out _);

        var attachedForeground = false;
        var attachedTarget = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
            if (targetThread != currentThread)
                attachedTarget = AttachThreadInput(currentThread, targetThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public static WindowChrome Create(double captionHeight, bool canResize)
    {
        // NonClientFrameEdges.None + live resize is a known WindowChrome flicker source
        // (dotnet/wpf#1176). Keeping one system edge (Right) markedly reduces chrome flash.
        // GlassFrameThickness=1 lets DWM compose the frame instead of blanking the client.
        return new WindowChrome
        {
            CaptionHeight = captionHeight,
            GlassFrameThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = canResize ? new Thickness(6) : new Thickness(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.Right,
        };
    }

    /// <summary>
    /// Call once after the HWND exists. WS_CLIPCHILDREN stops the parent from painting over
    /// the mpv child during resize, which otherwise flashes the WPF chrome.
    /// Also hooks WM_GETMINMAXINFO so WindowState.Maximized fills the work area (taskbar visible),
    /// distinct from borderless video fullscreen.
    /// </summary>
    public static void ApplyHostClipChildren(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var style = GetWindowLongPtr(hwnd, GwlStyle);
        _ = SetWindowLongPtr(hwnd, GwlStyle, style | (IntPtr)WsClipChildren);

        if (MinMaxHooked.Add(hwnd))
        {
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }
    }

    public static void DragOrToggle(Window window, MouseButtonEventArgs e, bool allowMaximize = true)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return;
        if (allowMaximize && e.ClickCount == 2)
        {
            ToggleMax(window);
            e.Handled = true;
            return;
        }

        try { window.DragMove(); }
        catch (InvalidOperationException) { /* ignore */ }
    }

    /// <summary>Ordinary window maximize / restore (work area). Not video fullscreen.</summary>
    public static void ToggleMax(Window window)
    {
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmSizing)
            return HandleSizing(wParam, lParam, ref handled);

        if (msg != WmGetMinMaxInfo)
            return IntPtr.Zero;

        // WindowStyle=None + WindowChrome otherwise maximizes over the taskbar, looking like fullscreen.
        // VideoFullscreen intentionally uses the full monitor; ordinary maximize stays in the work area.
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var monitorRect = monitorInfo.Monitor;
                if (VideoFullscreen)
                {
                    mmi.MaxPosition.X = 0;
                    mmi.MaxPosition.Y = 0;
                    mmi.MaxSize.X = Math.Abs(monitorRect.Right - monitorRect.Left);
                    mmi.MaxSize.Y = Math.Abs(monitorRect.Bottom - monitorRect.Top);
                }
                else
                {
                    var work = monitorInfo.Work;
                    mmi.MaxPosition.X = Math.Abs(work.Left - monitorRect.Left);
                    mmi.MaxPosition.Y = Math.Abs(work.Top - monitorRect.Top);
                    mmi.MaxSize.X = Math.Abs(work.Right - work.Left);
                    mmi.MaxSize.Y = Math.Abs(work.Bottom - work.Top);
                }

                Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            }
        }

        return IntPtr.Zero;
    }

    private static IntPtr HandleSizing(IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        WindowAspectLock? lockInfo;
        try
        {
            lockInfo = AspectLockProvider?.Invoke();
        }
        catch
        {
            return IntPtr.Zero;
        }

        if (lockInfo is not { ContentAspect: > 0.01 and < 100 } info)
            return IntPtr.Zero;

        var rect = Marshal.PtrToStructure<Rect>(lParam);
        ApplyAspectToSizingRect(ref rect, wParam.ToInt32(), info);
        Marshal.StructureToPtr(rect, lParam, fDeleteOld: false);
        handled = true;
        return (IntPtr)1;
    }

    private static void ApplyAspectToSizingRect(ref Rect rect, int edge, WindowAspectLock info)
    {
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var aspect = info.ContentAspect;
        var chromeW = Math.Max(0, info.ChromeWidthPx);
        var chromeH = Math.Max(0, info.ChromeHeightPx);
        var minW = Math.Max(info.MinWidthPx, chromeW + 16);
        var minH = Math.Max(info.MinHeightPx, chromeH + 16);

        void FromWidth()
        {
            var contentW = Math.Max(1, width - chromeW);
            height = (int)Math.Round(contentW / aspect + chromeH);
            if (height < minH)
            {
                height = minH;
                width = (int)Math.Round(Math.Max(1, height - chromeH) * aspect + chromeW);
            }

            if (width < minW)
            {
                width = minW;
                height = (int)Math.Round(Math.Max(1, width - chromeW) / aspect + chromeH);
            }
        }

        void FromHeight()
        {
            var contentH = Math.Max(1, height - chromeH);
            width = (int)Math.Round(contentH * aspect + chromeW);
            if (width < minW)
            {
                width = minW;
                height = (int)Math.Round(Math.Max(1, width - chromeW) / aspect + chromeH);
            }

            if (height < minH)
            {
                height = minH;
                width = (int)Math.Round(Math.Max(1, height - chromeH) * aspect + chromeW);
            }
        }

        switch (edge)
        {
            case WmszLeft:
            case WmszRight:
                FromWidth();
                if (edge == WmszLeft)
                    rect.Left = rect.Right - width;
                else
                    rect.Right = rect.Left + width;
                rect.Bottom = rect.Top + height;
                break;

            case WmszTop:
            case WmszBottom:
                FromHeight();
                rect.Right = rect.Left + width;
                if (edge == WmszTop)
                    rect.Top = rect.Bottom - height;
                else
                    rect.Bottom = rect.Top + height;
                break;

            case WmszTopLeft:
                FromWidth();
                rect.Left = rect.Right - width;
                rect.Top = rect.Bottom - height;
                break;

            case WmszTopRight:
                FromWidth();
                rect.Right = rect.Left + width;
                rect.Top = rect.Bottom - height;
                break;

            case WmszBottomLeft:
                FromWidth();
                rect.Left = rect.Right - width;
                rect.Bottom = rect.Top + height;
                break;

            case WmszBottomRight:
            default:
                FromWidth();
                rect.Right = rect.Left + width;
                rect.Bottom = rect.Top + height;
                break;
        }
    }

    /// <summary>Adjust caption / resize borders without replacing WindowChrome (avoids chrome rebuild flicker).</summary>
    public static void ApplyChromeMetrics(Window window, double captionHeight, bool canResize)
    {
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome is null)
        {
            WindowChrome.SetWindowChrome(window, Create(captionHeight, canResize));
            return;
        }

        chrome.CaptionHeight = captionHeight;
        chrome.ResizeBorderThickness = canResize ? new Thickness(6) : new Thickness(0);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : (IntPtr)SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32());

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }
}
