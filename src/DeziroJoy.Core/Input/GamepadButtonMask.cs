namespace DeziroJoy.Core.Input;

/// <summary>
/// Allocation-free bit set of <see cref="GamepadButton"/> values, one bit per button.
/// </summary>
[Flags]
public enum GamepadButtonMask : uint
{
    None = 0,
    A = 1u << 0,
    B = 1u << 1,
    X = 1u << 2,
    Y = 1u << 3,
    LeftBumper = 1u << 4,
    RightBumper = 1u << 5,
    LeftTrigger = 1u << 6,
    RightTrigger = 1u << 7,
    Back = 1u << 8,
    Start = 1u << 9,
    Guide = 1u << 10,
    LeftStick = 1u << 11,
    RightStick = 1u << 12,
    DPadUp = 1u << 13,
    DPadDown = 1u << 14,
    DPadLeft = 1u << 15,
    DPadRight = 1u << 16,
}
