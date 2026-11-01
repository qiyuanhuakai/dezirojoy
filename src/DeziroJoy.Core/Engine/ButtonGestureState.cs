using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Engine;

/// <summary>Where one button's gesture cycle currently is.</summary>
public enum GesturePhase
{
    /// <summary>No press in progress.</summary>
    Idle,

    /// <summary>Pressed once; waiting to learn whether a second press (双击) arrives.</summary>
    ClickPending,

    /// <summary>Held; waiting for the long-press threshold (长按).</summary>
    LongPressPending,

    /// <summary>The single action resolved as a hold: down now, up on release.</summary>
    Holding,

    /// <summary>The long-press fired; auto-repeat may still be running.</summary>
    LongPressActive,
}

/// <summary>What the engine should do to the target on this tick.</summary>
public enum GestureAction
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>Begin a hold-style activation: key/mouse button down, continuous output armed.</summary>
    BeginHold,

    /// <summary>End the hold-style activation: key/mouse button up, continuous output stopped.</summary>
    EndHold,

    /// <summary>Fire one instantaneous activation: down+up, one tick of movement, one notch.</summary>
    Tap,
}

/// <summary>
/// Which gesture an activation belongs to. The engine owns targets, so the machine only reports
/// which gesture resolved and lets <see cref="MappingEngine"/> pick what it fires: a gesture with
/// no action of its own re-fires the mapping's target, which is what keeps the action fields
/// purely additive.
/// </summary>
public enum GestureKind
{
    /// <summary>No gesture resolved.</summary>
    None,

    /// <summary>The plain click: the mapping's own target.</summary>
    Click,

    /// <summary>The double-click gesture.</summary>
    DoubleClick,

    /// <summary>The long-press gesture, including every auto-repeat activation.</summary>
    LongPress,
}

/// <summary>What the engine should do on this tick, and which gesture resolved it.</summary>
/// <param name="Action">What to do to the target.</param>
/// <param name="Kind">Which gesture resolved, so the right target can be picked.</param>
public readonly record struct GestureStep(GestureAction Action, GestureKind Kind)
{
    /// <summary>A tick that changes nothing.</summary>
    public static GestureStep None { get; } = new(GestureAction.None, GestureKind.None);

    /// <summary>One instantaneous activation of <paramref name="kind"/>.</summary>
    public static GestureStep Tap(GestureKind kind) => new(GestureAction.Tap, kind);
}

/// <summary>
/// Per-button gesture state machine. It owns no clock: every transition is driven by the
/// press/release edges and the <paramref name="deltaSeconds"/> the host threads through
/// <see cref="Update"/>, which is what keeps the whole mapping layer deterministic.
/// </summary>
/// <remarks>
/// <para>
/// States and transitions (a press edge starts a cycle; a release edge or a threshold ends it):
/// </para>
/// <list type="bullet">
///   <item><description><b>Idle</b> --press--&gt; ClickPending (双击 configured) or LongPressPending (长按 only).</description></item>
///   <item><description><b>ClickPending</b> --second press inside the window--&gt; Idle, firing the double-click.</description></item>
///   <item><description><b>ClickPending</b> --window expires, still held--&gt; Holding (单击 as a hold) or LongPressPending (长按 still ahead).</description></item>
///   <item><description><b>ClickPending</b> --window expires, released--&gt; Idle, firing the single as a tap.</description></item>
///   <item><description><b>LongPressPending</b> --release before the threshold--&gt; Idle, firing the single as a tap, or nothing at all when the long-press carries an action of its own.</description></item>
///   <item><description><b>LongPressPending</b> --threshold reached--&gt; LongPressActive, firing the long-press.</description></item>
///   <item><description><b>Holding</b> --release--&gt; Idle, ending the hold.</description></item>
///   <item><description><b>LongPressActive</b> --every repeat interval--&gt; fire the long-press again; --release--&gt; Idle.</description></item>
/// </list>
/// <para>
/// When more than one gesture is configured, whichever resolves first wins and the others are
/// cancelled: the long-press threshold is checked before the double-click window closes, so a
/// short threshold beats a long window, and a window that closes first hands a still-held
/// button over to the long-press instead of firing the single.
/// </para>
/// <para>
/// A long-press that carries its own action consumes the press: releasing before the threshold
/// fires nothing, because the button now has two functions and a press that reached neither is
/// discarded rather than guessed at. Without an action the long-press stays a pure timing
/// modifier on the single, so an early release still fires the single exactly as it always did.
/// </para>
/// <para>
/// With no gesture configured (or nothing enabled) the machine takes the zero-latency path:
/// the press edge activates the target immediately, exactly as an ungestured button does.
/// </para>
/// </remarks>
public sealed class ButtonGestureState
{
    /// <summary>
    /// Timing comparisons tolerate one microsecond. The thresholds are user-facing decimal
    /// seconds held as float, and 0.1f is not exactly 0.1, so an exact comparison against a
    /// double accumulator can push a threshold a whole tick late (0.3s reached by 0.1s ticks
    /// compares just under 0.3f) or swallow the last repeat of a long catch-up tick. One
    /// microsecond sits far below anything perceptible and far above representation noise.
    /// </summary>
    private const double TimingTolerance = 1e-6;

