using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// Sampling-gap robustness. The host loop hands the engine the real elapsed time of each
/// tick, which is usually a couple of milliseconds and occasionally much more: a stalled
/// loop thread, a modal dialog, a machine coming back from sleep. Two different things have
/// to happen for those two cases.
/// <para>
/// Ordinary jitter is compensated whole: a tick that covers several repeat intervals fires
/// every repeat it covers, never losing a unit, up to the per-tick catch-up budget whose
/// remainder stays on the clock. A sampling gap - a tick far longer than any jitter - is
/// recovered from instead: every accumulated cycle is cleared, the freshly polled snapshot
/// is re-adopted as the edge baseline, and the tick integrates one bounded step, so a ten
/// second gap can neither teleport the pointer ten seconds worth of stick deflection nor
/// replay a hundred taps the user never asked for.
/// </para>
/// </summary>
public class SamplingGapTests
{
    /// <summary>The largest timestep displacement is integrated for (MappingEngine.MaxIntegrationSeconds).</summary>
    private const float IntegrationBound = 0.25f;

    /// <summary>A tick longer than this is a sampling gap (MappingEngine.SamplingGapSeconds).</summary>
    private const float GapThreshold = 3f;

    /// <summary>The per-tick periodic catch-up budget (ButtonGestureState.MaxCatchUpUnits).</summary>
    private const int CatchUpBudget = 16;

