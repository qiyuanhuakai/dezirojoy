using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The axis-level diagnostic: which ABS codes the tracker maps, and what the provider reports
/// about the ones it does not. An unmapped axis is otherwise indistinguishable from a broken
/// stick - the user moves it and nothing lights up - so the raw code and declared range must
/// survive the scan for an evtest comparison.
/// </summary>
public class EvdevUnhandledAxisTests
{
    [Fact]
    public void Every_Mapped_Axis_Code_Is_Handled()
    {
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsX));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsY));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsZ));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsRx));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsRy));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsRz));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsGas));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsBrake));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsHat0X));
        Assert.True(EvdevGamepadStateTracker.IsHandledAbsCode(EvdevCodes.AbsHat0Y));
    }

    [Theory]
    [InlineData(0x06)] // ABS_THROTTLE
    [InlineData(0x07)] // ABS_RUDDER
    [InlineData(0x08)] // ABS_WHEEL - the simulation axis just below ABS_GAS
    [InlineData(0x0b)] // just above ABS_BRAKE
    [InlineData(0x12)] // ABS_HAT1X - a second hat
    [InlineData(0x18)] // ABS_PRESSURE
    [InlineData(0x35)] // ABS_MT_POSITION_X - multitouch
    [InlineData(0x3f)] // ABS_MAX
    public void The_Codes_Around_The_Mapped_Ones_Are_Not_Handled(int code)
    {
        Assert.False(EvdevGamepadStateTracker.IsHandledAbsCode((ushort)code));
    }

    [Fact]
    public void A_Fully_Mapped_Device_Has_Nothing_To_Report()
    {
        var ranges = new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsX] = new EvdevAxisRange(-32768, 32767, 0),
            [EvdevCodes.AbsY] = new EvdevAxisRange(-32768, 32767, 0),
            [EvdevCodes.AbsZ] = new EvdevAxisRange(0, 255, 0),
            [EvdevCodes.AbsRz] = new EvdevAxisRange(0, 255, 0),
            [EvdevCodes.AbsGas] = new EvdevAxisRange(0, 255, 0),
            [EvdevCodes.AbsBrake] = new EvdevAxisRange(0, 255, 0),
            [EvdevCodes.AbsHat0X] = new EvdevAxisRange(-1, 1, 0),
        };

        Assert.Empty(EvdevGamepadProvider.FindUnhandledAxes("/dev/input/event5", ranges));
    }

    [Fact]
    public void An_Axis_The_Tracker_Does_Not_Map_Is_Listed_With_Its_Raw_Code_And_Range()
    {
        var ranges = new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsX] = new EvdevAxisRange(-32768, 32767, 3276),
            [0x08] = new EvdevAxisRange(0, 255, 0), // ABS_WHEEL
            [0x35] = new EvdevAxisRange(0, 32767, 1234), // ABS_MT_POSITION_X
        };

        var unhandled = EvdevGamepadProvider.FindUnhandledAxes("/dev/input/event5", ranges);

        Assert.Equal(2, unhandled.Count);

        // Code order, so the list reads the way the kernel's own axis numbering does.
        Assert.Equal(0x08, unhandled[0].Code);
        Assert.Equal(0x35, unhandled[1].Code);

        Assert.Equal(0, unhandled[0].Minimum);
        Assert.Equal(255, unhandled[0].Maximum);
        Assert.Equal(1234, unhandled[1].Value);
        Assert.All(unhandled, axis => Assert.Equal("/dev/input/event5", axis.DevicePath));
    }
}
