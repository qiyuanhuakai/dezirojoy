using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using DeziroJoy.App.Views;
using Xunit;

namespace DeziroJoy.App.Tests.Views;

/// <summary>
/// The production above-state seam: which windows it touches and which it leaves alone. The
/// two floating windows ask it to keep them above the target application, and the ask is only
/// safe if it names a real X11 window - under Wayland the platform handle is a wl_surface, a
/// number of the right shape that names no X window, and on a window with no platform handle
/// there is nothing to name at all.
/// </summary>
public sealed class X11OverlayTopmostTests
{
    [Fact]
    public void An_X11_Window_Is_Recognized()
    {
        // Avalonia's X11 backend reports the window id under the XID descriptor.
        Assert.True(X11OverlayTopmost.IsX11Window(new PlatformHandle(0x1234, "XID")));
    }

    [Fact]
    public void A_Wayland_Surface_Is_Not_An_X11_Window()
    {
        // The handle is a wl_surface pointer: the EWMH requests would be sent to a window id
        // that names nothing, so the seam must not try.
        Assert.False(X11OverlayTopmost.IsX11Window(new PlatformHandle(0x1234, "WAYLAND")));
        Assert.False(X11OverlayTopmost.IsX11Window(new PlatformHandle(0x1234, "wl_surface")));
    }

    [Fact]
    public void A_Headless_Stub_Is_Not_An_X11_Window()
    {
        Assert.False(X11OverlayTopmost.IsX11Window(new PlatformHandle(0, "STUB")));
    }

    [Fact]
    public void A_Handle_With_No_Descriptor_Is_Not_An_X11_Window()
    {
        Assert.False(X11OverlayTopmost.IsX11Window(new PlatformHandle(0x1234, null!)));
    }

    [AvaloniaFact]
    public void A_Window_The_Platform_Cannot_Name_Is_Left_Alone()
    {
        var window = new Window();
        try
        {
            // Whatever this session is - Wayland, a headless test platform - a window whose
            // handle is not an X11 window gets no EWMH requests and no exception either: the
            // window keeps whatever stacking it already has.
            var applied = X11OverlayTopmost.Shared.Apply(window);

            Assert.False(applied);
        }
        finally
        {
            window.Close();
        }
    }
}
