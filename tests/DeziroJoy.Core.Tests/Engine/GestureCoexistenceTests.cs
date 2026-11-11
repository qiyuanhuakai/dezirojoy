using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Engine;

/// <summary>
/// FP02 gesture coexistence: one button carrying 单击 / 双击 / 长按 at the same time. Every
/// configured gesture must resolve on its own timing - the click on release when no second
/// press completed a double-click, the double-click on the second press inside the window,
/// the long press at its threshold - and no combination may swallow another gesture. All
/// timing comes from the tick's deltaSeconds; there is no clock.
/// </summary>
public class GestureCoexistenceTests
{
    private const float Window = 0.25f;
    private const float Threshold = 0.4f;

    private static GamepadProfile Profile(params MappingEntry[] mappings) => new()
    {
        Name = "coexistence-profile",
        Mappings = mappings,
    };

    private static MappingEntry Gestured(GamepadButton button, MappingTarget target, ButtonGesture gesture)
        => new() { Source = new ButtonBinding(button) { Gesture = gesture }, Target = target };

    private static GamepadState Held(GamepadButton button)
        => GamepadState.Create(button.ToMask(), 0, 0, 0, 0);

    private static GamepadState Released => GamepadState.Neutral;

    private static readonly KeyPressTarget Click = new(GamepadKey.Enter);
    private static readonly KeyPressTarget DoubleClick = new(GamepadKey.Space);
    private static readonly KeyComboTarget LongPress = new(KeyModifiers.Ctrl, GamepadKey.W);

    private static List<InputCommand> Tap(KeyPressTarget target) =>
        [new KeyDownCommand(target.Key, KeyModifiers.None), new KeyUpCommand(target.Key, KeyModifiers.None)];

    private static List<InputCommand> Tap(KeyComboTarget target) =>
        [new KeyDownCommand(target.Key, target.Modifiers), new KeyUpCommand(target.Key, target.Modifiers)];

