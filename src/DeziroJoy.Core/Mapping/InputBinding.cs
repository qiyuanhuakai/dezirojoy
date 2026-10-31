using System.Text.Json.Serialization;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Core.Mapping;

/// <summary>What triggers a mapping: a button or an analog stick.</summary>
[JsonDerivedType(typeof(ButtonBinding), "button")]
[JsonDerivedType(typeof(StickBinding), "stick")]
public abstract record InputBinding;

public sealed record ButtonBinding(GamepadButton Button) : InputBinding;

public sealed record StickBinding(GamepadStick Stick) : InputBinding;
