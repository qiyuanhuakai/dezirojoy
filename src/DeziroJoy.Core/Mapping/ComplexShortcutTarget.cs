namespace DeziroJoy.Core.Mapping;

/// <summary>
/// A hand-written "复杂快捷键" program. The script is a small zero-dependency DSL
/// (see <see cref="ComplexShortcutDsl"/>) that compiles to a bounded list of
/// <see cref="ComplexShortcutStep"/>s and runs from a button's 长按 (see
/// <see cref="Engine.ComplexShortcutRuntime"/>). It only ever emits standard
/// <see cref="Engine.InputCommand"/>s - no eval, no reflection, no shell.
/// </summary>
/// <remarks>
/// The script text is the single source of truth and what profiles persist: the step
/// list is compiled from it when the engine is built and again when a long press
/// starts the program. The example from the mission reads
/// <c>hold Alt\nevery 1s tap Tab</c>: while the button is held, Alt stays down and
/// Tab is tapped once per second.
/// </remarks>
public sealed record ComplexShortcutTarget : MappingTarget
{
    /// <summary>The DSL source, one step per line. Parsed by <see cref="ComplexShortcutDsl.Parse"/>.</summary>
    public required string Script { get; init; }
}

/// <summary>One step of a compiled complex shortcut. The set is closed: DSL text is the only authoring form.</summary>
public abstract record ComplexShortcutStep;

/// <summary>Presses a key and releases it again in the same tick.</summary>
public sealed record ComplexShortcutTapStep(GamepadKey Key) : ComplexShortcutStep;

/// <summary>Presses a key and keeps it down until the program stops or a matching release step.</summary>
public sealed record ComplexShortcutHoldStep(GamepadKey Key) : ComplexShortcutStep;

/// <summary>Releases a key a previous hold step is keeping down; releasing an unheld key does nothing.</summary>
public sealed record ComplexShortcutReleaseStep(GamepadKey Key) : ComplexShortcutStep;

/// <summary>Pauses the program for a duration; time left over in the tick carries into the next step.</summary>
public sealed record ComplexShortcutWaitStep(double Seconds) : ComplexShortcutStep;

/// <summary>
/// Taps a key every interval for as long as the program runs. Registering the timer
/// costs no time, so steps after it still run in order; a tick covering several
/// intervals fires every tap it covers.
/// </summary>
public sealed record ComplexShortcutEveryStep(double Interval, GamepadKey Key) : ComplexShortcutStep;
