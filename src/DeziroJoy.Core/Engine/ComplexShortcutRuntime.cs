using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// One running complex shortcut program. Deterministic and clock-free: every transition
/// is driven by the <paramref name="deltaSeconds"/> the engine threads through
/// <see cref="Tick"/>, exactly like the gesture machine. A program starts when its trigger
/// resolves - a press, a release, a double-click or a long press - runs for as long as its
/// execution strategy says, and stops on the release tick or on its own completion,
/// releasing every key it held - so a stopped program can never leave a key stuck down or
/// a periodic tap still running.
/// </summary>
/// <remarks>
/// <para>
/// Steps run in order. <see cref="ComplexShortcutTapStep"/> emits a down/up pair,
/// <see cref="ComplexShortcutHoldStep"/> presses a key the program then owns until it stops
/// or a <see cref="ComplexShortcutReleaseStep"/> releases it, <see cref="ComplexShortcutClickStep"/>
/// taps a mouse button, <see cref="ComplexShortcutTextStep"/> types a string, and
/// <see cref="ComplexShortcutWaitStep"/> pauses the sequence - time left over in a tick
/// carries into the next step, so one large tick runs every step it covers.
/// </para>
/// <para>
/// A <see cref="ComplexShortcutEveryStep"/> registers a periodic tap that fires every
/// interval for as long as the program runs; registering costs no time, so steps after it
/// still run in order. A tick covering several intervals fires every tap it covers and the
/// remainder carries on - the same "never lose a unit" rule the gesture machine's
/// auto-repeat applies, with the same one-microsecond tolerance so a run of 0.1s ticks
/// cannot drift a tap out of existence - up to the per-tick catch-up budget
/// <see cref="ButtonGestureState.MaxCatchUpUnits"/>, so a tick that covers far more time than
/// the interval emits a bounded burst instead of one tap per interval it happened to cover.
/// </para>
/// <para>
/// A timer only exists from the moment its every step runs: within the tick that registers
/// it, the timer is credited the time left after the waits that preceded it, while timers
/// from earlier ticks take the whole tick. Within one tick the sequence's commands come
/// first, then the periodic taps in registration order. A tap due after the release
/// boundary never fires, because the release tick stops the program before it is advanced.
/// </para>
/// <para>
/// The execution strategy decides when a program ends on its own. <see cref="ActionExecution.WhileHeld"/>
/// never does - it lives exactly as long as the press that started it. <see cref="ActionExecution.Once"/>
/// stops the moment its sequence is exhausted and no timer is left, releasing what it held,
/// which is what makes "hold for half a second, then type this" a program that finishes
/// without anybody letting go. <see cref="ActionExecution.Toggle"/> is stopped by the next
/// trigger, never by a release.
/// </para>
/// </remarks>
public sealed class ComplexShortcutRuntime
{
    private readonly IReadOnlyList<ComplexShortcutStep> _steps;
    private readonly List<EveryTimer> _timers = [];
    private readonly List<GamepadKey> _heldKeys = [];
    private readonly ActionExecution _execution;
    private readonly ActionTrigger _trigger;
    private readonly ActionRepeat _repeat;
    private int _position;
    private double _waitRemaining;
    private bool _running;

    private ComplexShortcutRuntime(
        IReadOnlyList<ComplexShortcutStep> steps,
        OutputOwner owner,
        ActionTrigger trigger,
        ActionExecution execution,
        ActionRepeat repeat)
    {
        _steps = steps;
        _execution = execution;
        _trigger = trigger;
        _repeat = repeat;
        Owner = owner;
    }

    /// <summary>
    /// Compiles a target into a stopped runtime. <paramref name="owner"/> names who the
    /// program's held keys belong to - one macro instance, refcounted apart from the mapping
    /// that started it - and defaults to the anonymous owner for a directly driven runtime.
    /// </summary>
    /// <exception cref="ComplexShortcutParseException">The script is malformed; the message is Chinese and carries line and column.</exception>
    public static ComplexShortcutRuntime Compile(ComplexShortcutTarget target, OutputOwner owner = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new(
            ComplexShortcutDsl.Parse(target.Script),
            owner.IsNone ? new OutputOwner(Guid.NewGuid()) : owner,
            target.Trigger,
            target.Execution,
            target.Repeat);
    }

    /// <summary>
    /// Compiles a script into a stopped runtime, reading its header lines through
    /// <see cref="ComplexShortcutDsl.ParseTarget"/> so a script and a configured target are
    /// the same program.
    /// </summary>
    /// <exception cref="ComplexShortcutParseException">The script is malformed; the message is Chinese and carries line and column.</exception>
    public static ComplexShortcutRuntime Compile(string script, OutputOwner owner = default)
        => Compile(ComplexShortcutDsl.ParseTarget(script), owner);

