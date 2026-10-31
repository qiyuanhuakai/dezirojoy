using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Windows.XInput;

/// <summary>
/// Pure normalization from the XInput gamepad struct to <see cref="GamepadState"/>.
/// Stick axes divide by 32768 so -32768..32767 maps to -1..~1; triggers are digital
/// above the 50% threshold.
/// </summary>
internal static class XInputStateMapper
{
    public static GamepadState ToGamepadState(in XInputGamepad pad)
    {
        var buttons = GamepadButtonMask.None;

        if ((pad.Buttons & XInputNative.DpadUp) != 0)
        {
            buttons |= GamepadButtonMask.DPadUp;
        }

        if ((pad.Buttons & XInputNative.DpadDown) != 0)
        {
            buttons |= GamepadButtonMask.DPadDown;
        }

        if ((pad.Buttons & XInputNative.DpadLeft) != 0)
        {
            buttons |= GamepadButtonMask.DPadLeft;
        }

        if ((pad.Buttons & XInputNative.DpadRight) != 0)
        {
            buttons |= GamepadButtonMask.DPadRight;
        }

        if ((pad.Buttons & XInputNative.Start) != 0)
        {
            buttons |= GamepadButtonMask.Start;
        }

        if ((pad.Buttons & XInputNative.Back) != 0)
        {
            buttons |= GamepadButtonMask.Back;
        }

        if ((pad.Buttons & XInputNative.LeftThumb) != 0)
        {
            buttons |= GamepadButtonMask.LeftStick;
        }

        if ((pad.Buttons & XInputNative.RightThumb) != 0)
        {
            buttons |= GamepadButtonMask.RightStick;
        }

        if ((pad.Buttons & XInputNative.LeftShoulder) != 0)
        {
            buttons |= GamepadButtonMask.LeftBumper;
        }

        if ((pad.Buttons & XInputNative.RightShoulder) != 0)
        {
            buttons |= GamepadButtonMask.RightBumper;
        }

        if ((pad.Buttons & XInputNative.Guide) != 0)
        {
            buttons |= GamepadButtonMask.Guide;
        }

        if ((pad.Buttons & XInputNative.A) != 0)
        {
            buttons |= GamepadButtonMask.A;
        }

        if ((pad.Buttons & XInputNative.B) != 0)
        {
            buttons |= GamepadButtonMask.B;
        }

        if ((pad.Buttons & XInputNative.X) != 0)
        {
            buttons |= GamepadButtonMask.X;
        }

        if ((pad.Buttons & XInputNative.Y) != 0)
        {
            buttons |= GamepadButtonMask.Y;
        }

        if (pad.LeftTrigger >= XInputNative.TriggerPressedThreshold)
        {
            buttons |= GamepadButtonMask.LeftTrigger;
        }

        if (pad.RightTrigger >= XInputNative.TriggerPressedThreshold)
        {
            buttons |= GamepadButtonMask.RightTrigger;
        }

        return GamepadState.Create(
            buttons,
            pad.ThumbLX / 32768f,
            pad.ThumbLY / 32768f,
            pad.ThumbRX / 32768f,
            pad.ThumbRY / 32768f);
    }
}
