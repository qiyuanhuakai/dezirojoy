using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Windows.Keys;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

public class XInputStateMapperTests
{
    private const ushort AllButtons = XInputNative.DpadUp
                                      | XInputNative.DpadDown
                                      | XInputNative.DpadLeft
                                      | XInputNative.DpadRight
                                      | XInputNative.Start
                                      | XInputNative.Back
                                      | XInputNative.LeftThumb
                                      | XInputNative.RightThumb
                                      | XInputNative.LeftShoulder
                                      | XInputNative.RightShoulder
                                      | XInputNative.Guide
                                      | XInputNative.A
                                      | XInputNative.B
                                      | XInputNative.X
                                      | XInputNative.Y;

    [Fact]
    public void Every_Logical_Button_Is_Mapped_From_The_XInput_Bitmask()
    {
        var state = XInputStateMapper.ToGamepadState(new XInputGamepad
        {
            Buttons = AllButtons,
            LeftTrigger = 255,
            RightTrigger = 255,
        });

        foreach (var button in Enum.GetValues<GamepadButton>().Where(b => b != GamepadButton.Extra))
        {
            Assert.True(state.IsPressed(button), $"{button} is not mapped from XInput");
        }
    }

    [Fact]
    public void XInput_Cannot_Report_Extra_Buttons()
    {
        // XInput's wButtons bitmask is a fixed 14-button set with no raw-code channel, so
        // unmapped inputs are a Linux/evdev-only escape hatch. The Windows provider must
        // report an empty set rather than invent a code.
        var state = XInputStateMapper.ToGamepadState(new XInputGamepad
        {
            Buttons = AllButtons,
            LeftTrigger = 255,
            RightTrigger = 255,
        });

        Assert.Empty(state.ExtraButtons);
    }

