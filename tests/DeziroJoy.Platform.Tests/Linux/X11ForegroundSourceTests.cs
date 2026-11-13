using DeziroJoy.Platform.Linux.Foreground;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The X11 foreground source, best-effort by design. Under a session with no reachable X server -
/// Wayland, a headless box, or a display name with nothing listening on it - it must report
/// nothing rather than guess: no rule matches, and the desktop default profile runs. That is the
/// one behaviour that can be pinned without a window manager, and it is the one that keeps a
/// Wayland session from mapping into a program nobody can name.
/// </summary>
public sealed class X11ForegroundSourceTests
{
    [Fact]
    public void An_Unreachable_Display_Reports_No_Foreground_Program()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XCB query only exists on Linux");

        var original = Environment.GetEnvironmentVariable("DISPLAY");

        try
        {
            // Nothing is listening on this display, which is exactly the Wayland and headless
            // case: the connect fails and the read has nothing to answer with.
            Environment.SetEnvironmentVariable("DISPLAY", ":99");

            using var source = new X11ForegroundSource();
            var context = source.Read();

            Assert.Null(context.ProcessName);
            Assert.Null(context.IsFullscreen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", original);
        }
    }

    [Fact]
    public void Reading_The_Foreground_Never_Throws_And_Never_Names_An_Empty_Process()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XCB query only exists on Linux");

        using var source = new X11ForegroundSource();

        // Whatever this machine answers - a real X11 session, or nothing at all - the read has
        // to come back with a shape the rule table can use.
        var context = source.Read();

        if (context.ProcessName is { } name)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
        else
        {
            Assert.Null(context.ProcessName);
        }
    }

    [Fact]
    public void The_Property_Reply_Layout_Is_Pinned_To_The_Xcb_Abi()
    {
        // xcb_get_property_reply_t from xproto.h: response_type, format, sequence, length, type,
        // bytes_after, value_len, twelve pad bytes. These offsets were guessed once and wrong
        // once - the property read back empty and the source reported "no foreground program"
        // on a session that had one - so they are pinned rather than trusted.
        Assert.Equal(1, XcbNative.ReplyFormatOffset);
        Assert.Equal(12, XcbNative.ReplyBytesAfterOffset);
        Assert.Equal(16, XcbNative.ReplyValueLenOffset);
        Assert.Equal(32, XcbNative.ReplyHeaderSize);
        Assert.Equal(32, XcbNative.ReplyValueOffset);
    }

    [Fact]
    public void A_Source_Can_Be_Disposed_Before_It_Ever_Read()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XCB query only exists on Linux");

        using var source = new X11ForegroundSource();
        source.Dispose();

        // No connection was ever opened, so there is nothing to leak and nothing to close twice.
        Assert.Null(source.Read().ProcessName);
    }
}