    private const float Tick = 1f / 64f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "sampling-profile",
        Mappings = mappings,
    };

    private static MappingEntry Button(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target };

    private static readonly MouseStickSettings RawMouse = new()
    {
        DeadZone = 0f,
        MaxSpeed = 640f,
        Curve = ResponseCurve.Linear,
        CurveStrength = 0f,
        ResponseTime = 0f,
        InvertY = false,
    };

    private static GamepadState Stick(GamepadStick stick, float x, float y)
        => stick == GamepadStick.Left
            ? GamepadState.Create(GamepadButtonMask.None, x, y, 0, 0)
            : GamepadState.Create(GamepadButtonMask.None, 0, 0, x, y);

    private static GamepadState Held(GamepadButton button) => GamepadState.Create(button.ToMask(), 0, 0, 0, 0);

    private static GamepadState Released => GamepadState.Neutral;

    /// <summary>A long press that re-fires the mapping's own target, for the baseline tests.</summary>
    private static MappingEntry LongPressEnter(GamepadButton button, float threshold = 0.5f)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture { LongPressThreshold = threshold },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

    /// <summary>A long press whose own action runs a program.</summary>
    private static MappingEntry LongPressProgram(GamepadButton button, string script, float threshold = 0.5f)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = threshold,
                    LongPressAction = new ComplexShortcutTarget { Script = script },
                },
            },
            Target = new NoneTarget(),
        };

    /// <summary>A click with auto-repeat, for the gesture catch-up test.</summary>
    private static MappingEntry RepeatClickEnter(GamepadButton button, float interval = 0.05f)
        => new()
        {
            Source = new ButtonBinding(button)
            {
                Gesture = new ButtonGesture { ClickRepeatInterval = interval },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        };

    [Fact]
    public void A_Large_Dt_Moves_The_Pointer_One_Bounded_Step()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Right),
            Target = new MouseMoveTarget(RawMouse),
        }));

        // Ten seconds of real time with the stick pinned right: the pointer moves one
        // bounded step, not ten seconds of full-speed deflection.
        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(engine.Tick(Stick(GamepadStick.Right, 1f, 0f), 10f)));

        Assert.Equal((int)(RawMouse.MaxSpeed * IntegrationBound), move.Dx);
        Assert.Equal(0, move.Dy);
    }

    [Fact]
    public void A_Large_Dt_Scrolls_One_Bounded_Step()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Left),
            Target = new WheelScrollTarget(new ScrollStickSettings
            {
                DeadZone = 0f,
                MaxTicksPerSecond = 64f,
                Curve = ResponseCurve.Linear,
                CurveStrength = 0f,
                ResponseTime = 0f,
            }),
        }));

        var wheel = Assert.IsType<WheelCommand>(Assert.Single(engine.Tick(Stick(GamepadStick.Left, 0f, 1f), 10f)));

        Assert.Equal(0, wheel.HorizontalNotches);
        Assert.Equal((int)(64f * IntegrationBound), wheel.VerticalNotches);
    }

    [Fact]
    public void A_Button_Driven_Mouse_Move_Is_Bounded_Too()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.A) { Direction = InputDirection.Right },
            Target = new MouseMoveTarget(RawMouse),
        }));

        // 2.9s of jitter: the press edge arms the continuous target and the tick integrates
        // one bounded step, so the pointer never takes the whole 2.9s at once.
        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(engine.Tick(Held(GamepadButton.A), GapThreshold - 0.1f)));

        Assert.Equal((int)(RawMouse.MaxSpeed * IntegrationBound), move.Dx);
    }

    [Fact]
    public void A_Sampling_Gap_Does_Not_Fabricate_A_Press_Edge_For_A_Button_Held_Through_It()
    {
        var engine = new MappingEngine(Profile(Button(GamepadButton.A, new KeyPressTarget(GamepadKey.Enter))));

        Assert.Equal(
            new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.A), Tick)));

        // The button was held before the gap and is still held after it: re-adopting the
        // baseline means no second press edge, so the key is not pressed twice.
        Assert.Empty(engine.Tick(Held(GamepadButton.A), 10f));

        // The release is still a real edge, so the key comes back up exactly once.
        Assert.Equal(
            new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, Tick)));
    }

    [Fact]
    public void A_Sampling_Gap_Clears_Accumulated_State_And_Re_Adopts_The_Baseline()
    {
        var engine = new MappingEngine(Profile(LongPressEnter(GamepadButton.B)));

        // A long press is in flight when the gap hits.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.3f));

        // The gap tick clears the cycle and adopts the still-held button as the baseline, so
        // nothing fires: no press edge is fabricated for input the user never re-pressed, and
        // the half-elapsed gesture is cancelled rather than resumed.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 10f));

        // A fresh press after the gap runs a whole new cycle from scratch.
        Assert.Empty(engine.Tick(Released, Tick));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.3f));
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None),
            ],
            engine.Tick(Held(GamepadButton.B), 0.25f));
    }

    [Fact]
    public void A_Sampling_Gap_Stops_A_Running_Program_Instead_Of_Replaying_It()
    {
        var engine = new MappingEngine(Profile(
            LongPressProgram(GamepadButton.LeftBumper, "hold Alt")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.3f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(activation));

        // Ten seconds of gap: the program is cleared, so the ~200 taps it would have replayed
        // never fire. The host lifts Alt beside this, exactly as it does for Reset.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 10f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), Tick));
    }

    [Fact]
    public void A_Large_Tick_Emits_A_Bounded_Catch_Up_Tap_Count()
    {
        var engine = new MappingEngine(Profile(
            LongPressProgram(GamepadButton.LeftBumper, "every 0.05s tap Tab")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // The activation tick starts the program and credits the timer with the time left
        // after the threshold, so its first tap lands there.
        Assert.Equal(2, engine.Tick(Held(GamepadButton.LeftBumper), 0.3f).Count);

        // 2.5s at a 0.05s interval is 50 taps; the per-tick budget bounds the burst, and the
        // remainder stays on the timer instead of firing as one flood.
        var burst = engine.Tick(Held(GamepadButton.LeftBumper), 2.5f);

        Assert.Equal(CatchUpBudget * 2, burst.Count);
        Assert.All(burst.OfType<KeyDownCommand>(), command => Assert.Equal(GamepadKey.Tab, command.Key));
    }

    [Fact]
    public void A_Bounded_Burst_Leaves_The_Rest_Of_The_Interval_On_The_Timer()
    {
        var engine = new MappingEngine(Profile(
            LongPressProgram(GamepadButton.LeftBumper, "every 0.05s tap Tab")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Equal(2, engine.Tick(Held(GamepadButton.LeftBumper), 0.3f).Count);

        // 0.85s at a 0.05s interval is 17 taps: the budget takes 16 and leaves one interval
        // on the timer instead of firing the rest as one flood.
        var burst = engine.Tick(Held(GamepadButton.LeftBumper), 0.85f);
        Assert.Equal(CatchUpBudget * 2, burst.Count);

        // The next ordinary tick keeps draining what the burst left behind: the taps are
        // spread, not dropped and not duplicated.
        var next = engine.Tick(Held(GamepadButton.LeftBumper), Tick);

        Assert.Equal(2, next.Count);
        Assert.IsType<KeyDownCommand>(next[0]);
        Assert.IsType<KeyUpCommand>(next[1]);
    }

    [Fact]
    public void A_Normal_Tick_Still_Drains_Every_Repeat_It_Covers()
    {
        var engine = new MappingEngine(Profile(
            LongPressProgram(GamepadButton.LeftBumper, "every 0.1s tap Tab")));

        // The activation tick starts the program; the 0.1s interval is not due yet.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.3f));

        var taps = 0;
        for (var i = 0; i < 10; i++)
        {
            taps += engine.Tick(Held(GamepadButton.LeftBumper), 0.1f).Count / 2;
        }

        Assert.Equal(10, taps);
    }

    [Fact]
    public void A_Gesture_Auto_Repeat_Catch_Up_Is_Bounded_The_Same_Way()
    {
        var engine = new MappingEngine(Profile(RepeatClickEnter(GamepadButton.B)));

        // The press tick fires the initial click and returns before the repeat clock
        // advances; the next held tick covers 50 intervals and emits one of its own plus the
        // budget the drain fills - seventeen, not fifty.
        Assert.Equal(2, engine.Tick(Held(GamepadButton.B), 2.5f).Count);

        var catchUp = engine.Tick(Held(GamepadButton.B), 2.5f);

        Assert.Equal((CatchUpBudget + 1) * 2, catchUp.Count);
    }

    [Fact]
    public void A_Tick_Just_Below_The_Gap_Threshold_Is_Still_Compensated_Whole()
    {
        var engine = new MappingEngine(Profile(
            LongPressProgram(GamepadButton.LeftBumper, "every 0.1s tap Tab")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.3f));

        // 2.9s is jitter, not a gap: every interval it covers still fires, up to the budget.
        var commands = engine.Tick(Held(GamepadButton.LeftBumper), GapThreshold - 0.1f);

        Assert.Equal(CatchUpBudget * 2, commands.Count);
    }

    [Fact]
    public void A_Slow_Stick_Still_Accumulates_Sub_Pixel_Residuals_Across_A_Bounded_Step()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Right),
            Target = new MouseMoveTarget(RawMouse with { MaxSpeed = 32f }),
        }));

        // 32 px/s over 0.25s is 8 px per bounded step; a slow stick keeps its remainder, so
        // bounding the timestep never drops a pixel that fit inside it.
        var move = Assert.IsType<MouseMoveCommand>(Assert.Single(engine.Tick(Stick(GamepadStick.Right, 1f, 0f), 10f)));

        Assert.Equal(8, move.Dx);
    }

    [Fact]
    public void A_Negative_Or_NaN_Step_Moves_Nothing()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new StickBinding(GamepadStick.Right),
            Target = new MouseMoveTarget(RawMouse),
        }));

        Assert.Empty(engine.Tick(Stick(GamepadStick.Right, 1f, 0f), -1f));
        Assert.Empty(engine.Tick(Stick(GamepadStick.Right, 1f, 0f), float.NaN));
    }
}
