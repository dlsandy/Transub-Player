using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using TransubPlayer.Platform;

namespace TransubPlayer.Desktop.Controls;

/// <summary>
/// Avalonia native host for mpv <c>--wid</c> (HWND on Windows/X11, NSView* on macOS).
/// </summary>
internal sealed class MpvVideoSurface : NativeControlHost, IVideoSurface
{
    private IPlatformHandle? _handle;

    public IntPtr NativeHandle => _handle?.Handle ?? IntPtr.Zero;

    public void EnsureHandle()
    {
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    bool IVideoSurface.CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public void HookEmbeddedChildren()
    {
        // macOS NSView embed does not need Win32 child WndProc hooks.
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _handle = base.CreateNativeControlCore(parent);
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _handle = null;
        base.DestroyNativeControlCore(control);
    }
}
