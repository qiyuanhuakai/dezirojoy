using System.Text.Json.Serialization;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>What triggers a mapping: a button or an analog stick.</summary>
[JsonDerivedType(typeof(ButtonBinding), "button")]
[JsonDerivedType(typeof(StickBinding), "stick")]
public abstract record InputBinding;

/// <summary>
/// A button press. <paramref name="Code"/> carries the raw platform code and is only
/// meaningful when <paramref name="Button"/> is <see cref="GamepadButton.Extra"/>; it is
/// omitted from JSON while it holds its default so ordinary profiles stay unchanged.
/// </summary>
public sealed record ButtonBinding(
    GamepadButton Button,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Code = 0) : InputBinding
{
    /// <summary>
    /// Direction this button implies for a continuous target (mouse move / wheel). A button is
    /// binary, so it maps to one cardinal direction at full deflection rather than to an axis.
    /// Null for key / combo / mouse-button targets, where a direction is meaningless.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public InputDirection? Direction { get; init; }

    /// <summary>
    /// Optional gesture configuration (双击 / 长按). Null - the default - means no gesture is
    /// configured: the press edge fires the target immediately, adding no latency.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ButtonGesture? Gesture { get; init; }
}

/// <summary>
/// An analog stick. <see cref="Direction"/> turns the stick into a digital source for
/// key / combo / mouse-button targets; it stays null for mouse-move / wheel targets, which
/// consume both axes and need no direction.
/// </summary>
public sealed record StickBinding(GamepadStick Stick) : InputBinding
{
    /// <summary>
    /// Direction this stick acts as a switch for. Null (the default) keeps the stick
    /// omnidirectional, which is what mouse-move / wheel targets consume.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public InputDirection? Direction { get; init; }
}
