using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.Platform.Tests;

/// <summary>
/// Cross-platform contract for the stick Y axis: positive Y means "pushed up".
/// <para>
/// The Core engine assumes this direction. <c>MouseStickProcessor</c> applies
/// <c>dy = -stickY</c> when <c>InvertY</c> is enabled (its default), so a positive stick Y
/// has to move the pointer up on screen. XInput's <c>sThumbLY</c> is already positive-up;
/// evdev's <c>ABS_Y</c>/<c>ABS_RY</c> are positive-down and must be negated by the Linux
/// tracker. Both halves are pinned here even though only one is observable per platform.
/// </para>
/// </summary>
public class StickSignConventionTests
{
    [Fact]
    public void Windows_Reports_Positive_Stick_Y_When_Pushed_Up()
    {
        var state = XInputStateMapper.ToGamepadState(new XInputGamepad
        {
            ThumbLY = 32767,
            ThumbRY = 32767,
        });

        Assert.Equal(32767f / 32768f, state.LeftStickY, 5);
        Assert.Equal(32767f / 32768f, state.RightStickY, 5);
    }

    [Fact]
    public void Windows_Reports_Negative_Stick_Y_When_Pushed_Down()
    {
        var state = XInputStateMapper.ToGamepadState(new XInputGamepad
        {
            ThumbLY = -32768,
            ThumbRY = -32768,
        });

        Assert.Equal(-1f, state.LeftStickY);
        Assert.Equal(-1f, state.RightStickY);
    }

    [Fact]
    public void Linux_Reports_Positive_Stick_Y_When_Pushed_Up()
    {
        var tracker = new EvdevGamepadStateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, -32768));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRy, -32768));

        var state = tracker.Snapshot();

        Assert.Equal(1f, state.LeftStickY);
        Assert.Equal(1f, state.RightStickY);
    }

    [Fact]
    public void Linux_Reports_Negative_Stick_Y_When_Pushed_Down()
    {
        var tracker = new EvdevGamepadStateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, 32767));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRy, 32767));

        var state = tracker.Snapshot();

        Assert.Equal(-1f, state.LeftStickY);
        Assert.Equal(-1f, state.RightStickY);
    }

    [Fact]
    public void Both_Platforms_Agree_That_A_Pushed_Up_Stick_Is_Positive()
    {
        var windows = XInputStateMapper.ToGamepadState(new XInputGamepad { ThumbLY = 32767 });

        var tracker = new EvdevGamepadStateTracker();
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, -32768));
        var linux = tracker.Snapshot();

        Assert.True(windows.LeftStickY > 0.9f, "XInput: a pushed-up stick must be positive");
        Assert.True(linux.LeftStickY > 0.9f, "evdev: a pushed-up stick must be positive");
    }

    [Fact]
    public void Both_Platforms_Agree_That_A_Pushed_Down_Stick_Is_Negative()
    {
        var windows = XInputStateMapper.ToGamepadState(new XInputGamepad { ThumbLY = -32768 });

        var tracker = new EvdevGamepadStateTracker();
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, 32767));
        var linux = tracker.Snapshot();

        Assert.True(windows.LeftStickY < -0.9f, "XInput: a pushed-down stick must be negative");
        Assert.True(linux.LeftStickY < -0.9f, "evdev: a pushed-down stick must be negative");
    }

    [Fact]
    public void Both_Platforms_Leave_The_X_Axis_Untouched()
    {
        var windows = XInputStateMapper.ToGamepadState(new XInputGamepad { ThumbLX = -32768 });

        var tracker = new EvdevGamepadStateTracker();
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768));
        var linux = tracker.Snapshot();

        Assert.Equal(-1f, windows.LeftStickX);
        Assert.Equal(-1f, linux.LeftStickX);
    }
}
