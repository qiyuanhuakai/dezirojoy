using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>What a mapping does when triggered.</summary>
[JsonDerivedType(typeof(KeyPressTarget), "key")]
[JsonDerivedType(typeof(KeyComboTarget), "combo")]
[JsonDerivedType(typeof(MouseButtonTarget), "mouseButton")]
[JsonDerivedType(typeof(MouseMoveTarget), "mouseMove")]
[JsonDerivedType(typeof(WheelScrollTarget), "wheel")]
[JsonDerivedType(typeof(SpecialKeyTarget), "specialKey")]
[JsonDerivedType(typeof(ComplexShortcutTarget), "complexShortcut")]
[JsonDerivedType(typeof(NoneTarget), "none")]
public abstract record MappingTarget;

/// <summary>Hold-to-key mapping: key goes down on press, up on release.</summary>
public sealed record KeyPressTarget(GamepadKey Key) : MappingTarget;

/// <summary>Tap combo on press edge (e.g. Ctrl+C): modifiers down, key tap, modifiers up.</summary>
public sealed record KeyComboTarget(KeyModifiers Modifiers, GamepadKey Key) : MappingTarget;

/// <summary>Mouse button mapping: down on press, up on release.</summary>
public sealed record MouseButtonTarget(MouseButton Button) : MappingTarget;

/// <summary>Stick to mouse movement with modern sensitivity controls.</summary>
public sealed record MouseMoveTarget(MouseStickSettings Settings) : MappingTarget;

/// <summary>Stick to mouse wheel scrolling.</summary>
public sealed record WheelScrollTarget(ScrollStickSettings Settings) : MappingTarget;

/// <summary>A gesture action that deliberately does nothing: the gesture resolves, nothing is emitted.</summary>
public sealed record NoneTarget : MappingTarget;