    /// <summary>
    /// The press/release order the program produces, with no clock involved: every
    /// instantaneous step in order, one firing per periodic timer, and the keys a hold step
    /// pressed released at the end exactly the way <see cref="Stop"/> releases them. Waits
    /// produce no output, so what is left is the sequence of outputs the user actually gets
    /// - the preview the editor shows beside the script.
    /// </summary>
    public static IReadOnlyList<InputCommand> Preview(ComplexShortcutTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var commands = new List<InputCommand>();
        var held = new List<GamepadKey>();

        foreach (var step in ComplexShortcutDsl.Parse(target.Script))
        {
            switch (step)
            {
                case ComplexShortcutTapStep tap:
                    commands.Add(new KeyDownCommand(tap.Key, KeyModifiers.None));
                    commands.Add(new KeyUpCommand(tap.Key, KeyModifiers.None));
                    break;

                case ComplexShortcutHoldStep hold:
                    // Holding an already-held key is a no-op, exactly as the runtime treats it.
                    if (!held.Contains(hold.Key))
                    {
                        held.Add(hold.Key);
                        commands.Add(new KeyDownCommand(hold.Key, KeyModifiers.None));
                    }

                    break;

                case ComplexShortcutReleaseStep release:
                    if (held.Remove(release.Key))
                    {
                        commands.Add(new KeyUpCommand(release.Key, KeyModifiers.None));
                    }

                    break;

                case ComplexShortcutEveryStep every:
                    commands.Add(new KeyDownCommand(every.Key, KeyModifiers.None));
                    commands.Add(new KeyUpCommand(every.Key, KeyModifiers.None));
                    break;

                case ComplexShortcutClickStep click:
                    commands.Add(new MouseDownCommand(click.Button));
                    commands.Add(new MouseUpCommand(click.Button));
                    break;

                case ComplexShortcutTextStep text:
                    commands.Add(new TextCommand(text.Text));
                    break;
            }
        }

        // The same reverse press order Stop unwinds a hold in.
        for (var i = held.Count - 1; i >= 0; i--)
        {
            commands.Add(new KeyUpCommand(held[i], KeyModifiers.None));
        }

        return commands;
    }

    /// <summary>Who the keys this program holds belong to.</summary>
    public OutputOwner Owner { get; }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning => _running;

    /// <summary>How this program ends: on a release, on its own completion, or on the next trigger.</summary>
    public ActionExecution Execution => _execution;

    /// <summary>What starts this program, the way its target's header says it.</summary>
    public ActionTrigger Trigger => _trigger;

    /// <summary>What a second trigger does while this program is already running.</summary>
    public ActionRepeat Repeat => _repeat;

    /// <summary>
    /// True when the sequence has run past its last step and no periodic timer is left:
    /// the program has nothing further to emit. A program that is finished but still
    /// running - a <see cref="ActionExecution.WhileHeld"/> program holding nothing - stays
    /// up until its release, exactly as it did before the execution strategies existed.
    /// </summary>
    public bool IsFinished => _position >= _steps.Count && _timers.Count == 0;

    /// <summary>Starts (or restarts) the program from its first step with all state cleared.</summary>
    public void Start()
    {
        _running = true;
        _position = 0;
        _waitRemaining = 0d;
        _timers.Clear();
        _heldKeys.Clear();
    }

    /// <summary>
    /// Advances the program by one tick, appending its commands. A NaN or negative step
    /// counts as no time passing, so it can never rewind or explode the timers. The step is
    /// a double so the engine can hand over the gesture machine's post-threshold overflow
    /// without a lossy float round trip.
    /// </summary>
    public void Tick(double deltaSeconds, List<InputCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (!_running)
        {
            return;
        }

        var dt = double.IsNaN(deltaSeconds) || deltaSeconds < 0d ? 0d : deltaSeconds;

        var registered = AdvanceSequence(dt, commands);

        // Timers registered by earlier ticks take the whole tick; timers this tick's
        // sequence just registered only take the slice left after the waits that preceded
        // them - they did not exist before that point.
        foreach (var timer in _timers)
        {
            Drain(timer, dt, commands);
        }

        foreach (var (timer, credit) in registered)
        {
            _timers.Add(timer);
            Drain(timer, credit, commands);
        }

        // A run-once program ends itself: nothing is left to emit, so it releases what it
        // held instead of waiting for a release that may never come.
        if (_execution == ActionExecution.Once && IsFinished)
        {
            Stop(commands);
        }
    }

    /// <summary>
    /// Stops the program and releases every key it held, emitting the matching KeyUps. The
    /// release tick of the driving gesture calls this, so the keys a hold step pressed
    /// come back up exactly when the button does.
    /// </summary>
    public void Stop(List<InputCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        if (!_running)
        {
            return;
        }

        _running = false;

        // Release in reverse press order, the way the dispatcher unwinds a combo.
        for (var i = _heldKeys.Count - 1; i >= 0; i--)
        {
            commands.Add(new KeyUpCommand(_heldKeys[i], KeyModifiers.None, Owner));
        }

        ClearState();
    }

