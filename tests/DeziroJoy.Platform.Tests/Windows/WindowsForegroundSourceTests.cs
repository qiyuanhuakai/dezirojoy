using System.Runtime.InteropServices;
using DeziroJoy.Platform.Windows.Foreground;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The Windows foreground source. The interop itself only exists on Windows, so the real read is
/// guarded; the fullscreen decision it feeds is pure rectangle arithmetic and runs everywhere,
/// because that is the part a rule's "仅全屏" condition depends on and the part that has to be
/// right before it is allowed to gate anything.
/// </summary>
public sealed class WindowsForegroundSourceTests
{
    private static Rect Rect(int left, int top, int right, int bottom) => new()
    {
        Left = left,
        Top = top,
        Right = right,
        Bottom = bottom,
    };

    [Theory]
    [InlineData(0, 0, 1920, 1080, 0, 0, 1920, 1080, true)]
    [InlineData(-1, -1, 1921, 1081, 0, 0, 1920, 1080, true)]
    [InlineData(0, 0, 1920, 1040, 0, 0, 1920, 1080, false)]
    [InlineData(0, 0, 1900, 1080, 0, 0, 1920, 1080, false)]
    [InlineData(0, 40, 1920, 1080, 0, 0, 1920, 1080, false)]
    public void A_Window_Counts_As_Fullscreen_Only_When_It_Covers_Its_Monitor(
        int windowLeft,
        int windowTop,
        int windowRight,
        int windowBottom,
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom,
        bool expected)
    {
        var fullscreen = ForegroundGeometry.CoversMonitor(
            Rect(windowLeft, windowTop, windowRight, windowBottom),
            Rect(monitorLeft, monitorTop, monitorRight, monitorBottom));

        Assert.Equal(expected, fullscreen);
    }

    [Fact]
    public void Reading_The_Foreground_Never_Throws_And_Never_Names_An_Empty_Process()
    {
        // The guard, not Assert.SkipWhen: the platform analyzer has to see it to know the call
        // below is legal, and the skip keeps the test honest everywhere else.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("the user32 foreground query only exists on Windows");
            return;
        }

        var context = new WindowsForegroundSource().Read();

        if (context.ProcessName is { } name)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
        else
        {
            // No foreground window, or one with no resolvable process: no rule may match that.
            Assert.Null(context.ProcessName);
        }
    }

    [Fact]
    public void The_Monitor_Info_Buffer_Matches_The_Win32_Layout()
    {
        // cbSize + two RECTs + dwFlags + CCHDEVICENAME characters. A buffer of any other size
        // makes GetMonitorInfo read the monitor rectangle out of the wrong bytes and call a
        // windowed application fullscreen. The layout is the same everywhere, so this pins it on
        // every platform the suite runs on.
        Assert.Equal(104, MonitorInfoLayout.Size);
    }

    [Fact]
    public void The_Monitor_Rectangle_Is_Read_From_Where_The_Abi_Puts_It()
    {
        var buffer = Marshal.AllocHGlobal(MonitorInfoLayout.Size);
        try
        {
            MonitorInfoLayout.Prepare(buffer);

            // cbSize first, then rcMonitor: left, top, right, bottom.
            Marshal.WriteInt32(buffer, 4, -1920);
            Marshal.WriteInt32(buffer, 8, 0);
            Marshal.WriteInt32(buffer, 12, 0);
            Marshal.WriteInt32(buffer, 16, 1080);

            var monitor = MonitorInfoLayout.ReadMonitor(buffer);

            Assert.Equal(-1920, monitor.Left);
            Assert.Equal(0, monitor.Top);
            Assert.Equal(0, monitor.Right);
            Assert.Equal(1080, monitor.Bottom);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
