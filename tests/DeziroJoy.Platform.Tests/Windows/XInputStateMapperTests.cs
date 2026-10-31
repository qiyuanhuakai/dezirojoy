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

        foreach (var button in Enum.GetValues<GamepadButton>())
        {
            Assert.True(state.IsPressed(button), $"{button} is not mapped from XInput");
        }
    }

    [Fact]
    public void No_Buttons_Produces_A_Neutral_State()
    {
        var state = XInputStateMapper.ToGamepadState(default);

        Assert.Equal(GamepadButtonMask.None, state.PressedButtons);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(127, false)]
    [InlineData(128, true)]
    [InlineData(255, true)]
    public void Triggers_Are_Digital_Above_The_Halfway_Threshold(byte triggerValue, bool expectedPressed)
    {
        var state = XInputStateMapper.ToGamepadState(new XInputGamepad { LeftTrigger = triggerValue });

        Assert.Equal(expectedPressed, state.IsPressed(GamepadButton.LeftTrigger));
        Assert.False(state.IsPressed(GamepadButton.RightTrigger));
    }

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