    /// <summary>
    /// Clears all runtime state without emitting anything. The host pairs this with
    /// <see cref="Platform.CommandDispatcher.ReleaseAll"/>, which lifts the physical keys -
    /// the same contract <see cref="MappingEngine.Reset"/> already follows.
    /// </summary>
    public void Reset() => ClearState();

    private void ClearState()
    {
        _running = false;
        _position = 0;
        _waitRemaining = 0d;
        _timers.Clear();
        _heldKeys.Clear();
    }

    /// <summary>
    /// Runs sequence steps until the tick's time runs out. Instantaneous steps cost no
    /// time and always run; a wait consumes what is left and parks the sequence, carrying
    /// the remainder into the next tick. Reports the timers the sequence registered, each
    /// with the slice of the tick that was still left when it was registered.
    /// </summary>
    private List<(EveryTimer Timer, double Credit)> AdvanceSequence(double remaining, List<InputCommand> commands)
    {
        var registered = new List<(EveryTimer, double)>();

        while (_position < _steps.Count)
        {
            if (_steps[_position] is ComplexShortcutWaitStep wait)
            {
                if (_waitRemaining <= 0d)
                {
                    _waitRemaining = wait.Seconds;
                }

                if (remaining < _waitRemaining - ButtonGestureState.TimingTolerance)
                {
                    _waitRemaining = Math.Max(_waitRemaining - remaining, 0d);
                    return registered;
                }

                remaining = Math.Max(remaining - _waitRemaining, 0d);
                _waitRemaining = 0d;
                _position++;
                continue;
            }

            if (_steps[_position] is ComplexShortcutEveryStep every)
            {
                registered.Add((new EveryTimer(every.Interval, every.Key, Owner), remaining));
                _position++;
                continue;
            }

            ExecuteStep(_steps[_position], commands);
            _position++;
        }

        return registered;
    }

    private void ExecuteStep(ComplexShortcutStep step, List<InputCommand> commands)
    {
        switch (step)
        {
            case ComplexShortcutTapStep tap:
                commands.Add(new KeyDownCommand(tap.Key, KeyModifiers.None, Owner));
                commands.Add(new KeyUpCommand(tap.Key, KeyModifiers.None, Owner));
                break;

            case ComplexShortcutHoldStep hold:
                // Holding an already-held key is a no-op: the program owns one press of it.
                if (!_heldKeys.Contains(hold.Key))
                {
                    _heldKeys.Add(hold.Key);
                    commands.Add(new KeyDownCommand(hold.Key, KeyModifiers.None, Owner));
                }

                break;

            case ComplexShortcutReleaseStep release:
                // Releasing a key the program does not hold must not reach for a key the
                // user may be holding for real, so it emits nothing.
                if (_heldKeys.Remove(release.Key))
                {
                    commands.Add(new KeyUpCommand(release.Key, KeyModifiers.None, Owner));
                }

                break;

            case ComplexShortcutClickStep click:
                // A mouse button is not refcounted per owner here: the program taps it and
                // is done, so the pair goes out together exactly as a MouseButtonTarget does.
                commands.Add(new MouseDownCommand(click.Button, Owner));
                commands.Add(new MouseUpCommand(click.Button, Owner));
                break;

            case ComplexShortcutTextStep text:
                commands.Add(new TextCommand(text.Text));
                break;
        }
    }

    /// <summary>
    /// Fires every tap of one timer the credited time covers, draining the timer's own
    /// interval so one large tick emits every tap it covers instead of one - up to the
    /// per-tick catch-up budget, whose remainder stays on the timer for the next tick rather
    /// than firing as one burst. See <see cref="ButtonGestureState.MaxCatchUpUnits"/>.
    /// </summary>
    private static void Drain(EveryTimer timer, double deltaSeconds, List<InputCommand> commands)
    {
        timer.Elapsed += deltaSeconds;

        var taps = 0;
        while (taps < ButtonGestureState.MaxCatchUpUnits
               && timer.Elapsed >= timer.Interval - ButtonGestureState.TimingTolerance)
        {
            timer.Elapsed = Math.Max(timer.Elapsed - timer.Interval, 0d);
            taps++;
            commands.Add(new KeyDownCommand(timer.Key, KeyModifiers.None, timer.Owner));
            commands.Add(new KeyUpCommand(timer.Key, KeyModifiers.None, timer.Owner));
        }
    }

    private sealed class EveryTimer(double interval, GamepadKey key, OutputOwner owner)
    {
        public double Interval { get; } = interval;

        public GamepadKey Key { get; } = key;

        public OutputOwner Owner { get; } = owner;

        public double Elapsed { get; set; }
    }
}