    private static bool Reached(double elapsed, float threshold) => elapsed >= threshold - TimingTolerance;

    /// <summary>Current phase of the cycle.</summary>
    public GesturePhase Phase { get; private set; } = GesturePhase.Idle;

    /// <summary>
    /// Seconds of held time accumulated since the press edge that started this cycle. The
    /// double-click window and the long-press threshold are both measured from that press, so
    /// a hand-over from ClickPending to LongPressPending keeps the same clock. Accumulated in
    /// double precision so a long auto-repeat run cannot drift a repeat out of existence.
    /// </summary>
    public double Elapsed { get; private set; }

    /// <summary>Seconds since the last long-press activation (auto-repeat clock).</summary>
    public double RepeatElapsed { get; private set; }

    /// <summary>
    /// True while a hold-style activation is in effect for this mapping: a key or mouse button
    /// is down, or a button-driven continuous target (mouse move / wheel) is moving. The
    /// stick-as-digital path reuses the same flag for its pressed/released edge detection.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>Clears all gesture state (profile switch, engine pause).</summary>
    public void Reset()
    {
        Phase = GesturePhase.Idle;
        Elapsed = 0d;
        RepeatElapsed = 0d;
        IsActive = false;
    }

    /// <summary>
    /// Advances the machine by one tick and reports what the engine should do, and which gesture
    /// resolved it.
    /// </summary>
    /// <param name="gesture">Configured gestures, or null for the zero-latency path.</param>
    /// <param name="isPressed">Whether the button is down in this tick's snapshot.</param>
    /// <param name="wasPressed">Whether it was down in the previous tick's snapshot.</param>
    /// <param name="deltaSeconds">This tick's timestep; NaN or negative counts as no time passing.</param>
    public GestureStep Update(ButtonGesture? gesture, bool isPressed, bool wasPressed, float deltaSeconds)
    {
        var pressEdge = isPressed && !wasPressed;
        var releaseEdge = !isPressed && wasPressed;

        if (gesture is null || !gesture.HasAnyGesture)
        {
            if (pressEdge)
            {
                IsActive = true;
                return new GestureStep(GestureAction.BeginHold, GestureKind.Click);
            }

            if (releaseEdge)
            {
                IsActive = false;
                return new GestureStep(GestureAction.EndHold, GestureKind.Click);
            }

            return GestureStep.None;
        }

        // A NaN or negative step must never rewind or explode the gesture timers.
        var dt = float.IsNaN(deltaSeconds) || deltaSeconds < 0f ? 0f : deltaSeconds;

        var cycleStarted = false;
        if (Phase == GesturePhase.Idle && pressEdge)
        {
            Phase = gesture.HasDoubleClick ? GesturePhase.ClickPending : GesturePhase.LongPressPending;
            Elapsed = 0d;
            RepeatElapsed = 0d;
            cycleStarted = true;
        }

        switch (Phase)
        {
            case GesturePhase.ClickPending:
            case GesturePhase.LongPressPending:
                Elapsed += dt;
                break;
            case GesturePhase.LongPressActive:
                RepeatElapsed += dt;
                break;
        }

        switch (Phase)
        {
            case GesturePhase.Idle:
                break;

            case GesturePhase.ClickPending:
                if (pressEdge && !cycleStarted)
                {
                    Phase = GesturePhase.Idle;
                    return GestureStep.Tap(GestureKind.DoubleClick);
                }

                if (gesture.HasLongPress && isPressed && Reached(Elapsed, gesture.LongPressThreshold))
                {
                    return ActivateLongPress(gesture);
                }

                if (Reached(Elapsed, gesture.DoubleClickWindow))
                {
                    if (!isPressed)
                    {
                        Phase = GesturePhase.Idle;
                        return GestureStep.Tap(GestureKind.Click);
                    }

                    if (gesture.HasLongPress)
                    {
                        Phase = GesturePhase.LongPressPending;
                    }
                    else
                    {
                        Phase = GesturePhase.Holding;
                        IsActive = true;
                        return new GestureStep(GestureAction.BeginHold, GestureKind.Click);
                    }
                }

                break;

            case GesturePhase.LongPressPending:
                if (releaseEdge)
                {
                    Phase = GesturePhase.Idle;

                    // A long-press with an action of its own owns the press: a release that
                    // reached neither gesture is discarded rather than guessed at.
                    return gesture.HasLongPressAction
                        ? GestureStep.None
                        : GestureStep.Tap(GestureKind.Click);
                }

                if (Reached(Elapsed, gesture.LongPressThreshold))
                {
                    return ActivateLongPress(gesture);
                }

                break;

            case GesturePhase.Holding:
                if (releaseEdge)
                {
                    Phase = GesturePhase.Idle;
                    IsActive = false;
                    return new GestureStep(GestureAction.EndHold, GestureKind.Click);
                }

                break;

            case GesturePhase.LongPressActive:
                if (TryDequeueRepeat(gesture))
                {
                    return GestureStep.Tap(GestureKind.LongPress);
                }

                if (releaseEdge)
                {
                    Phase = GesturePhase.Idle;
                    IsActive = false;
                }

                break;
        }

        return GestureStep.None;
    }

    /// <summary>
    /// Consumes one due auto-repeat interval, if any. The engine drains this after
    /// <see cref="Update"/> so a tick covering several intervals emits every repeat it covers
    /// instead of silently dropping them - the same "never lose a unit" rule the stick
    /// processors apply to sub-pixel residuals. The host's timestep bounds this in practice.
    /// </summary>
    public bool TryDequeueRepeat(ButtonGesture gesture)
    {
        if (gesture.LongPressRepeatInterval <= 0f || !Reached(RepeatElapsed, gesture.LongPressRepeatInterval))
        {
            return false;
        }

        RepeatElapsed = Math.Max(RepeatElapsed - gesture.LongPressRepeatInterval, 0d);
        return true;
    }

    private GestureStep ActivateLongPress(ButtonGesture gesture)
    {
        Phase = GesturePhase.LongPressActive;

        // The long-press taps; it never becomes a hold, so the single stays cancelled.
        IsActive = false;

        // Time spent past the threshold inside this tick carries into the repeat clock.
        RepeatElapsed = Math.Max(Elapsed - gesture.LongPressThreshold, 0d);
        return GestureStep.Tap(GestureKind.LongPress);
    }
}
