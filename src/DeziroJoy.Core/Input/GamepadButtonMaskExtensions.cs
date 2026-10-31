namespace DeziroJoy.Core.Input;

public static class GamepadButtonMaskExtensions
{
    /// <summary>Converts a single button to its bit.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="GamepadButton.Extra"/> has no bit: a raw code can exceed the mask's width,
    /// so extra buttons are matched through <see cref="IGamepadState.ExtraButtons"/>.
    /// </exception>
    public static GamepadButtonMask ToMask(this GamepadButton button) => button switch
    {
        GamepadButton.A => GamepadButtonMask.A,
        GamepadButton.B => GamepadButtonMask.B,
        GamepadButton.X => GamepadButtonMask.X,
        GamepadButton.Y => GamepadButtonMask.Y,
        GamepadButton.LeftBumper => GamepadButtonMask.LeftBumper,
        GamepadButton.RightBumper => GamepadButtonMask.RightBumper,
        GamepadButton.LeftTrigger => GamepadButtonMask.LeftTrigger,
        GamepadButton.RightTrigger => GamepadButtonMask.RightTrigger,
        GamepadButton.Back => GamepadButtonMask.Back,
        GamepadButton.Start => GamepadButtonMask.Start,
        GamepadButton.Guide => GamepadButtonMask.Guide,
        GamepadButton.LeftStick => GamepadButtonMask.LeftStick,
        GamepadButton.RightStick => GamepadButtonMask.RightStick,
        GamepadButton.DPadUp => GamepadButtonMask.DPadUp,
        GamepadButton.DPadDown => GamepadButtonMask.DPadDown,
        GamepadButton.DPadLeft => GamepadButtonMask.DPadLeft,
        GamepadButton.DPadRight => GamepadButtonMask.DPadRight,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown gamepad button."),
    };

    public static bool Has(this GamepadButtonMask mask, GamepadButton button)
        => (mask & button.ToMask()) != 0;

    public static GamepadButtonMask Add(this GamepadButtonMask mask, GamepadButton button)
        => mask | button.ToMask();

    public static GamepadButtonMask Remove(this GamepadButtonMask mask, GamepadButton button)
        => mask & ~button.ToMask();
}
