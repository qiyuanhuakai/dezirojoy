namespace DeziroJoy.Core.Input;

/// <summary>
/// Immutable, allocation-free gamepad snapshot. Construct via <see cref="Create"/> to get
/// axis clamping; the record struct primary constructor is kept for deconstruction/with-expressions.
/// </summary>
public readonly record struct GamepadState : IGamepadState
{
    public GamepadState(
        GamepadButtonMask pressedButtons,
        float leftStickX,
        float leftStickY,
        float rightStickX,
        float rightStickY)
    {
        PressedButtons = pressedButtons;
        LeftStickX = Clamp(leftStickX);
        LeftStickY = Clamp(leftStickY);
        RightStickX = Clamp(rightStickX);
        RightStickY = Clamp(rightStickY);
    }

    public GamepadButtonMask PressedButtons { get; }

    public float LeftStickX { get; }

    public float LeftStickY { get; }

    public float RightStickX { get; }

    public float RightStickY { get; }

    public static GamepadState Neutral => default;

    /// <summary>Creates a state with stick axes clamped to [-1, 1] and NaN sanitized to 0.</summary>
    public static GamepadState Create(
        GamepadButtonMask pressedButtons,
        float leftStickX,
        float leftStickY,
        float rightStickX,
        float rightStickY)
        => new(pressedButtons, Sanitize(leftStickX), Sanitize(leftStickY), Sanitize(rightStickX), Sanitize(rightStickY));

    public bool IsPressed(GamepadButton button) => PressedButtons.Has(button);

    private static float Clamp(float value) => value switch
    {
        > 1f => 1f,
        < -1f => -1f,
        _ => value,
    };

    private static float Sanitize(float value) => float.IsNaN(value) ? 0f : Clamp(value);
}
