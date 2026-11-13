using System.Text.Json.Serialization;

namespace DeziroJoy.Core.Mapping;

/// <summary>
/// What starts an action sequence. The trigger is the one half of a program the engine
/// resolves on its own - the gesture machine in <c>ButtonGestureState</c> decides when a
/// press becomes a click, a double-click or a long press - and the sequence is the other
/// half, so a program never has to describe how it is started.
/// </summary>
/// <remarks>
/// <see cref="LongPress"/> is deliberately the zero value: it is the trigger every profile
/// written before this field existed has, so a profile that carries no trigger at all still
/// behaves exactly as it did.
/// </remarks>
public enum ActionTrigger
{
    /// <summary>The hold past the long-press threshold (长按).</summary>
    LongPress,

    /// <summary>The press edge of the mapping's own click (单击).</summary>
    Press,

    /// <summary>The second press inside the double-click window (双击).</summary>
    DoubleClick,

    /// <summary>The moment the click's activation ends - a tap, or a hold let go (松开).</summary>
    Release,
}

/// <summary>
/// How long a started sequence keeps running. The trigger says when it starts; this says
/// when it stops, which is what decouples "hold to keep it going" from "run it to the end"
/// from "press again to stop it".
/// </summary>
/// <remarks>
/// <see cref="WhileHeld"/> is the zero value because it is what the long-press programs
/// written before this field existed do: the program lives exactly as long as the press.
/// </remarks>
public enum ActionExecution
{
    /// <summary>Run while the source is held; releasing it stops the sequence (按住期间执行).</summary>
    WhileHeld,

    /// <summary>Run the sequence to its end and stop by itself (执行一次).</summary>
    Once,

    /// <summary>Each trigger toggles: start when stopped, stop when running (再按切换).</summary>
    Toggle,
}

/// <summary>
/// What a trigger does while the sequence is already running. Only <see cref="Restart"/>
/// and <see cref="Once"/> can end a run on their own, so this is the question a
/// re-trigger asks: cut the current run short, leave it alone, or wait for it.
/// </summary>
/// <remarks>
/// <see cref="Restart"/> is the zero value: it is the behaviour a long press had before
/// this field existed, where a second long press simply ran the program again.
/// </remarks>
public enum ActionRepeat
{
    /// <summary>Stop the running sequence and start again from the first step (重新开始).</summary>
    Restart,

    /// <summary>Do nothing (忽略).</summary>
    Ignore,

    /// <summary>Let this run finish, then start again - at most one waiting (排队).</summary>
    Queue,
}

/// <summary>
/// A hand-written "复杂快捷键" program: a trigger, an action sequence and an execution
/// strategy. The script is a small zero-dependency DSL (see <see cref="ComplexShortcutDsl"/>)
/// that compiles to a bounded list of <see cref="ComplexShortcutStep"/>s and runs from a
/// button's gesture (see <see cref="Engine.ComplexShortcutRuntime"/>). It only ever emits
/// standard <see cref="Engine.InputCommand"/>s - no eval, no reflection, no shell.
/// </summary>
/// <remarks>
/// The three fields are the decoupling the editor and the DSL share: a trigger says when
/// the program starts, the steps say what it does, and the execution strategy says how it
/// ends. A simple program is a key tap on the press edge, which is exactly what the editor
/// builds; a complex one stays text. The script text is the single source of truth and what
/// profiles persist - the step list and the three fields are compiled from it.
/// <para>
/// Every field defaults to the long-press behaviour profiles have always had, so a script
/// written before any of them existed loads and runs unchanged.
/// </para>
/// </remarks>
public sealed record ComplexShortcutTarget : MappingTarget
{
    /// <summary>The trigger a script with no <c>trigger</c> line runs from.</summary>
    public const ActionTrigger DefaultTrigger = ActionTrigger.LongPress;

    /// <summary>The execution a script with no <c>run</c> line uses.</summary>
    public const ActionExecution DefaultExecution = ActionExecution.WhileHeld;

    /// <summary>The repeat policy a script with no <c>repeat</c> line uses.</summary>
    public const ActionRepeat DefaultRepeat = ActionRepeat.Restart;

    /// <summary>The DSL source, one step per line. Parsed by <see cref="ComplexShortcutDsl.Parse"/>.</summary>
    public required string Script { get; init; }

    /// <summary>
    /// What starts the program. Defaults to <see cref="ActionTrigger.LongPress"/> and is
    /// omitted from JSON at that value, so a profile written before the field existed is
    /// byte-identical to one written now.
    /// </summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActionTrigger Trigger { get; init; } = DefaultTrigger;

    /// <summary>How long the program keeps running once started. Defaults to <see cref="ActionExecution.WhileHeld"/>.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActionExecution Execution { get; init; } = DefaultExecution;

    /// <summary>What a trigger does while the program is already running. Defaults to <see cref="ActionRepeat.Restart"/>.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActionRepeat Repeat { get; init; } = DefaultRepeat;
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

/// <summary>
/// Taps a mouse button: down and up in the same tick, the way a
/// <see cref="MouseButtonTarget"/> behaves. A mouse button is the whole mouse vocabulary
/// the DSL offers - there is no pointer-move step, because a program that moves the pointer
/// is a stick mapping, not a shortcut.
/// </summary>
public sealed record ComplexShortcutClickStep(MouseButton Button) : ComplexShortcutStep
{
    /// <summary>How the step reads in a plain-language summary, rather than as the record dump.</summary>
    public override string ToString() => $"单击鼠标{Button switch
    {
        MouseButton.Left => "左键",
        MouseButton.Right => "右键",
        MouseButton.Middle => "中键",
        MouseButton.XButton1 => "侧键1",
        MouseButton.XButton2 => "侧键2",
        _ => "按键",
    }}";
}

/// <summary>
/// Types a short string as one step, the way a <see cref="TextTarget"/> does. The text is
/// the rest of the line, so it may contain spaces; it may not contain a line break, which
/// is what keeps the parser line-oriented.
/// </summary>
public sealed record ComplexShortcutTextStep(string Text) : ComplexShortcutStep
{
    /// <summary>How the step reads in a plain-language summary, rather than as the record dump.</summary>
    public override string ToString()
        => string.IsNullOrEmpty(Text) ? "输入文本" : $"输入文本「{Text}」";
}
