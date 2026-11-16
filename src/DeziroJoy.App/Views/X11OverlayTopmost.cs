using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using DeziroJoy.Platform.Linux.Windowing;

namespace DeziroJoy.App.Views;

/// <summary>
/// The seam the two floating windows - the layer hint and the screen keyboard - keep themselves
/// above the target application through. Production resolves the window's X11 id and asks for
/// <c>_NET_WM_STATE_ABOVE</c> the EWMH way; the tests substitute a recorder, so the lifecycle
/// (applied per appearance, re-applied after every hide-and-show) is pinned without a display.
/// </summary>
internal interface IOverlayTopmostState
{
    /// <summary>
    /// Applies the above-state to one window. Returns false when the platform has nothing to
    /// apply it through - a window with no platform handle yet, a session with no X11 - which
    /// is an answer, not a failure: the window keeps whatever stacking it already has.
    /// </summary>
    bool Apply(Window window);
}

/// <summary>
/// The production <see cref="IOverlayTopmostState"/>: the X11 path only.
/// <para>
/// <see cref="Window.Topmost"/> is a toolkit hint, and Avalonia's X11 backend applies it when
/// the property is set - which for a window declared topmost in XAML is before the window is
/// mapped. A window manager only honours <c>_NET_WM_STATE_ABOVE</c> once it manages the mapped
/// window, so the state is asked for again here, where the window exists: every appearance, and
/// again on the pass that follows, because a window manager may not have seen the map yet when
/// the first ask goes out.
/// </para>
/// <para>
/// Everything the toolkit hint already guarantees stays guaranteed: this asks for stacking only,
/// so it never takes keyboard focus (no activation, no <c>_NET_ACTIVE_WINDOW</c>), never touches
/// the input region (the layer hint stays click-through), and names no window but the one it is
/// given (the target application is never raised or activated).
/// </para>
/// </summary>
internal sealed class X11OverlayTopmost : IOverlayTopmostState
{
    public static IOverlayTopmostState Shared { get; } = new X11OverlayTopmost();

    public bool Apply(Window window)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        // The platform handle only exists once the window is live; a window that was never
        // shown has no X11 id to name.
        if (window.TryGetPlatformHandle() is not { } handle)
        {
            return false;
        }

        // Under Wayland the handle is a wl_surface pointer, not an X11 window id: a number of
        // the right shape that names no X window, so the EWMH requests are not made at all.
        if (handle.Handle == nint.Zero || !IsX11Window(handle))
        {
            return false;
        }

        X11OverlayState.Apply(handle.Handle);
        return true;
    }

    /// <summary>
    /// Whether the platform handle names an X11 window. Avalonia's X11 backend reports its
    /// window id under the "XID" descriptor; every other backend - Wayland, Windows, the
    /// headless test platform - reports its own, and none of them is an X11 window.
    /// </summary>
    internal static bool IsX11Window(IPlatformHandle handle)
        => handle.HandleDescriptor?.Contains("XID", StringComparison.OrdinalIgnoreCase) == true;
}
