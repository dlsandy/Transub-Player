namespace TransubPlayer.Platform;

/// <summary>Native video embed surface for mpv <c>--wid</c> (HWND / NSView).</summary>
internal interface IVideoSurface
{
    /// <summary>Platform native handle for mpv embed; <see cref="IntPtr.Zero"/> until ready.</summary>
    IntPtr NativeHandle { get; }

    /// <summary>Force creation of the native surface before starting mpv.</summary>
    void EnsureHandle();

    bool CheckAccess();

    Task InvokeAsync(Action action);

    void Post(Action action);

    /// <summary>Re-hook child windows/views after mpv creates them (Windows HWND path).</summary>
    void HookEmbeddedChildren();
}
