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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Code = 0) : InputBinding;

public sealed record StickBinding(GamepadStick Stick) : InputBinding;
