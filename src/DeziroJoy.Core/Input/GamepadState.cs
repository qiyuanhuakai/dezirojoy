namespace DeziroJoy.Core.Input;

/// <summary>
/// Immutable, allocation-free gamepad snapshot. Construct via <see cref="Create"/> to get
/// axis clamping; the record struct primary constructor is kept for deconstruction/with-expressions.
/// </summary>
/// <remarks>
/// <see cref="ExtraButtons"/> stays null unless a platform actually reports raw codes, so the
/// common path allocates nothing.
/// </remarks>
public readonly record struct GamepadState : IGamepadState
{
    private readonly IReadOnlyList<int>? _extraButtons;

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

    /// <summary>
    /// Raw platform button codes with no logical <see cref="GamepadButton"/> position. Empty
    /// unless the platform reported some; see <see cref="IGamepadState.ExtraButtons"/>.
    /// </summary>
    public IReadOnlyList<int> ExtraButtons
    {
        get => _extraButtons ?? Array.Empty<int>();
        init => _extraButtons = value;
    }

    public static GamepadState Neutral => default;

    /// <summary>Creates a state with stick axes clamped to [-1, 1] and NaN sanitized to 0.</summary>
    public static GamepadState Create(
        GamepadButtonMask pressedButtons,
        float leftStickX,
        float leftStickY,
        float rightStickX,
        float rightStickY,
        IReadOnlyList<int>? extraButtons = null)
        => new(pressedButtons, Sanitize(leftStickX), Sanitize(leftStickY), Sanitize(rightStickX), Sanitize(rightStickY))
        {
            ExtraButtons = extraButtons ?? Array.Empty<int>(),
        };

    public bool IsPressed(GamepadButton button) => PressedButtons.Has(button);

    private static float Clamp(float value) => value switch
    {
        > 1f => 1f,
        < -1f => -1f,
        _ => value,
    };

    private static float Sanitize(float value) => float.IsNaN(value) ? 0f : Clamp(value);
}