    [Fact]
    public void Click_Plus_Double_Click_Every_Gesture_Fires()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { DoubleClickWindow = Window, DoubleClickAction = DoubleClick })));

        // A quick press: the window closes after the release and the single fires as a tap.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.2f));

        // A second press inside the window fires the double-click's own action.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(DoubleClick), engine.Tick(Held(GamepadButton.B), 0.05f));

        // Holding past the window resolves the single as a hold: down now, up on release.
        Assert.Empty(engine.Tick(Released, 0.2f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.B), 0.1f)));
        Assert.Equal(
            new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, 0.1f)));
    }

    [Fact]
    public void Click_Plus_Long_Press_Every_Gesture_Fires_Without_Actions()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { LongPressThreshold = Threshold })));

        // A quick press fires the single on release.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.1f));

        // Holding past the threshold fires the long press, which re-fires the target.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Held(GamepadButton.B), 0.1f));

        // The release only ends the cycle: no trailing activation.
        Assert.Empty(engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Click_Plus_Long_Press_Action_Quick_Press_Still_Fires_The_Click()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { LongPressThreshold = Threshold, LongPressAction = LongPress })));

        // The long press carries its own action, but a press that never reached the
        // threshold is still a click: it fires the click's own target on release.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.1f));

        // The long press still resolves to its own action at the threshold.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(LongPress), engine.Tick(Held(GamepadButton.B), 0.1f));

        // The release only ends the cycle.
        Assert.Empty(engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Click_Plus_Long_Press_Special_Key_Action_Coexists()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture
            {
                LongPressThreshold = Threshold,
                LongPressAction = new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Press),
            })));

        // A quick press fires the click and touches no preset: the leader key is the long
        // press's own action, so it never sees a press that reached no threshold.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));

        // The long press fires the leader key once.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(new SwitchPresetCommand(2), Assert.Single(engine.Tick(Held(GamepadButton.B), 0.1f)));

        // A Press leader emits nothing on the release edge.
        Assert.Empty(engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Double_Click_Plus_Long_Press_Every_Gesture_Fires_Without_Actions()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { DoubleClickWindow = Window, LongPressThreshold = Threshold })));

        // A second press inside the window fires the double-click (re-firing the target).
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Held(GamepadButton.B), 0.05f));

        // A quick press fires the single when the window closes after the release.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.15f));

        // Holding past the threshold fires the long press.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));
    }

    [Fact]
    public void Double_Click_Plus_Long_Press_Action_Handed_Over_Release_Fires_The_Click()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture
            {
                DoubleClickWindow = Window,
                LongPressThreshold = Threshold,
                DoubleClickAction = DoubleClick,
                LongPressAction = LongPress,
            })));

        // Held past the double-click window but released before the long-press threshold:
        // the hand-over to the pending long press must not swallow the single.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.05f));

        // The double-click still fires on the second press inside the window.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(DoubleClick), engine.Tick(Held(GamepadButton.B), 0.05f));

        // The long press still fires its own action at the threshold.
        Assert.Empty(engine.Tick(Released, 0.2f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(LongPress), engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));
    }

    [Fact]
    public void All_Three_Gestures_With_Their_Own_Actions_Never_Swallow_Each_Other()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture
            {
                DoubleClickWindow = Window,
                LongPressThreshold = Threshold,
                DoubleClickAction = DoubleClick,
                LongPressAction = LongPress,
            })));

        // Quick press: the single fires when the window closes after the release.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.2f));

        // Second press inside the window: the double-click action fires.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(DoubleClick), engine.Tick(Held(GamepadButton.B), 0.05f));

        // Held past the window, released before the threshold: still the single.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.05f));

        // Held past the threshold: the long-press action fires, release ends cleanly.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(LongPress), engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void All_Three_Gestures_With_A_Complex_Shortcut_Long_Press_Coexist()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.LeftBumper,
            Click,
            new ButtonGesture
            {
                DoubleClickWindow = Window,
                LongPressThreshold = 0.5f,
                DoubleClickAction = DoubleClick,
                LongPressAction = new ComplexShortcutTarget { Script = "hold Alt" },
            })));

        // A quick press: the single fires, the program stays unstarted.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.2f));

        // A second press inside the window fires the double-click action.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(DoubleClick), engine.Tick(Held(GamepadButton.LeftBumper), 0.05f));

        // Held past the window, released before the threshold: still the single.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.05f));

        // The long press runs the program: Alt goes down on the threshold tick (0.25 + 0.25
        // reaches the 0.5s threshold)...
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f)));

        // ...and the release tick stops it and hands Alt back.
        Assert.Empty(engine.Tick(Held(GamepadButton.LeftBumper), 0.25f));
        Assert.Equal(
            new KeyUpCommand(GamepadKey.LeftAlt, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, 0.25f)));
    }

    [Fact]
    public void Second_Press_Exactly_At_The_Window_Edge_Still_Double_Clicks()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { DoubleClickWindow = Window, DoubleClickAction = DoubleClick })));

        // 0.05 + 0.05 + 0.10 + 0.05 = 0.25s of elapsed time when the second press lands:
        // the press edge is arbitrated before the window check, so it still double-clicks.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.1f));
        Assert.Equal(Tap(DoubleClick), engine.Tick(Held(GamepadButton.B), 0.05f));

        // One tick later the window has closed: the same press starts a fresh single.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.2f));
    }

    [Fact]
    public void Long_Press_Fires_Exactly_At_The_Threshold_And_The_Release_Wins_On_The_Crossing_Tick()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { LongPressThreshold = Threshold, LongPressAction = LongPress })));

        // Four 0.1s held ticks reach exactly the 0.4s threshold.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(LongPress), engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Released, 0.1f));

        // A release tick that carries elapsed time to exactly the threshold ends the cycle
        // as a click: the press never completed a held tick past the threshold.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.1f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Rapid_Presses_Resolve_One_Gesture_Each()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { DoubleClickWindow = Window, LongPressThreshold = Threshold })));

        // Press/release/press inside the window: one double-click.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Held(GamepadButton.B), 0.05f));

        // The next press starts a fresh cycle; released before the window closes it is a
        // single, never a leftover from the double-click.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.2f));

        // And the cycle after that is independent again.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Held(GamepadButton.B), 0.05f));
    }

    [Fact]
    public void Long_Press_Repeat_Release_Leaves_No_Trailing_Events()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture
            {
                LongPressThreshold = 0.2f,
                LongPressRepeatInterval = 0.1f,
                LongPressAction = LongPress,
            })));

        var held = new List<InputCommand>();
        for (var i = 0; i < 4; i++)
        {
            held.AddRange(engine.Tick(Held(GamepadButton.B), 0.05f));
        }

        // Fires once at the 0.2s threshold: the long-press action, never the single.
        Assert.Equal(Tap(LongPress), held);

        // One more interval repeats the long-press action.
        Assert.Equal(Tap(LongPress), engine.Tick(Held(GamepadButton.B), 0.1f));

        // The release ends the cycle: no repeat, no single, nothing afterwards.
        Assert.Empty(engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Unconfigured_Double_Click_Produces_No_Extra_Events()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { LongPressThreshold = Threshold })));

        // Two quick presses are two singles: with no double-click configured, the second
        // press never resolves as anything but a fresh click.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Equal(Tap(Click), engine.Tick(Released, 0.05f));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }

    [Fact]
    public void Unconfigured_Long_Press_Produces_No_Extra_Events()
    {
        var engine = new MappingEngine(Profile(Gestured(
            GamepadButton.B,
            Click,
            new ButtonGesture { DoubleClickWindow = Window, DoubleClickAction = DoubleClick })));

        // Holding well past the double-click window resolves the single as a hold. No
        // long-press action is configured, so seconds of extra holding fire nothing else.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.05f));
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 0.1f));
        Assert.Equal(
            new KeyDownCommand(GamepadKey.Enter, KeyModifiers.None),
            Assert.Single(engine.Tick(Held(GamepadButton.B), 0.1f)));

        // The release ends the hold, and nothing trails it.
        Assert.Empty(engine.Tick(Held(GamepadButton.B), 1f));
        Assert.Equal(
            new KeyUpCommand(GamepadKey.Enter, KeyModifiers.None),
            Assert.Single(engine.Tick(Released, 0.1f)));
        Assert.Empty(engine.Tick(Released, 0.5f));
    }
}
