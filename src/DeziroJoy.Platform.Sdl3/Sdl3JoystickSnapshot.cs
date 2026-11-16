namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// One raw sample of a plain joystick: every button, every axis and every hat, as SDL reads
/// them. The interactive mapping flow compares two of these - the baseline taken when a step
/// starts and the frame that follows it - so "the user just moved this control" is decided
/// from data instead of from a guess about the device. Snapshots are immutable, which is what
/// makes the detection logic testable without SDL.
/// </summary>
public sealed record Sdl3JoystickSnapshot
{
    public static Sdl3JoystickSnapshot Empty { get; } = new([], [], []);

    public Sdl3JoystickSnapshot(
        IReadOnlyList<bool> buttons,
        IReadOnlyList<short> axes,
        IReadOnlyList<byte> hats)
    {
        Buttons = buttons;
        Axes = axes;
        Hats = hats;
    }

    /// <summary>Pressed state per joystick button index.</summary>
    public IReadOnlyList<bool> Buttons { get; }

    /// <summary>Value per joystick axis index, in SDL's signed 16-bit range.</summary>
    public IReadOnlyList<short> Axes { get; }

    /// <summary>SDL hat bitmask per hat index (1 up, 2 right, 4 down, 8 left; 0 centered).</summary>
    public IReadOnlyList<byte> Hats { get; }

    public bool Button(int index) => index >= 0 && index < Buttons.Count && Buttons[index];

    public short Axis(int index) => index >= 0 && index < Axes.Count ? Axes[index] : (short)0;

    public byte Hat(int index) => index >= 0 && index < Hats.Count ? Hats[index] : (byte)0;
}

/// <summary>Which raw control a captured token points at.</summary>
public enum Sdl3MappingControlKind
{
    Button,
    Axis,
    Hat,
}

/// <summary>
/// One raw control the user activated during capture, with the SDL mapping token that
/// addresses it. The token is derived here - not by the caller - so the sign conventions SDL's
/// own mapping parser expects are applied in exactly one place.
/// </summary>
/// <param name="Kind">Button, axis or hat.</param>
/// <param name="Index">The raw control index inside that kind.</param>
/// <param name="Token">
/// The mapping token, e.g. <c>b3</c>, <c>a0</c>, <c>a0~</c>, <c>+a5</c>, <c>-a5</c> or
/// <c>h0.1</c>.
/// </param>
public sealed record Sdl3MappingControl(Sdl3MappingControlKind Kind, int Index, string Token)
{
    /// <summary>Sign of the raw axis value that produced this control, or 0 for buttons/hats.</summary>
    public int AxisSign { get; init; }
}
