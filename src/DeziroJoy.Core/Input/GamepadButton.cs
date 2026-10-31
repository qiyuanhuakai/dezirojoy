namespace DeziroJoy.Core.Input;

/// <summary>
/// Logical gamepad buttons using the Xbox layout naming convention.
/// Platform layers normalize their raw button codes (XInput bitmask, evdev BTN_*) onto this set.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Extra"/> is the escape hatch for inputs that have no logical position: a pedal,
/// a macro pad button, an unusual "M" button. Such a button is identified by its raw platform
/// code (see <c>ButtonBinding.Code</c>) and is matched through
/// <see cref="IGamepadState.ExtraButtons"/> rather than through a <see cref="GamepadButtonMask"/>
/// bit, because a raw code can be far larger than the mask's 32 bits.
/// </para>
/// </remarks>
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
    Extra,
}
