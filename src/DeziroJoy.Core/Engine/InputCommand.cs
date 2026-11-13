using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// One output action produced by the engine for a single tick. The engine is pure:
/// a thin dispatcher in the platform layer translates these into SendInput / uinput calls.
/// </summary>
public abstract record InputCommand;

/// <summary>
/// Which mapping or macro instance asked for an output. Every key, modifier and mouse
/// button the dispatcher holds is refcounted per owner: the output goes down when its first
/// owner presses it and comes back up only when the last one releases it, so two mappings
/// sharing Ctrl never lift each other's modifier, and revoking one owner - a cancelled
/// gesture, a disconnected device - lifts only what that owner holds.
/// <para>
/// An owner is deliberately not part of a command's identity: two ticks that ask for the
/// same key are the same command whatever asked for them, so the command records compare
/// their output only.
/// </para>
/// </summary>
public readonly record struct OutputOwner
{
    /// <summary>No owner: an untagged command, counted as one anonymous owner.</summary>
    public static OutputOwner None => default;

    public OutputOwner(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; }

    public bool IsNone => Id == Guid.Empty;

    /// <summary>
    /// A short tag rather than the record's full dump: the owner is dispatch metadata, and a
    /// failing assertion is easier to read when it names the output, not the bookkeeping.
    /// </summary>
    public override string ToString() => IsNone ? "none" : Id.ToString("n")[..8];
}

public sealed record MouseMoveCommand(int Dx, int Dy) : InputCommand;

/// <summary>Wheel notches (1 notch = 120 units on Windows).</summary>
public sealed record WheelCommand(int HorizontalNotches, int VerticalNotches) : InputCommand;

/// <summary>
/// Press modifiers (in canonical order) then the key. The matching
/// <see cref="KeyUpCommand"/> releases the key first, then the modifiers.
/// </summary>
public sealed record KeyDownCommand(GamepadKey Key, KeyModifiers Modifiers, OutputOwner Owner = default) : InputCommand
{
    // The owner is dispatch metadata, not part of what the command does, so equality and
    // hashing look at the output alone.
    public bool Equals(KeyDownCommand? other) => other is not null && Key == other.Key && Modifiers == other.Modifiers;

    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
}

public sealed record KeyUpCommand(GamepadKey Key, KeyModifiers Modifiers, OutputOwner Owner = default) : InputCommand
{
    public bool Equals(KeyUpCommand? other) => other is not null && Key == other.Key && Modifiers == other.Modifiers;

    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
}

/// <summary>
/// Types a short Unicode string in one go. The engine emits it for a
/// <see cref="Mapping.TextTarget"/> on the press edge; the dispatcher hands it straight to
/// the sink, which sends it through its own text path - Windows KEYEVENTF_UNICODE, Linux
/// through the virtual keyboard - rather than as a sequence of key events the local layout
/// may not be able to name.
/// </summary>
public sealed record TextCommand(string Text) : InputCommand;

public sealed record MouseDownCommand(MouseButton Button, OutputOwner Owner = default) : InputCommand
{
    public bool Equals(MouseDownCommand? other) => other is not null && Button == other.Button;

    public override int GetHashCode() => Button.GetHashCode();
}

public sealed record MouseUpCommand(MouseButton Button, OutputOwner Owner = default) : InputCommand
{
    public bool Equals(MouseUpCommand? other) => other is not null && Button == other.Button;

    public override int GetHashCode() => Button.GetHashCode();
}

/// <summary>
/// Switches the active preset slot. Host-side only: the engine emits it for leader-key
/// mappings, and <see cref="Platform.CommandDispatcher"/> deliberately never forwards it
/// to the output sink - the host applies it by swapping the active profile.
/// </summary>
public sealed record SwitchPresetCommand(int TargetSlot) : InputCommand;