    [Fact]
    public void No_Buttons_Produces_A_Neutral_State()
    {
        var state = XInputStateMapper.ToGamepadState(default);

        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(115, false)]
    [InlineData(116, false)] // inside the hysteresis band, starts released
    [InlineData(139, false)] // inside the hysteresis band
    [InlineData(140, true)]
    [InlineData(200, true)]
    [InlineData(255, true)]
    public void Triggers_Press_Only_Above_The_Hysteresis_Band(byte triggerValue, bool expectedPressed)
    {
        var state = ToState(new XInputGamepad { LeftTrigger = triggerValue }, "press-band");

        Assert.Equal(expectedPressed, state.IsPressed(GamepadButton.LeftTrigger));
        Assert.False(state.IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void Trigger_Holds_Its_State_Inside_The_Hysteresis_Band()
    {
        const string Device = "hysteresis-band";

        Assert.True(ToState(new XInputGamepad { LeftTrigger = 200 }, Device).IsPressed(GamepadButton.LeftTrigger));

        // Easing into the band keeps the trigger pressed: only crossing the release
        // point (<= 115) lets go.
        Assert.True(ToState(new XInputGamepad { LeftTrigger = 139 }, Device).IsPressed(GamepadButton.LeftTrigger));
        Assert.True(ToState(new XInputGamepad { LeftTrigger = 116 }, Device).IsPressed(GamepadButton.LeftTrigger));
        Assert.False(ToState(new XInputGamepad { LeftTrigger = 115 }, Device).IsPressed(GamepadButton.LeftTrigger));

        // Re-entering the band from below keeps it released until the press point.
        Assert.False(ToState(new XInputGamepad { LeftTrigger = 116 }, Device).IsPressed(GamepadButton.LeftTrigger));
        Assert.False(ToState(new XInputGamepad { LeftTrigger = 139 }, Device).IsPressed(GamepadButton.LeftTrigger));
        Assert.True(ToState(new XInputGamepad { LeftTrigger = 140 }, Device).IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Midpoint_Noise_Does_Not_Chatter_The_Trigger()
    {
        // M29: bLeftToggle alternating 126/128 flipped the pressed flag 9 times in 10
        // samples. The mapping engine is edge-driven, so the same chatter injected
        // BeginHold + EndHold pairs at up to 500 Hz while the visualizer highlight
        // snapped on and off. Inside the hysteresis band the state must not move, so
        // neither symptom can be produced downstream.
        const string Device = "midpoint-noise";

        for (var i = 0; i < 50; i++)
        {
            var state = ToState(new XInputGamepad { LeftTrigger = (byte)(i % 2 == 0 ? 126 : 128) }, Device);

            Assert.False(state.IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void Band_Noise_Does_Not_Release_A_Held_Trigger()
    {
        const string Device = "band-noise";

        Assert.True(ToState(new XInputGamepad { LeftTrigger = 200 }, Device).IsPressed(GamepadButton.LeftTrigger));

        for (var i = 0; i < 24; i++)
        {
            var state = ToState(new XInputGamepad { LeftTrigger = (byte)(116 + i) }, Device);

            Assert.True(state.IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void Interleaved_Polls_Keep_Each_Devices_Trigger_State_Independent()
    {
        // XInputGamepadProvider.Poll forwards its device id, and the engine polls every
        // connected pad once per tick on one thread, so mapper calls interleave. Pad A
        // eases its left trigger into the band while pad B rests at 0: B's poll must not
        // reset A's held state, and A's band value must not press B's trigger either.
        const string PadA = "xinput:0";
        const string PadB = "xinput:1";

        Assert.True(ToState(new XInputGamepad { LeftTrigger = 200 }, PadA).IsPressed(GamepadButton.LeftTrigger));

        for (var i = 0; i < 10; i++)
        {
            Assert.False(ToState(new XInputGamepad { LeftTrigger = 0 }, PadB).IsPressed(GamepadButton.LeftTrigger));
            Assert.True(ToState(new XInputGamepad { LeftTrigger = 130 }, PadA).IsPressed(GamepadButton.LeftTrigger));
        }

        // Mirror image: B presses while A rests in the band from below.
        Assert.False(ToState(new XInputGamepad { LeftTrigger = 0 }, PadA).IsPressed(GamepadButton.LeftTrigger));
        Assert.True(ToState(new XInputGamepad { LeftTrigger = 200 }, PadB).IsPressed(GamepadButton.LeftTrigger));

        for (var i = 0; i < 10; i++)
        {
            Assert.False(ToState(new XInputGamepad { LeftTrigger = 130 }, PadA).IsPressed(GamepadButton.LeftTrigger));
            Assert.True(ToState(new XInputGamepad { LeftTrigger = 130 }, PadB).IsPressed(GamepadButton.LeftTrigger));
        }
    }

    [Fact]
    public void A_Trigger_Held_At_Startup_Presses_On_The_First_Sample()
    {
        var state = ToState(new XInputGamepad { LeftTrigger = 255 }, "startup-held");

        Assert.True(state.IsPressed(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void A_Trigger_Resting_At_Startup_Stays_Released()
    {
        var state = ToState(new XInputGamepad { LeftTrigger = 130 }, "startup-rest");

        Assert.False(state.IsPressed(GamepadButton.LeftTrigger));
    }

    private static GamepadState ToState(XInputGamepad pad, string deviceId)
        => XInputStateMapper.ToGamepadState(pad, deviceId);

    [Fact]
    public void Stick_Axes_Are_Normalized_To_Unit_Range()
    {
        var state = XInputStateMapper.ToGamepadState(new XInputGamepad
        {
            ThumbLX = -32768,
            ThumbLY = 32767,
            ThumbRX = 16384,
            ThumbRY = -16384,
        });

        Assert.Equal(-1f, state.LeftStickX);
        Assert.Equal(32767f / 32768f, state.LeftStickY, 5);
        Assert.Equal(0.5f, state.RightStickX, 5);
        Assert.Equal(-0.5f, state.RightStickY, 5);
    }

    [Fact]
    public void Individual_Button_Bits_Map_To_The_Right_Logical_Buttons()
    {
        Assert.Equal(GamepadButtonMask.A, MaskFor(XInputNative.A));
        Assert.Equal(GamepadButtonMask.B, MaskFor(XInputNative.B));
        Assert.Equal(GamepadButtonMask.X, MaskFor(XInputNative.X));
        Assert.Equal(GamepadButtonMask.Y, MaskFor(XInputNative.Y));
        Assert.Equal(GamepadButtonMask.DPadUp, MaskFor(XInputNative.DpadUp));
        Assert.Equal(GamepadButtonMask.DPadDown, MaskFor(XInputNative.DpadDown));
        Assert.Equal(GamepadButtonMask.DPadLeft, MaskFor(XInputNative.DpadLeft));
        Assert.Equal(GamepadButtonMask.DPadRight, MaskFor(XInputNative.DpadRight));
        Assert.Equal(GamepadButtonMask.LeftBumper, MaskFor(XInputNative.LeftShoulder));
        Assert.Equal(GamepadButtonMask.RightBumper, MaskFor(XInputNative.RightShoulder));
        Assert.Equal(GamepadButtonMask.Back, MaskFor(XInputNative.Back));
        Assert.Equal(GamepadButtonMask.Start, MaskFor(XInputNative.Start));
        Assert.Equal(GamepadButtonMask.Guide, MaskFor(XInputNative.Guide));
        Assert.Equal(GamepadButtonMask.LeftStick, MaskFor(XInputNative.LeftThumb));
        Assert.Equal(GamepadButtonMask.RightStick, MaskFor(XInputNative.RightThumb));
    }

    private static GamepadButtonMask MaskFor(ushort bits)
        => XInputStateMapper.ToGamepadState(new XInputGamepad { Buttons = bits }).PressedButtons;
}
