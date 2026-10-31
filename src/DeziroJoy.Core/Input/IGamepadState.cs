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

    /// <summary>
    /// Raw platform button codes that carry no <see cref="GamepadButton"/> position (a pedal,
    /// a macro button, ...). Empty on platforms that cannot report them - XInput's fixed
    /// 14-button bitmask has no raw-code channel, so only the Linux evdev layer fills this in.
    /// </summary>
    IReadOnlyList<int> ExtraButtons => Array.Empty<int>();
}
