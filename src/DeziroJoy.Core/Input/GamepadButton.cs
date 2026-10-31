namespace DeziroJoy.Core.Input;

/// <summary>
/// Logical gamepad buttons using the Xbox layout naming convention.
/// Platform layers normalize their raw button codes (XInput bitmask, evdev BTN_*) onto this set.
/// </summary>
public enum GamepadButton
{
    A,
    B,
    X,
    Y,
    LeftBumper,
    RightBumper,
    LeftTrigger,
    RightTrigger,
    Back,
    Start,
    Guide,
    LeftStick,
    RightStick,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
}
