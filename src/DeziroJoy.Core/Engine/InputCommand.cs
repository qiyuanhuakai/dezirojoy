using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// One output action produced by the engine for a single tick. The engine is pure:
/// a thin dispatcher in the platform layer translates these into SendInput / uinput calls.
/// </summary>
public abstract record InputCommand;

public sealed record MouseMoveCommand(int Dx, int Dy) : InputCommand;

/// <summary>Wheel notches (1 notch = 120 units on Windows).</summary>
public sealed record WheelCommand(int HorizontalNotches, int VerticalNotches) : InputCommand;

/// <summary>
/// Press modifiers (in canonical order) then the key. The matching
/// <see cref="KeyUpCommand"/> releases the key first, then the modifiers.
/// </summary>
public sealed record KeyDownCommand(GamepadKey Key, KeyModifiers Modifiers) : InputCommand;

public sealed record KeyUpCommand(GamepadKey Key, KeyModifiers Modifiers) : InputCommand;

public sealed record MouseDownCommand(MouseButton Button) : InputCommand;

public sealed record MouseUpCommand(MouseButton Button) : InputCommand;

/// <summary>
/// Switches the active preset slot. Host-side only: the engine emits it for leader-key
/// mappings, and <see cref="Platform.CommandDispatcher"/> deliberately never forwards it
/// to the output sink - the host applies it by swapping the active profile.
/// </summary>
public sealed record SwitchPresetCommand(int TargetSlot) : InputCommand;
