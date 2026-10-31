namespace DeziroJoy.Core.Input;

/// <summary>
/// Normalized snapshot of one gamepad. Digital buttons come as a bit set; stick axes are
/// clamped to [-1, 1]. This is the single input contract the mapping engine consumes,
/// regardless of the underlying platform (XInput, evdev, ...).
/// </summary>
public interface IGamepadState
{
    GamepadButtonMask PressedButtons { get; }

    float LeftStickX { get; }

    float LeftStickY { get; }

    float RightStickX { get; }

    float RightStickY { get; }
}
