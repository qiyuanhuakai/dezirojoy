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

    /// <summary>A tracker that knows exactly one axis, with the rest value it was seeded with.</summary>
    private static EvdevGamepadStateTracker TrackerWith(ushort axis, EvdevAxisRange range)
        => new(new Dictionary<ushort, EvdevAxisRange> { [axis] = range });

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
    [InlineData(115, false)]
    [InlineData(116, false)] // inside the hysteresis band, starts released
    [InlineData(139, false)] // inside the hysteresis band
    [InlineData(140, true)]
    [InlineData(200, true)]
    [InlineData(255, true)]
    public void Analog_Triggers_Press_Only_Above_The_Hysteresis_Band(int value, bool expectedPressed)
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, value));

        Assert.Equal(expectedPressed, tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Trigger_Holds_Its_State_Inside_The_Hysteresis_Band()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 200));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        // Easing into the band keeps the trigger pressed: only crossing the release
        // point (<= 115) lets go.
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 139));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 116));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 115));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        // Re-entering the band from below keeps it released until the press point.
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 116));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 139));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 140));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Midpoint_Noise_Does_Not_Chatter_The_Trigger()
    {
        // M29: absZ alternating 126/128 flipped the pressed flag 9 times in 10 samples.
        // The mapping engine is edge-driven, so the same chatter injected BeginHold +
        // EndHold pairs at up to 500 Hz while the visualizer highlight snapped on and
        // off. Inside the hysteresis band the state must not move, so neither symptom
        // can be produced downstream.
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 0));
        for (var i = 0; i < 50; i++)
        {
            tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, i % 2 == 0 ? 126 : 128));

            Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void Band_Noise_Does_Not_Release_A_Held_Trigger()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 200));

        for (var i = 0; i < 24; i++)
        {
            tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 116 + i));

            Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void Left_And_Right_Triggers_Hold_Independent_Hysteresis_State()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 200));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRz, 120));

        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));

        // RT crossing its own press point must not disturb LT's held state, and LT
        // resting in the band must not leak into RT either.
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRz, 150));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 130));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void A_Trigger_Held_At_Startup_Presses_On_The_First_Sample()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 255));

        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Trigger_Resting_At_Startup_Stays_Released()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 130));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Reset_Clears_The_Trigger_Hysteresis_State()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 200));
        tracker.Reset();
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 130));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Trigger_Thresholds_Scale_With_The_Declared_Axis_Range()
    {
        // Thresholds are fractions of the declared travel, so a pad that reports AbsZ
        // on a 0..1023 range presses at >= 562 and releases at <= 461, not at the raw
        // 0..255 numbers.
        var tracker = new EvdevGamepadStateTracker(new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsZ] = new EvdevAxisRange(0, 1023),
        });

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 500));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 562));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 500));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 461));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Unknown_Event_Codes_Are_Ignored()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvKey, 0x999, 1));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, 0x999, 1234));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvSyn, 0, 0));

        Assert.Equal(GamepadButtonMask.None, tracker.Snapshot().PressedButtons);

        // Seeded, not moved: the canonical -32768..32767 range has an odd span, so its rest
        // value 0 normalizes to 1.5e-5 rather than an exact 0 - the same value an event
        // reporting 0 produces, which is why the stick tests above use precision 4.
        Assert.Equal(0f, tracker.Snapshot().LeftStickX, 4);
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

    [Fact]
    public void AbsGas_Presses_The_Right_Trigger_And_AbsBrake_The_Left()
    {
        // input-event-codes.h: ABS_GAS 0x09, ABS_BRAKE 0x0a. The kernel's HID simulation
        // mapping reports triggers on these codes for pads and wheels that use the
        // Simulation usage page instead of ABS_Z/ABS_RZ; gas accelerates (right trigger),
        // brake stops (left trigger), the side each pedal sits on.
        Assert.Equal((ushort)0x09, EvdevCodes.AbsGas);
        Assert.Equal((ushort)0x0a, EvdevCodes.AbsBrake);

        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 140));

        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsBrake, 140));

        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void Gas_And_Brake_Use_The_Same_Hysteresis_Band_As_Z_And_Rz()
    {
        var tracker = CreateTracker();

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 139));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsBrake, 139));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 140));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsBrake, 140));

        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 115));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsBrake, 115));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Gas_And_Brake_Thresholds_Scale_With_Their_Declared_Range()
    {
        // Same contract as ABS_Z on a 0..1023 range: the thresholds are fractions of the
        // declared travel, so a 10-bit gas axis presses at >= 562, not at the 0..255 numbers.
        var tracker = new EvdevGamepadStateTracker(new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsGas] = new EvdevAxisRange(0, 1023),
            [EvdevCodes.AbsBrake] = new EvdevAxisRange(0, 1023),
        });

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 500));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 562));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsBrake, 461));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsBrake, 562));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Trigger_Missing_From_The_Device_Ranges_Falls_Back_To_Its_Zero_Based_Default()
    {
        // EVIOCGABS failed for this axis (or the tracker was built partial): the fallback
        // must be the semantic 0..255, not the stick's -32768..32767, where a resting 0
        // would sit at half travel and leave the trigger a noise flick away from pressed.
        var tracker = new EvdevGamepadStateTracker(new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsX] = FullStickRange,
        });

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 140));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRz, 0));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void A_Stick_Missing_From_The_Device_Ranges_Falls_Back_To_The_Full_Range()
    {
        var tracker = new EvdevGamepadStateTracker(new Dictionary<ushort, EvdevAxisRange>());

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768));
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsRx, 32767));

        Assert.Equal(-1f, tracker.Snapshot().LeftStickX);
        Assert.Equal(1f, tracker.Snapshot().RightStickX);
    }

    [Fact]
    public void DefaultAxisRanges_Give_Triggers_A_Zero_Based_Range_And_Sticks_The_Full_Range()
    {
        // The fallback table itself, pinned: a trigger default of -32768..32767 would put
        // its rest value at half travel, and a stick default of 0..255 would halve it.
        Assert.Equal(new EvdevAxisRange(0, 255), EvdevGamepadStateTracker.DefaultAxisRanges[EvdevCodes.AbsZ]);
        Assert.Equal(new EvdevAxisRange(0, 255), EvdevGamepadStateTracker.DefaultAxisRanges[EvdevCodes.AbsRz]);
        Assert.Equal(new EvdevAxisRange(0, 255), EvdevGamepadStateTracker.DefaultAxisRanges[EvdevCodes.AbsGas]);
        Assert.Equal(new EvdevAxisRange(0, 255), EvdevGamepadStateTracker.DefaultAxisRanges[EvdevCodes.AbsBrake]);
        Assert.Equal(new EvdevAxisRange(-32768, 32767), EvdevGamepadStateTracker.DefaultAxisRanges[EvdevCodes.AbsX]);
        Assert.Equal(new EvdevAxisRange(-32768, 32767), EvdevGamepadStateTracker.DefaultAxisRanges[EvdevCodes.AbsRy]);
    }

    [Fact]
    public void The_First_Snapshot_Reflects_The_Rest_Values_The_Device_Reported()
    {
        // Open-time seeding: a stick resting ~10% off-center and a trigger resting at the
        // axis maximum must both show in the very first snapshot, before any event arrives.
        // Waiting for the first event left the display at the neutral default until the
        // user happened to move something.
        var tracker = new EvdevGamepadStateTracker(new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsX] = new EvdevAxisRange(-32768, 32767, Rest: 3276),
            [EvdevCodes.AbsZ] = new EvdevAxisRange(0, 255, Rest: 255),
        });

        var state = tracker.Snapshot();

        // 3276 of the 65535-wide canonical range is ~10% off center.
        Assert.Equal(0.1f, state.LeftStickX, 3);
        Assert.False(state.IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Stick_Resting_Off_Center_Seeds_Off_Center_With_The_Y_Inversion()
    {
        // ABS_Y rests at its raw maximum - pushed down - so the seeded state is -1, not +1.
        var tracker = new EvdevGamepadStateTracker(new Dictionary<ushort, EvdevAxisRange>
        {
            [EvdevCodes.AbsY] = new EvdevAxisRange(-32768, 32767, Rest: 32767),
        });

        Assert.Equal(-1f, tracker.Snapshot().LeftStickY);
    }

    [Fact]
    public void A_Forward_Trigger_Resting_At_Minimum_Keeps_Its_Usual_Orientation()
    {
        var tracker = TrackerWith(EvdevCodes.AbsZ, new EvdevAxisRange(0, 255, Rest: 0));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 139));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 140));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 115));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Reverse_Trigger_Resting_At_Maximum_Stays_Released_And_Presses_Toward_Minimum()
    {
        // The "扳机常亮" shape: a wheel or pedal axis that rests at the axis maximum. Read
        // forward, rest is full travel - permanently lit - so the seeded rest value orients
        // the travel: rest near max means pressed = toward min.
        var tracker = TrackerWith(EvdevCodes.AbsZ, new EvdevAxisRange(0, 255, Rest: 255));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        // Inverted travel of 139 is (255-139)/255 = 0.455: inside the band, still released.
        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 139));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 115));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 140));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 0));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Reverse_Trigger_On_A_1023_Scale_Inverts_The_Same_Way()
    {
        var tracker = TrackerWith(EvdevCodes.AbsZ, new EvdevAxisRange(0, 1023, Rest: 1023));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 500));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 461));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 562));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Reverse_Alternate_Trigger_Code_Is_Oriented_From_Its_Rest_Too()
    {
        // Orientation follows the seeded rest, not the axis code: an inverted ABS_GAS
        // behaves exactly like an inverted ABS_Z.
        var tracker = TrackerWith(EvdevCodes.AbsGas, new EvdevAxisRange(0, 255, Rest: 255));

        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 0));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsGas, 255));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void A_Rest_Value_At_Midpoint_Keeps_The_Forward_Orientation()
    {
        // Only a rest value strictly nearer the maximum inverts: a midpoint rest is
        // ambiguous, and forward is the conventional reading.
        var tracker = TrackerWith(EvdevCodes.AbsZ, new EvdevAxisRange(0, 255, Rest: 127));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 140));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 115));
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Reset_Clears_The_State_But_Keeps_The_Trigger_Orientation()
    {
        // Orientation is device calibration, not runtime state, so a Reset must not forget
        // it: after Reset the reverse trigger still reads pressed toward its minimum.
        var tracker = TrackerWith(EvdevCodes.AbsZ, new EvdevAxisRange(0, 255, Rest: 255));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 0));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Reset();
        Assert.False(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));

        tracker.Apply(new EvdevEvent(EvdevCodes.EvAbs, EvdevCodes.AbsZ, 0));
        Assert.True(tracker.Snapshot().IsPressed(GamepadButton.LeftTrigger));
    }
}
