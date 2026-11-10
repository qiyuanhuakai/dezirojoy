using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>
/// One running complex shortcut program. Deterministic and clock-free: every transition
/// is driven by the <paramref name="deltaSeconds"/> the engine threads through
/// <see cref="Tick"/>, exactly like the gesture machine. A program starts when a long
/// press fires, advances while the press holds, and stops on the release tick - releasing
/// every key it held - so a stopped program can never leave a key stuck down or a periodic
/// tap still running.
/// </summary>
/// <remarks>
/// <para>
/// Steps run in order. <see cref="ComplexShortcutTapStep"/> emits a down/up pair,
/// <see cref="ComplexShortcutHoldStep"/> presses a key the program then owns until it stops
/// or a <see cref="ComplexShortcutReleaseStep"/> releases it, and
/// <see cref="ComplexShortcutWaitStep"/> pauses the sequence - time left over in a tick
/// carries into the next step, so one large tick runs every step it covers.
/// </para>
/// <para>
/// A <see cref="ComplexShortcutEveryStep"/> registers a periodic tap that fires every
/// interval for as long as the program runs; registering costs no time, so steps after it
/// still run in order. A tick covering several intervals fires every tap it covers and the
/// remainder carries on - the same "never lose a unit" rule the gesture machine's
/// auto-repeat applies, with the same one-microsecond tolerance so a run of 0.1s ticks
/// cannot drift a tap out of existence.
/// </para>
/// <para>
/// A timer only exists from the moment its every step runs: within the tick that registers
/// it, the timer is credited the time left after the waits that preceded it, while timers
/// from earlier ticks take the whole tick. Within one tick the sequence's commands come
/// first, then the periodic taps in registration order. A tap due after the release
/// boundary never fires, because the release tick stops the program before it is advanced.
/// </para>
/// </remarks>
public sealed class ComplexShortcutRuntime
{
    private readonly IReadOnlyList<ComplexShortcutStep> _steps;
    private readonly List<EveryTimer> _timers = [];
    private readonly List<GamepadKey> _heldKeys = [];
    private int _position;
    private double _waitRemaining;
    private bool _running;

    private ComplexShortcutRuntime(IReadOnlyList<ComplexShortcutStep> steps)
    {
        _steps = steps;
    }

    /// <summary>
    /// Compiles a script into a stopped runtime.
    /// </summary>
    /// <exception cref="ComplexShortcutParseException">The script is malformed; the message is Chinese and carries line and column.</exception>
    public static ComplexShortcutRuntime Compile(string script)
        => new(ComplexShortcutDsl.Parse(script));

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning => _running;

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
    }

    /// <summary>
    /// Stops the program and releases every key it held, emitting the matching KeyUps. The
    /// release tick of the driving long press calls this, so the keys a hold step pressed
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
            commands.Add(new KeyUpCommand(_heldKeys[i], KeyModifiers.None));
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
                registered.Add((new EveryTimer(every.Interval, every.Key), remaining));
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
                commands.Add(new KeyDownCommand(tap.Key, KeyModifiers.None));
                commands.Add(new KeyUpCommand(tap.Key, KeyModifiers.None));
                break;

            case ComplexShortcutHoldStep hold:
                // Holding an already-held key is a no-op: the program owns one press of it.
                if (!_heldKeys.Contains(hold.Key))
                {
                    _heldKeys.Add(hold.Key);
                    commands.Add(new KeyDownCommand(hold.Key, KeyModifiers.None));
                }

                break;

            case ComplexShortcutReleaseStep release:
                // Releasing a key the program does not hold must not reach for a key the
                // user may be holding for real, so it emits nothing.
                if (_heldKeys.Remove(release.Key))
                {
                    commands.Add(new KeyUpCommand(release.Key, KeyModifiers.None));
                }

                break;
        }
    }

    /// <summary>
    /// Fires every tap of one timer the credited time covers, draining the timer's own
    /// interval so one large tick emits every tap it covers instead of one.
    /// </summary>
    private static void Drain(EveryTimer timer, double deltaSeconds, List<InputCommand> commands)
    {
        timer.Elapsed += deltaSeconds;

        while (timer.Elapsed >= timer.Interval - ButtonGestureState.TimingTolerance)
        {
            timer.Elapsed = Math.Max(timer.Elapsed - timer.Interval, 0d);
            commands.Add(new KeyDownCommand(timer.Key, KeyModifiers.None));
            commands.Add(new KeyUpCommand(timer.Key, KeyModifiers.None));
        }
    }

    private sealed class EveryTimer(double interval, GamepadKey key)
    {
        public double Interval { get; } = interval;

        public GamepadKey Key { get; } = key;

        public double Elapsed { get; set; }
    }
}
