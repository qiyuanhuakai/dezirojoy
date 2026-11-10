using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// The complex-shortcut runtime as the engine drives it: a long press starts the program,
/// every held tick advances it by deltaSeconds, and the release tick stops it and releases
/// the keys it held. All timing comes from the tick's deltaSeconds; there is no clock.
/// </summary>
public class ComplexShortcutTests
{
    private const string UserScript = "hold Alt\nevery 1s tap Tab";

    private const float Threshold = 0.5f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "complex-profile",
        Mappings = mappings,
    };

    private static MappingEntry LongPressComplex(GamepadButton button, string script, float threshold = Threshold)
        => new()
        {
            Source = new ButtonBinding(button) { Gesture = new ButtonGesture { LongPressThreshold = threshold } },
            Target = new ComplexShortcutTarget { Script = script },
        };

    private static GamepadState Held(GamepadButton button)
        => GamepadState.Create(button.ToMask(), 0, 0, 0, 0);

    private static GamepadState Released => GamepadState.Neutral;

    [Fact]
    public void User_Example_Holds_Alt_And_Taps_Tab_Every_Second_While_LB_Is_Held()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(GamepadButton.LeftBumper, UserScript)));

        // A quarter second in, the long press has not resolved yet: nothing fires.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // The threshold tick starts the program: Alt goes down and the 1s Tab timer arms.
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(activation));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // One second after activation: exactly one Tab tap, down then straight up.
        var firstTap = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.Tab, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Tab, KeyModifiers.None),
            ],
            firstTap);

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // Releasing stops the program on that tick: Alt comes back up and nothing follows.
        var release = engine.Tick(Released, 0.25f);
        Assert.Equal(new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(release));
        Assert.Empty(engine.Tick(Released, 0.25f));
        Assert.Empty(engine.Tick(Released, 5f));
    }

    [Fact]
    public void A_Large_Tick_Fires_Every_Periodic_Tap_It_Covers()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(GamepadButton.LeftBumper, UserScript)));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(activation));

        // 2.75s of held time covers the taps at 1s and 2s; the 0.75s remainder carries on.
        var catchUp = engine.Tick(Held(GamepadButton.LeftBumper), 2.5f);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.Tab, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Tab, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.Tab, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Tab, KeyModifiers.None),
            ],
            catchUp);

        var release = engine.Tick(Released, 0.25f);
        Assert.Equal(new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(release));
    }

    [Fact]
    public void A_Tap_Due_Past_The_Release_Boundary_Never_Fires()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(GamepadButton.LeftBumper, UserScript)));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(activation));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // The release tick would push the timer to 1.25s, but the button is already up:
        // the program stops before it is advanced, so only the held key comes back.
        var release = engine.Tick(Released, 0.5f);
        Assert.Equal(new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(release));
        Assert.Empty(engine.Tick(Released, 0.25f));
    }

    [Fact]
    public void Wait_Completes_Exactly_On_The_Tick_Its_Time_Runs_Out()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(
            GamepadButton.LeftBumper,
            "wait 1s\ntap B")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f)); // activation; wait owes 0.75s
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f)); // owes 0.5s
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f)); // owes 0.25s

        // 1.0s after activation: B taps on the tick the wait ends, not a tick later.
        var tap = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.B, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.B, KeyModifiers.None),
            ],
            tap);

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 1f));
    }

    [Fact]
    public void A_Large_Tick_Carries_The_Wait_Remainder_Into_The_Next_Step()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(
            GamepadButton.LeftBumper,
            "wait 1s\ntap B")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f)); // activation; wait owes 0.75s

        // 1.0s clears the 0.75s the wait still owed and B taps with 0.25s to spare.
        var tap = engine.Tick(Held(GamepadButton.LeftBumper), 1f);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.B, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.B, KeyModifiers.None),
            ],
            tap);

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 1f));
    }

    [Fact]
    public void Release_Step_Lets_Go_Of_A_Key_The_Program_Holds()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(
            GamepadButton.LeftBumper,
            "hold Alt\nwait 0.5s\nrelease Alt\ntap Enter")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(activation));

        // The wait ends: Alt goes back up and Enter taps in the same tick.
        var released = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None),
                new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None),
            ],
            released);

        // The release tick of the long press has nothing left to release.
        Assert.Empty(engine.Tick(Released, 0.25f));
    }

    [Fact]
    public void Every_Registers_Without_Blocking_The_Steps_After_It()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(
            GamepadButton.LeftBumper,
            "every 1s tap Tab\ntap Enter")));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // Enter fires on the activation tick; the Tab timer arms beside it, not after it.
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None),
            ],
            activation);

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        var tabTap = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(
            [
                new KeyDownCommand(GamepadKey.Tab, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.Tab, KeyModifiers.None),
            ],
            tabTap);
    }

    [Fact]
    public void Releasing_Before_The_Threshold_Starts_Nothing()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(GamepadButton.LeftBumper, UserScript)));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));

        // The click must not fire the program: a complex shortcut is long-press only.
        Assert.Empty(engine.Tick(Released, 0.25f));
        Assert.Empty(engine.Tick(Released, 5f));
    }

    [Fact]
    public void Reset_Stops_A_Running_Program_So_No_Period_Survives()
    {
        var engine = new MappingEngine(Profile(LongPressComplex(GamepadButton.LeftBumper, UserScript)));

        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        var activation = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(activation));

        // The host pairs Reset with CommandDispatcher.ReleaseAll, which lifts Alt; the
        // engine's own contract is that no residual period keeps tapping afterwards.
        engine.Reset();

        Assert.Empty(engine.Tick(Released, 5f));

        // A fresh long press runs the program again from scratch.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        var restart = engine.Tick(Held(GamepadButton.LeftBumper), 0.25f);
        Assert.Equal(new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(restart));
        Assert.Equal(new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None), Assert.Single(engine.Tick(Released, 0.25f)));
    }

    [Fact]
    public void A_Complex_Shortcut_Needs_A_Button_Source()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Left),
                Target = new ComplexShortcutTarget { Script = UserScript },
            })));

        Assert.Contains("long-press program", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Complex_Shortcut_Needs_A_Long_Press_Gesture()
    {
        var plain = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper),
                Target = new ComplexShortcutTarget { Script = UserScript },
            })));
        Assert.Contains("without a long press", plain.Message, StringComparison.OrdinalIgnoreCase);

        var emptyGesture = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper) { Gesture = new ButtonGesture() },
                Target = new ComplexShortcutTarget { Script = UserScript },
            })));
        Assert.Contains("without a long press", emptyGesture.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Complex_Shortcut_Rejects_Double_Click_Repeat_And_Extra_Actions()
    {
        // Every one of these would fire something else on the same press, which is the
        // double-trigger v1 avoids by binding complex shortcuts to a bare long press.
        var doubleClick = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            LongPressComplex(GamepadButton.LeftBumper, UserScript) with
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper)
                {
                    Gesture = new ButtonGesture { LongPressThreshold = Threshold, DoubleClickWindow = 0.25f },
                },
            })));
        Assert.Contains("双击", doubleClick.Message, StringComparison.Ordinal);

        var clickRepeat = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            LongPressComplex(GamepadButton.LeftBumper, UserScript) with
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper)
                {
                    Gesture = new ButtonGesture { LongPressThreshold = Threshold, ClickRepeatInterval = 0.1f },
                },
            })));
        Assert.Contains("auto-repeat", clickRepeat.Message, StringComparison.OrdinalIgnoreCase);

        var longPressAction = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            LongPressComplex(GamepadButton.LeftBumper, UserScript) with
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper)
                {
                    Gesture = new ButtonGesture
                    {
                        LongPressThreshold = Threshold,
                        LongPressAction = new KeyPressTarget(GamepadKey.Space),
                    },
                },
            })));
        Assert.Contains("gesture action", longPressAction.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Malformed_Script_Is_Rejected_When_The_Engine_Is_Built()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MappingEngine(Profile(
            LongPressComplex(GamepadButton.LeftBumper, "hold Alt\nfrobnicate"))));

        var parse = Assert.IsType<ComplexShortcutParseException>(exception.InnerException);
        Assert.Equal(2, parse.Line);
        Assert.Contains("未知指令", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ticks_Before_Start_And_After_Stop_Do_Nothing()
    {
        var runtime = ComplexShortcutRuntime.Compile("every 0.1s tap A");
        var commands = new List<InputCommand>();

        runtime.Tick(1f, commands);
        Assert.Empty(commands);

        runtime.Start();

        // One second at a 0.1s interval is ten taps, not one.
        runtime.Tick(1f, commands);
        Assert.Equal(20, commands.Count);

        runtime.Stop(commands);
        var stopped = commands.Count;
        runtime.Tick(1f, commands);
        Assert.Equal(stopped, commands.Count);
    }

    [Fact]
    public void Holding_The_Same_Key_Twice_Emits_One_Press()
    {
        var runtime = ComplexShortcutRuntime.Compile("hold A\nhold A");
        var commands = new List<InputCommand>();

        runtime.Start();
        runtime.Tick(0.1f, commands);

        Assert.Equal(new KeyDownCommand(GamepadKey.A, KeyModifiers.None), Assert.Single(commands));
    }

    [Fact]
    public void Releasing_A_Key_The_Program_Does_Not_Hold_Emits_Nothing()
    {
        var runtime = ComplexShortcutRuntime.Compile("release B");
        var commands = new List<InputCommand>();

        runtime.Start();
        runtime.Tick(0.1f, commands);

        Assert.Empty(commands);
    }

    [Fact]
    public void Stop_Releases_Held_Keys_In_Reverse_Press_Order()
    {
        var runtime = ComplexShortcutRuntime.Compile("hold A\nhold B\nhold C");
        var commands = new List<InputCommand>();

        runtime.Start();
        runtime.Tick(0.1f, commands);
        commands.Clear();

        runtime.Stop(commands);

        Assert.Equal(
            [
                new KeyUpCommand(GamepadKey.C, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.B, KeyModifiers.None),
                new KeyUpCommand(GamepadKey.A, KeyModifiers.None),
            ],
            commands);
    }

    [Fact]
    public void A_NaN_Or_Negative_Step_Moves_Nothing()
    {
        var runtime = ComplexShortcutRuntime.Compile("every 1s tap A");
        var commands = new List<InputCommand>();

        runtime.Start();
        runtime.Tick(float.NaN, commands);
        runtime.Tick(-1f, commands);

        Assert.Empty(commands);
    }
}
