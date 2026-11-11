using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The capability classifier, pinned in both directions. The old rule ("ABS_X and ABS_RX")
/// silently dropped arcade sticks and flight controls that report no right stick; the relaxed
/// rule must widen to those without mistaking a touchpad or a mouse for a gamepad - the two
/// failure modes are equally invisible in production, so both get tests.
/// </summary>
public class EvdevGamepadDetectionTests
{
    private static readonly ulong BothSticks =
        AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY) | AbsBit(EvdevCodes.AbsRx) | AbsBit(EvdevCodes.AbsRy);

    [Fact]
    public void A_Dual_Stick_Pad_With_The_Face_Cluster_Is_A_Gamepad()
    {
        var keys = KeyBits(EvdevCodes.BtnSouth, EvdevCodes.BtnEast, EvdevCodes.BtnNorth, EvdevCodes.BtnWest);

        Assert.True(EvdevGamepadProvider.LooksLikeGamepad(BothSticks, keys));
    }

    [Fact]
    public void A_Right_Stick_Alone_Is_A_Gamepad()
    {
        // Some devices report the right stick without ABS_X/ABS_Y (broken or partial HID
        // descriptors); a second stick is still the strongest signal there is.
        var abs = AbsBit(EvdevCodes.AbsRx) | AbsBit(EvdevCodes.AbsRy);

        Assert.True(EvdevGamepadProvider.LooksLikeGamepad(abs, []));
    }

    [Fact]
    public void An_Arcade_Stick_Without_A_Right_Stick_Is_A_Gamepad()
    {
        // The shape the old rule missed: ABS_X/ABS_Y, no ABS_RX, and the explicit joystick
        // button the USB HID descriptor gives arcade sticks.
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY);

        Assert.True(EvdevGamepadProvider.LooksLikeGamepad(abs, KeyBits(EvdevCodes.BtnJoystick)));
    }

    [Fact]
    public void An_Arcade_Stick_With_Trigger_Happy_Buttons_Is_A_Gamepad()
    {
        // Cheap encoders map their buttons onto BTN_TRIGGER_HAPPY1..8 instead of the face
        // cluster; still a gamepad, still no right stick.
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY);

        Assert.True(EvdevGamepadProvider.LooksLikeGamepad(abs, KeyBits(EvdevCodes.BtnTriggerHappy1)));
    }

    [Fact]
    public void A_Dpad_As_Keys_Is_A_Gamepad_Signal()
    {
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY);

        Assert.True(EvdevGamepadProvider.LooksLikeGamepad(abs, KeyBits(EvdevCodes.BtnDpadUp, EvdevCodes.BtnDpadDown)));
    }

    [Fact]
    public void A_Touchpad_Is_Not_A_Gamepad()
    {
        // The false positive the relaxed rule must not introduce: ABS_X/ABS_Y plus BTN_TOUCH
        // is exactly what every touchpad reports.
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY) | AbsBit(0x30); // ABS_MT_SLOT
        var keys = KeyBits(EvdevCodes.BtnTouch, 0x145); // BTN_TOUCH, BTN_TOOL_FINGER

        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(abs, keys));
    }

    [Fact]
    public void A_Touchscreen_Is_Not_A_Gamepad()
    {
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY);
        var keys = KeyBits(EvdevCodes.BtnTouch);

        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(abs, keys));
    }

    [Fact]
    public void A_Mouse_Is_Not_A_Gamepad()
    {
        // Mice report relative axes only: the ABS mask is empty, so no button can rescue it.
        var keys = KeyBits(EvdevCodes.BtnLeft, EvdevCodes.BtnRight, EvdevCodes.BtnMiddle);

        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(0, keys));
    }

    [Fact]
    public void A_Keyboard_Is_Not_A_Gamepad()
    {
        var keys = new byte[EvdevNative.KeyBitBufferSize];

        // A keyboard key code lives far below BTN_*; bit 30 (KEY_A) is the classic example.
        keys[30 / 8] |= (byte)(1 << (30 % 8));

        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(0, keys));
    }

    [Fact]
    public void A_Device_With_Axes_But_No_Gamepad_Buttons_Is_Not_A_Gamepad()
    {
        // Accelerometers, light sensors and the like: axes exist, no second stick, no explicit
        // gamepad button. Not something a user maps.
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsY) | AbsBit(EvdevCodes.AbsZ);

        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(abs, KeyBits(EvdevCodes.BtnLeft)));
    }

    [Fact]
    public void A_Racing_Wheel_Stays_Out_Until_It_Reports_A_Gamepad_Signal()
    {
        // Documented limitation, pinned so a future widening is a deliberate decision:
        // ABS_X + pedals + gear buttons carry none of the accepted signals.
        var abs = AbsBit(EvdevCodes.AbsX) | AbsBit(EvdevCodes.AbsZ);
        var keys = KeyBits(EvdevCodes.BtnGearDown, EvdevCodes.BtnGearUp);

        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(abs, keys));
    }

    [Fact]
    public void A_Short_Key_Mask_Never_Reads_Out_Of_Bounds()
    {
        // A caller that only filled part of the EV_KEY buffer must not crash: BTN_SOUTH sits
        // at bit 0x130, past the first eight bytes.
        Assert.False(EvdevGamepadProvider.LooksLikeGamepad(AbsBit(EvdevCodes.AbsX), new byte[4]));
    }

    private static ulong AbsBit(ushort code) => 1UL << code;

    private static byte[] KeyBits(params ushort[] codes)
    {
        var bits = new byte[EvdevNative.KeyBitBufferSize];

        foreach (var code in codes)
        {
            bits[code / 8] |= (byte)(1 << (code % 8));
        }

        return bits;
    }
}
