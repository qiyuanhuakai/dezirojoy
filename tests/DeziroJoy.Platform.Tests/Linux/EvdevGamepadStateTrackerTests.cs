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
        // input-event-codes.h: BTN_WEST 0x134 is #defined as BTN_Y, BTN_NORTH 0x133 as BTN_X.
        Assert.Equal(GamepadButton.Y, EvdevButtonMap.ToButton(EvdevCodes.BtnWest));
        Assert.Equal(GamepadButton.X, EvdevButtonMap.ToButton(EvdevCodes.BtnNorth));
        Assert.Equal(GamepadButton.LeftBumper, EvdevButtonMap.ToButton(EvdevCodes.BtnTl));
        Assert.Equal(GamepadButton.RightBumper, EvdevButtonMap.ToButton(EvdevCodes.BtnTr));
        Assert.Equal(GamepadButton.LeftTrigger, EvdevButtonMap.ToButton(EvdevCodes.BtnTl2));
        Assert.Equal(GamepadButton.RightTrigger, EvdevButtonMap.ToButton(EvdevCodes.BtnTr2));
        Assert.Equal(GamepadButton.Back, EvdevButtonMap.ToButton(EvdevCodes.BtnSelect));
        Assert.Equal(GamepadButton.Start, EvdevButtonMap.ToButton(EvdevCodes.BtnStart));
        Assert.Equal(GamepadButton.Guide, EvdevButtonMap.ToButton(EvdevCodes.BtnMode));
        Assert.Equal(GamepadButton.LeftStick, EvdevButtonMap.ToButton(EvdevCodes.BtnThumbL));
        Assert.Equal(GamepadButton.RightStick, EvdevButtonMap.ToButton(EvdevCodes.BtnThumbR));
        // input-event-codes.h: BTN_DPAD_UP/DOWN/LEFT/RIGHT = 0x220..0x223.
        Assert.Equal(GamepadButton.DPadUp, EvdevButtonMap.ToButton(0x220));
        Assert.Equal(GamepadButton.DPadDown, EvdevButtonMap.ToButton(0x221));
        Assert.Equal(GamepadButton.DPadLeft, EvdevButtonMap.ToButton(0x222));
        Assert.Equal(GamepadButton.DPadRight, EvdevButtonMap.ToButton(0x223));
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
        // Core contract: positive stick Y means "pushed up". evdev's ABS_Y is positive when
        // pushed DOWN, so a raw maximum must normalize to -1, not +1.
        Assert.Equal(-1f, state.LeftStickY);
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
    public void Unmapped_Button_Code_Is_Surfaced_As_An_Extra_Button()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x149, 1));

        Assert.Contains(0x149, tracker.Snapshot().ExtraButtons);
        Assert.Equal(GamepadButtonMask.None, tracker.Snapshot().PressedButtons);
    }

    [Fact]
    public void Releasing_An_Extra_Button_Clears_It()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x149, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x149, 0));

        Assert.Empty(tracker.Snapshot().ExtraButtons);
    }

    [Fact]
    public void Several_Extra_Buttons_Are_Tracked_Independently()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x149, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x14a, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x149, 0));

        Assert.Equal([0x14a], tracker.Snapshot().ExtraButtons);
    }

    [Fact]
    public void Keyboard_Key_Codes_Are_Not_Treated_As_Extra_Buttons()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x01, 1));

        Assert.Empty(tracker.Snapshot().ExtraButtons);
    }

    [Fact]
    public void Reset_Clears_Extra_Buttons_Too()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x149, 1));
        tracker.Reset();

        Assert.Empty(tracker.Snapshot().ExtraButtons);
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

    [Fact]
    public void BTN_C_And_BTN_Z_Have_No_Logical_Position()
    {
        // input-event-codes.h: BTN_C 0x132, BTN_Z 0x135. The Xbox layout that GamepadButton
        // follows has exactly four face buttons, so C and Z stay raw-code extras.
        Assert.Null(EvdevButtonMap.ToButton(0x132));
        Assert.Null(EvdevButtonMap.ToButton(0x135));
    }

    [Fact]
    public void BTN_GEAR_DOWN_And_Up_Have_No_Logical_Position()
    {
        // input-event-codes.h: BTN_GEAR_DOWN 0x150 (also #defined as BTN_WHEEL), BTN_GEAR_UP
        // 0x151. 0x150 is dual-named, so it is not reliably a menu button; both stay extras.
        Assert.Null(EvdevButtonMap.ToButton(0x150));
        Assert.Null(EvdevButtonMap.ToButton(0x151));
    }

    [Fact]
    public void Dpad_Key_Codes_Drive_The_Same_Logical_Buttons_As_The_Hat_Axis()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x220, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x222, 1));

        var state = tracker.Snapshot();

        Assert.True(state.IsPressed(GamepadButton.DPadUp));
        Assert.True(state.IsPressed(GamepadButton.DPadLeft));
        Assert.False(state.IsPressed(GamepadButton.DPadDown));
        Assert.False(state.IsPressed(GamepadButton.DPadRight));
        Assert.Empty(state.ExtraButtons);
    }

    [Fact]
    public void Dpad_Key_Release_Clears_The_Logical_Button()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x221, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x221, 0));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.DPadDown));
    }

    [Theory]
    [InlineData(0x2c0)]
    [InlineData(0x2c1)]
    [InlineData(0x2e7)]
    public void Trigger_Happy_Codes_Stay_Raw_Extra_Buttons(int code)
    {
        // input-event-codes.h: BTN_TRIGGER_HAPPY1 0x2c0 .. BTN_TRIGGER_HAPPY40 0x2e7. No
        // logical equivalent exists, and IsButtonCode accepts 0x100..0x2FF, so they must
        // surface as raw-code extras rather than being dropped.
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, (ushort)code, 1));

        var state = tracker.Snapshot();

        Assert.Contains(code, state.ExtraButtons);
        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
    }

    [Theory]
    [InlineData(0x132)]
    [InlineData(0x135)]
    [InlineData(0x150)]
    [InlineData(0x151)]
    public void Codes_With_No_Logical_Position_Surface_As_Raw_Extra_Buttons(int code)
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, (ushort)code, 1));

        var state = tracker.Snapshot();

        Assert.Contains(code, state.ExtraButtons);
        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
    }

    [Fact]
    public void Stick_Y_Is_Positive_When_Pushed_Up_On_Both_Sticks()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, -32768));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRy, -32768));

        var state = tracker.Snapshot();

        Assert.Equal(1f, state.LeftStickY);
        Assert.Equal(1f, state.RightStickY);
    }

    [Fact]
    public void Stick_Y_Is_Negative_When_Pushed_Down_On_Both_Sticks()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, 32767));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRy, 32767));

        var state = tracker.Snapshot();

        Assert.Equal(-1f, state.LeftStickY);
        Assert.Equal(-1f, state.RightStickY);
    }

    [Fact]
    public void Stick_X_Keeps_Its_Sign_On_Both_Sticks()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRx, 32767));

        var state = tracker.Snapshot();

        Assert.Equal(-1f, state.LeftStickX);
        Assert.Equal(1f, state.RightStickX);
    }

    [Fact]
    public void Stick_Y_Negation_Is_Linear_Through_The_Midpoint()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsY, 0));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRy, 0));

        var state = tracker.Snapshot();

        Assert.Equal(0f, state.LeftStickY, 4);
        Assert.Equal(0f, state.RightStickY, 4);
    }
}
