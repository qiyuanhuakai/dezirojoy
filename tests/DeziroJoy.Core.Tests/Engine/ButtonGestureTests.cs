using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// The gesture layer: per-button 单击 / 双击 / 长按 with adaptive latency. The latency rule is
/// the core contract - a button with no gesture configured fires on the press edge with zero
/// wait, while 双击 / 长按 introduce a wait because the single action cannot fire until the
/// gesture resolves. All timing comes from the tick's deltaSeconds; there is no clock.
/// </summary>
public class ButtonGestureTests
{
    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "gesture-profile",
        Mappings = mappings,
    };

    private static MappingEntry Gestured(GamepadButton button, MappingTarget target, ButtonGesture gesture)
        => new() { Source = new ButtonBinding(button) { Gesture = gesture }, Target = target };

    private static MappingEntry Plain(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target };

    private static readonly ButtonGesture DoubleClickOnly = new() { DoubleClickWindow = 0.25f };

    private static readonly ButtonGesture LongPressOnly = new() { LongPressThreshold = 0.4f };

    private static readonly ButtonGesture LongPressRepeating = new()
    {
        LongPressThreshold = 0.2f,
        LongPressRepeatInterval = 0.1f,
    };

    private static readonly ButtonGesture BothGestures = new()
    {
        DoubleClickWindow = 0.25f,
        LongPressThreshold = 0.4f,
    };

    private static readonly ButtonGesture DoubleClickWithAction = new()
    {
        DoubleClickWindow = 0.25f,
        DoubleClickAction = new KeyPressTarget(GamepadKey.Space),
    };

    private static readonly ButtonGesture LongPressWithAction = new()
    {
        LongPressThreshold = 0.4f,
        LongPressAction = new KeyPressTarget(GamepadKey.Space),
    };

    private static readonly ButtonGesture LongPressActionRepeating = new()
    {
        LongPressThreshold = 0.2f,
        LongPressRepeatInterval = 0.1f,
        LongPressAction = new KeyPressTarget(GamepadKey.Space),
    };

    private static readonly ButtonGesture BothActions = new()
    {
        DoubleClickWindow = 0.25f,
        LongPressThreshold = 0.4f,
        DoubleClickAction = new KeyPressTarget(GamepadKey.Space),
        LongPressAction = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.W),
    };

    private static GamepadState Held(GamepadButton button)
        => GamepadState.Create(button.ToMask(), 0, 0, 0, 0);

    private static GamepadState Released => GamepadState.Neutral;

    private static int Taps(IReadOnlyList<InputCommand> commands)
        => commands.Count(command => command is KeyDownCommand);

    [Fact]
    public void No_Gesture_Configured_Fires_On_The_Press_Edge_With_Zero_Latency()
    {
        var engine = new MappingEngine(Profile(Plain(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter))));

        var press = engine.Tick(Held(GamepadButton.B), 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(press));
    }

    [Fact]
    public void Gesture_With_Nothing_Enabled_Adds_No_Latency()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture())));

        var press = engine.Tick(Held(GamepadButton.B), 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(press));
    }

    [Fact]
    public void Double_Click_Configured_Defers_The_Single_Until_The_Window_Expires()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            DoubleClickOnly)));

        // The press edge must not fire: it is still unknown whether a second press is coming.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        // 0.3s of held time >= the 0.25s window, and the button is still down: the single
        // resolves as a hold, exactly like an ungestured button would have.
        var resolved = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(resolved));

        var release = engine.Tick(Released, 0.1f);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(release));
    }

    [Fact]
    public void Double_Click_Fires_A_Tap_When_A_Second_Press_Lands_Inside_The_Window()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.RightBumper,
            new MouseButtonTarget(MouseButton.Left),
            DoubleClickOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.RightBumper), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));

        var second = engine.Tick(Held(GamepadButton.RightBumper), 0.05f);
        Assert.Equal(2, second.Count);
        Assert.Equal(new MouseDownCommand(MouseButton.Left), second[0]);
        Assert.Equal(new MouseUpCommand(MouseButton.Left), second[1]);

        // The double-click won, so the single is cancelled: waiting out the window emits nothing.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.2f));
    }

    [Fact]
    public void Double_Click_Window_Expiring_After_Release_Emits_A_Single_Tap()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            DoubleClickOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.1f));

        var tap = engine.Tick(Released, 0.1f);
        Assert.Equal(2, tap.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), tap[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), tap[1]);
    }

    [Fact]
    public void A_Second_Press_After_The_Window_Is_A_Fresh_Single_Click()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            DoubleClickOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(2, engine.Tick(Released, 0.2f).Count);

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(2, engine.Tick(Released, 0.2f).Count);
    }

    [Fact]
    public void Long_Press_Fires_Once_When_Held_Past_The_Threshold()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        var fired = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(2, fired.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), fired[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), fired[1]);

        // Fired once, no repeat configured: holding longer changes nothing.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));
    }

    [Fact]
    public void Long_Press_Released_Before_The_Threshold_Emits_A_Single_Tap()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        var tap = engine.Tick(Released, 0.1f);
        Assert.Equal(2, tap.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), tap[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), tap[1]);
    }

    [Fact]
    public void Long_Press_Auto_Repeat_Fires_Every_Interval_While_Held()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressRepeating)));

        var commands = new List<InputCommand>();
        for (var i = 0; i < 20; i++)
        {
            commands.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Held for 1.0s: fires at 0.2s, then every 0.1s -> 0.2, 0.3, ... 1.0 = 9 activations.
        Assert.Equal(9, Taps(commands));
    }

    [Fact]
    public void Long_Press_Auto_Repeat_Catches_Up_Inside_One_Long_Tick()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressRepeating)));

        var commands = engine.Tick(Held(GamepadButton.B), 1f);

        // One 1s tick covers the 0.2s threshold plus eight 0.1s repeat intervals.
        Assert.Equal(9, Taps(commands));
    }

    [Fact]
    public void Long_Press_Auto_Repeat_Stops_On_Release()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressRepeating)));

        var held = new List<InputCommand>();
        for (var i = 0; i < 10; i++)
        {
            held.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        Assert.Equal(4, Taps(held));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Long_Press_Wins_When_Its_Threshold_Comes_First()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { DoubleClickWindow = 0.25f, LongPressThreshold = 0.1f })));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));

        var fired = engine.Tick(Held(GamepadButton.B), 0.05f);
        Assert.Equal(2, fired.Count);

        // The long-press resolved first, so the double-click window closing changes nothing.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));
    }

    [Fact]
    public void Double_Click_Window_Hands_The_Hold_Over_To_Long_Press()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            BothGestures)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));

        // 0.25s window closes while still held: no single fires, the long-press takes over.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));

        // 0.4s of held time: the long-press fires and the single stays cancelled.
        var fired = engine.Tick(Held(GamepadButton.B), 0.05f);
        Assert.Equal(2, fired.Count);
        Assert.Empty(engine.Tick(Released, 0.05f));
    }

    [Fact]
    public void Double_Click_Taps_A_Combo_Target()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.X,
            new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
            new ButtonGesture { DoubleClickWindow = 0.2f })));

        Assert.Empty(engine.Tick(Held(GamepadButton.X), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));

        var tap = engine.Tick(Held(GamepadButton.X), 0.05f);
        Assert.Equal(2, tap.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.C, KeyModifiers.Ctrl), tap[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.C, KeyModifiers.Ctrl), tap[1]);
    }

    [Fact]
    public void Reset_Clears_Gesture_State_So_A_Held_Button_Starts_A_Fresh_Cycle()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            DoubleClickOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        engine.Reset();

        // The still-held button counts as a fresh press edge, so a new cycle starts and the
        // single is deferred again rather than firing immediately.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        var resolved = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(resolved));
    }

    [Fact]
    public void Gestures_Apply_To_Extra_Buttons_Too()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Extra, 305) { Gesture = DoubleClickOnly },
            Target = new KeyPressTarget(GamepadKey.F13),
        }));

        Assert.Empty(engine.Tick(new ExtraButtonState(305), 0.05f));
        Assert.Empty(engine.Tick(GamepadState.Neutral, 0.05f));

        var tap = engine.Tick(new ExtraButtonState(305), 0.05f);
        Assert.Equal(2, tap.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.F13, KeyModifiers.None), tap[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.F13, KeyModifiers.None), tap[1]);
    }

    [Fact]
    public void Two_Mappings_On_One_Button_Resolve_Their_Gestures_Independently()
    {
        var engine = new MappingEngine(Profile(
            Gestured(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter), DoubleClickOnly),
            Gestured(GamepadButton.B, new KeyPressTarget(GamepadKey.Space), LongPressOnly)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        // The double-click window (0.25s) closes first: that mapping's single resolves as a hold.
        var single = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(single));

        // The long-press threshold (0.4s) resolves later, on its own clock.
        var longPress = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(2, longPress.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Space, KeyModifiers.None), longPress[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Space, KeyModifiers.None), longPress[1]);

        var release = engine.Tick(Released, 0.1f);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(release));
    }

    [Fact]
    public void Double_Click_Action_Fires_On_The_Second_Press()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            DoubleClickWithAction)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));

        var second = engine.Tick(Held(GamepadButton.B), 0.05f);
        Assert.Equal(2, second.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Space, KeyModifiers.None), second[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Space, KeyModifiers.None), second[1]);

        // The double-click action won, so the single stays cancelled.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.2f));
    }

    [Fact]
    public void Double_Click_Action_Null_Adds_No_Wait_And_Fires_The_Single_Immediately()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { DoubleClickWindow = 0f, DoubleClickAction = null })));

        var press = engine.Tick(Held(GamepadButton.B), 0.016f);

        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(press));
    }

    [Fact]
    public void Double_Click_Action_Taps_A_Mouse_Button()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.RightBumper,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture
            {
                DoubleClickWindow = 0.25f,
                DoubleClickAction = new MouseButtonTarget(MouseButton.Right),
            })));

        Assert.Empty(engine.Tick(Held(GamepadButton.RightBumper), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));

        var second = engine.Tick(Held(GamepadButton.RightBumper), 0.05f);
        Assert.Equal(2, second.Count);
        Assert.Equal(new MouseDownCommand(MouseButton.Right), second[0]);
        Assert.Equal(new MouseUpCommand(MouseButton.Right), second[1]);
    }

    [Fact]
    public void Double_Click_Action_Scrolls_The_Wheel_Once()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture
            {
                DoubleClickWindow = 0.25f,
                DoubleClickAction = new WheelScrollTarget(new ScrollStickSettings()),
            })));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));

        var second = engine.Tick(Held(GamepadButton.B), 0.05f);
        Assert.Equal(new WheelCommand(0, 1), Assert.Single(second));
    }

    [Fact]
    public void Long_Press_Action_Fires_When_The_Threshold_Elapses()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressWithAction)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        var fired = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(2, fired.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Space, KeyModifiers.None), fired[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Space, KeyModifiers.None), fired[1]);

        // The long-press action owns the hold, so the single never fires.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));
    }

    [Fact]
    public void Long_Press_Action_Released_Before_The_Threshold_Fires_Nothing()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressWithAction)));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        // The press was consumed by the pending long-press: neither the long-press action
        // nor the single-click action fires.
        Assert.Empty(engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Long_Press_Action_Repeats_At_The_Interval()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressActionRepeating)));

        var commands = new List<InputCommand>();
        for (var i = 0; i < 20; i++)
        {
            commands.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Held for 1.0s: fires at 0.2s, then every 0.1s -> 0.2, 0.3, ... 1.0 = 9 activations,
        // every one of them the long-press action rather than the single.
        Assert.Equal(9, Taps(commands));
        Assert.All(
            commands.OfType<KeyDownCommand>(),
            down => Assert.Equal(GamepadKey.Space, down.Key));
    }

    [Fact]
    public void Long_Press_Action_Repeat_Stops_On_Release()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            LongPressActionRepeating)));

        var held = new List<InputCommand>();
        for (var i = 0; i < 10; i++)
        {
            held.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        Assert.Equal(4, Taps(held));
        Assert.All(
            held.OfType<KeyDownCommand>(),
            down => Assert.Equal(GamepadKey.Space, down.Key));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Both_Gesture_Actions_Fire_Their_Own_Targets()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            BothActions)));

        // A double-click resolves to its own action.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        var doubleClick = engine.Tick(Held(GamepadButton.B), 0.05f);
        Assert.Equal(new KeyDownCommand(GamepadKey.Space, KeyModifiers.None), doubleClick[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Space, KeyModifiers.None), doubleClick[1]);

        // A long press resolves to its own, different action.
        Assert.Empty(engine.Tick(Released, 0.2f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        var longPress = engine.Tick(Held(GamepadButton.B), 0.1f);
        Assert.Equal(2, longPress.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.W, KeyModifiers.Ctrl), longPress[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.W, KeyModifiers.Ctrl), longPress[1]);

        Assert.Empty(engine.Tick(Released, 0.1f));
    }

    [Fact]
    public void Gesture_Actions_Apply_To_Extra_Buttons_Too()
    {
        var engine = new MappingEngine(Profile(new MappingEntry
        {
            Source = new ButtonBinding(GamepadButton.Extra, 305)
            {
                Gesture = new ButtonGesture
                {
                    LongPressThreshold = 0.2f,
                    LongPressAction = new KeyPressTarget(GamepadKey.F13),
                },
            },
            Target = new KeyPressTarget(GamepadKey.Enter),
        }));

        Assert.Empty(engine.Tick(new ExtraButtonState(305), 0.1f));

        var fired = engine.Tick(new ExtraButtonState(305), 0.1f);
        Assert.Equal(2, fired.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.F13, KeyModifiers.None), fired[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.F13, KeyModifiers.None), fired[1]);
    }

    [Fact]
    public void Single_Click_Only_Fires_BeginHold_And_EndHold_On_The_Edges()
    {
        // A gesture object that enables nothing takes the zero-latency path: BeginHold on the
        // press edge, EndHold on the release edge, byte-for-byte as an ungestured button.
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture())));

        var press = engine.Tick(Held(GamepadButton.B), 0.016f);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(press));

        var release = engine.Tick(Released, 0.016f);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), Assert.Single(release));
    }

    [Fact]
    public void Click_Repeat_Fires_On_The_Press_Edge_With_Zero_Latency()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { ClickRepeatInterval = 0.1f })));

        // The initial activation is a Tap on the press edge: zero added latency, but a discrete
        // down+up because a repeat is a sequence of discrete activations.
        var press = engine.Tick(Held(GamepadButton.B), 0.016f);
        Assert.Equal(2, press.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), press[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), press[1]);
    }

    [Fact]
    public void Click_Repeat_Fires_Every_Interval_While_Held()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { ClickRepeatInterval = 0.1f })));

        var commands = new List<InputCommand>();
        for (var i = 0; i < 20; i++)
        {
            commands.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Held for 1.0s: fires on the press edge (t=0), then every 0.1s -> 0, 0.1, ... 0.9 = 10 taps.
        Assert.Equal(10, Taps(commands));
    }

    [Fact]
    public void Click_Repeat_Stops_On_Release()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { ClickRepeatInterval = 0.1f })));

        var held = new List<InputCommand>();
        for (var i = 0; i < 9; i++)
        {
            held.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Held for 0.45s: fires on the press edge, then every 0.1s -> 0, 0.1, 0.2, 0.3, 0.4 = 5 taps.
        Assert.Equal(5, Taps(held));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Click_Repeat_Stops_When_The_Release_Tick_Lands_On_A_Repeat_Boundary()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { ClickRepeatInterval = 0.1f })));

        // Ten held ticks, not the nine above: repeats land on even ticks, so the tenth leaves
        // RepeatElapsed at 0.05 and the release tick takes it to exactly the 0.1 interval.
        var held = new List<InputCommand>();
        for (var i = 0; i < 10; i++)
        {
            held.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        Assert.Equal(5, Taps(held));
        Assert.Empty(engine.Tick(Released, 0.05f));

        // A repeat firing on the release tick returns before the release edge is handled,
        // leaving the phase in ClickRepeating - so repeats continue after the button is up.
        for (var i = 0; i < 20; i++)
        {
            Assert.Empty(engine.Tick(Released, 0.05f));
        }
    }

    [Fact]
    public void Click_Repeat_Wins_Over_Long_Press_When_Both_Configured()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture
            {
                ClickRepeatInterval = 0.1f,
                LongPressThreshold = 0.4f,
                LongPressAction = new KeyPressTarget(GamepadKey.Space),
            })));

        var commands = new List<InputCommand>();
        for (var i = 0; i < 20; i++)
        {
            commands.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Click auto-repeat wins: the click fires 10 times, the long-press is inert (no Space).
        Assert.Equal(10, commands.Count(c => c is KeyDownCommand { Key: GamepadKey.Enter }));
        Assert.Equal(0, commands.Count(c => c is KeyDownCommand { Key: GamepadKey.Space }));
    }

    [Fact]
    public void Long_Press_Still_Works_When_Click_Repeat_Is_Off()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture { LongPressThreshold = 0.4f })));

        var commands = new List<InputCommand>();
        for (var i = 0; i < 20; i++)
        {
            commands.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Long-press fires once at 0.4s; no click auto-repeat.
        Assert.Equal(1, Taps(commands));
    }

    [Fact]
    public void Long_Press_With_None_Action_Does_Not_Consume_The_Press()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture
            {
                LongPressThreshold = 0.4f,
                LongPressAction = new NoneTarget(),
            })));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));

        // 无功能 is not a second function, so an early release still fires the single.
        var tap = engine.Tick(Released, 0.1f);
        Assert.Equal(2, tap.Count);
        Assert.Equal(new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None), tap[0]);
        Assert.Equal(new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None), tap[1]);
    }

    [Fact]
    public void None_Target_Emits_No_Commands()
    {
        var engine = new MappingEngine(Profile(Plain(GamepadButton.B, new NoneTarget())));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.016f));
        Assert.Empty(engine.Tick(Released, 0.016f));
    }

    [Fact]
    public void None_Target_As_A_Gesture_Action_Emits_No_Commands()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            new KeyPressTarget(GamepadKey.Enter),
            new ButtonGesture
            {
                DoubleClickWindow = 0.25f,
                DoubleClickAction = new NoneTarget(),
            })));

        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));

        // The double-click resolves to its NoneTarget action, which emits nothing.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
    }

    private sealed record ExtraButtonState(params int[] Codes) : IGamepadState
    {
        public GamepadButtonMask PressedButtons => GamepadButtonMask.None;

        public float LeftStickX => 0f;

        public float LeftStickY => 0f;

        public float RightStickX => 0f;

        public float RightStickY => 0f;

        public IReadOnlyList<int> ExtraButtons => Codes;
    }
}
