using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

public class EvdevGamepadStateTrackerTests
{
    private static readonly EvdevAxisRange FullStickRange = new(-32768, 32767);

    private static EvdevGamepadStateTracker CreateTracker() => new(new Dictionary<ushort, EvdevAxisRange>
    {
        [EvdevCodes.AbsX] = FullStickRange,
        [EvdevCodes.AbsY] = FullStickRange,
        [EvdevCodes.AbsRx] = FullStickRange,
        [EvdevCodes.AbsRy] = FullStickRange,
        [EvdevCodes.AbsZ] = new EvdevAxisRange(0, 255),
        [EvdevCodes.AbsRz] = new EvdevAxisRange(0, 255),
    });

    [Fact]
    public void Every_Button_Code_Maps_To_The_Right_Logical_Button()
    {
        Assert.Equal(GamepadButton.A, EvdevButtonMap.ToButton(EvdevCodes.BtnSouth));
        Assert.Equal(GamepadButton.B, EvdevButtonMap.ToButton(EvdevCodes.BtnEast));
        Assert.Equal(GamepadButton.X, EvdevButtonMap.ToButton(EvdevCodes.BtnWest));
        Assert.Equal(GamepadButton.Y, EvdevButtonMap.ToButton(EvdevCodes.BtnNorth));
        Assert.Equal(GamepadButton.LeftBumper, EvdevButtonMap.ToButton(EvdevCodes.BtnTl));
        Assert.Equal(GamepadButton.RightBumper, EvdevButtonMap.ToButton(EvdevCodes.BtnTr));
        Assert.Equal(GamepadButton.LeftTrigger, EvdevButtonMap.ToButton(EvdevCodes.BtnTl2));
        Assert.Equal(GamepadButton.RightTrigger, EvdevButtonMap.ToButton(EvdevCodes.BtnTr2));
        Assert.Equal(GamepadButton.Back, EvdevButtonMap.ToButton(EvdevCodes.BtnSelect));
        Assert.Equal(GamepadButton.Start, EvdevButtonMap.ToButton(EvdevCodes.BtnStart));
        Assert.Equal(GamepadButton.Guide, EvdevButtonMap.ToButton(EvdevCodes.BtnMode));
        Assert.Equal(GamepadButton.LeftStick, EvdevButtonMap.ToButton(EvdevCodes.BtnThumbL));
        Assert.Equal(GamepadButton.RightStick, EvdevButtonMap.ToButton(EvdevCodes.BtnThumbR));
    }

    [Fact]
    public void Key_Press_And_Release_Toggle_The_Mapped_Button()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, EvdevCodes.BtnSouth, 1));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.A));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, EvdevCodes.BtnSouth, 0));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.A));
    }

    [Fact]
    public void Stick_Axes_Normalize_With_Device_Ranges()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, 32767));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRx, 0));

        var state = tracker.Snapshot();

        Assert.Equal(-1f, state.LeftStickX);
        Assert.Equal(1f, state.LeftStickY);
        Assert.Equal(0f, state.RightStickX, 4);
    }

    [Fact]
    public void Hat_Axes_Map_To_Dpad_Buttons()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsHat0X, -1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsHat0Y, 1));

        var state = tracker.Snapshot();

        Assert.True(state.IsPressed(GamepadButton.DPadLeft));
        Assert.True(state.IsPressed(GamepadButton.DPadDown));
        Assert.False(state.IsPressed(GamepadButton.DPadUp));
        Assert.False(state.IsPressed(GamepadButton.DPadRight));
    }

    [Fact]
    public void Hat_Center_Clears_Dpad_Buttons()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsHat0X, -1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsHat0X, 0));

        var state = tracker.Snapshot();

        Assert.False(state.IsPressed(GamepadButton.DPadLeft));
        Assert.False(state.IsPressed(GamepadButton.DPadRight));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(127, false)]
    [InlineData(128, true)]
    [InlineData(255, true)]
    public void Analog_Triggers_Are_Digital_Above_The_Midpoint(int value, bool expectedPressed)
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, value));

        Assert.Equal(expectedPressed, tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Unknown_Event_Codes_Are_Ignored()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x999, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, 0x999, 1234));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvSyn, 0, 0));

        Assert.Equal(GamepadButtonMask.None, tracker.Snapshot().PressedButtons);
        Assert.Equal(0f, tracker.Snapshot().LeftStickX);
    }

    [Fact]
    public void Reset_Clears_Everything()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, EvdevCodes.BtnSouth, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, 32767));
        tracker.Reset();

        Assert.Equal(GamepadButtonMask.None, tracker.Snapshot().PressedButtons);
        Assert.Equal(0f, tracker.Snapshot().LeftStickX);
    }
}
