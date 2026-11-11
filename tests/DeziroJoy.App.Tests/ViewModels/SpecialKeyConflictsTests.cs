using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The conflict rules between the two global special keys and everything else on their
/// trigger buttons. Every expectation here is a statement about how the engine actually
/// behaves: a leader key switches from the mapping loop, so a gesture on the same mapping
/// delays the switch (the double-click window) or repeats it (click auto-repeat, or a long
/// press with a repeat interval that falls back to the click), while a precision key engages
/// from the raw button and ignores gestures entirely - but only from the click position, the
/// one the engine registers it from. The engine swallows nothing: no long-press action
/// consumes the press, so a sibling's quick press always fires beside the global key. The
/// hints only report; none of them rewrites the configuration.
/// </summary>
public class SpecialKeyConflictsTests
{
    private static MappingEntry Button(
        GamepadButton button,
        MappingTarget target,
        ButtonGesture? gesture = null,
        bool enabled = true)
        => new()
        {
            Source = new ButtonBinding(button) { Gesture = gesture },
            Target = target,
            Enabled = enabled,
        };

    private static ButtonGesture Gesture(
        float doubleClickWindow = 0f,
        float longPressThreshold = 0f,
        float clickRepeatInterval = 0f,
        MappingTarget? doubleClickAction = null,
        MappingTarget? longPressAction = null,
        float longPressRepeatInterval = 0f)
        => new()
        {
            DoubleClickWindow = doubleClickWindow,
            LongPressThreshold = longPressThreshold,
            ClickRepeatInterval = clickRepeatInterval,
            DoubleClickAction = doubleClickAction,
            LongPressAction = longPressAction,
            LongPressRepeatInterval = longPressRepeatInterval,
        };

    private static SpecialKeyTarget Leader(
        LeaderSwitchMode switchMode = LeaderSwitchMode.Press,
        int slotA = 1,
        int slotB = 2)
        => new(SpecialKeyKind.Leader, PrecisionMode.Toggle, switchMode, slotA, slotB);

    private static SpecialKeyTarget Precision(PrecisionMode mode = PrecisionMode.Hold)
        => new(SpecialKeyKind.Precision, mode);

    private static string Message(IReadOnlyList<SpecialKeyConflict> conflicts)
        => Assert.Single(conflicts).Message;

    [Fact]
    public void A_Profile_Without_Special_Keys_Has_No_Conflicts()
    {
        var mappings = new[]
        {
            Button(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter)),
            Button(GamepadButton.A, new KeyPressTarget(GamepadKey.Escape)),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Bare_Leader_Key_On_Its_Own_Button_Is_No_Conflict()
    {
        var mappings = new[] { Button(GamepadButton.Back, Leader()) };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Leader_Key_With_A_Double_Click_Gesture_Delays_The_Switch()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader(), Gesture(doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow)),
        };

        Assert.Equal(
            "「Leader 键」设在Back 键的单击上，但该按钮还配了双击手势：单击要等双击窗口（0.25 秒）结束才切换预设；窗口内按第二次时触发的是双击手势，双击若另配了动作，该次按下不会切换预设。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Leader_Key_With_A_Long_Press_That_Has_A_Function_Still_Switches_On_A_Quick_Press()
    {
        // The engine swallows nothing: a release before the threshold always fires the single,
        // so the long press's own action is an extra function, not a stolen click.
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                Leader(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: new KeyPressTarget(GamepadKey.A))),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Leader_Key_With_A_Plain_Long_Press_Still_Fires_On_A_Quick_Press()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader(), Gesture(longPressThreshold: ButtonGesture.DefaultLongPressThreshold)),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Leader_Key_With_Click_Auto_Repeat_Switches_Repeatedly()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                Leader(),
                Gesture(clickRepeatInterval: ButtonGesture.DefaultClickRepeatInterval)),
        };

        Assert.Equal(
            "「Leader 键」设在Back 键的单击上，但该按钮开启了长按重复触发：按住期间会每隔 0.1 秒反复切换一次预设。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Leader_Key_With_A_Bare_Long_Press_Repeat_Switches_Repeatedly()
    {
        // The long press carries no action of its own, so every repeat falls back to the
        // mapping's own target - the leader key - and the preset flips on each one.
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                Leader(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    clickRepeatInterval: 0f,
                    longPressRepeatInterval: 0.2f)),
        };

        Assert.Equal(
            "「Leader 键」设在Back 键的单击上，但该按钮的长按配了重复间隔（每 0.2 秒一次）：按住越过长按阈值后，长按会反复回落到该 Leader 键，预设随之反复切换。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Long_Press_With_An_Action_Of_Its_Own_Does_Not_Repeat_The_Leader()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                Leader(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressRepeatInterval: 0.2f,
                    longPressAction: new KeyPressTarget(GamepadKey.A))),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void Gestures_Leave_A_Precision_Key_Alone()
    {
        // Precision engages from the raw button, not from the mapping loop, so the very
        // gestures that delay a leader switch change nothing for a precision key.
        var mappings = new[]
        {
            Button(
                GamepadButton.LeftStick,
                Precision(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    clickRepeatInterval: ButtonGesture.DefaultClickRepeatInterval)),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Long_Press_Special_Key_Costs_The_Click_Nothing()
    {
        // The engine lets the gestures coexist: a quick press fires the click and only a hold
        // past the threshold fires the long press, so the click stays reachable.
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new KeyPressTarget(GamepadKey.Enter),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: Leader())),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Long_Press_Special_Key_Over_无功能_Costs_Nothing()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: Leader())),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void Another_Mapping_On_The_Leader_Button_Fires_Beside_It()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter)),
        };

        Assert.Equal(
            "Back 键上还有另一条映射（单击：Enter）：每次按下触发「Leader 键」的同时也会触发Enter。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Disabled_Sibling_Never_Fires_So_It_Never_Conflicts()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter), enabled: false),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Disabled_Leader_Key_Conflicts_With_Nothing()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader(), enabled: false),
            Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter)),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Sibling_With_A_Long_Press_Action_Double_Fires_Too()
    {
        // No long-press action consumes the press any more, so the sibling's quick press fires
        // the click beside the leader - the exclusion that once hid this is gone. The sibling's
        // long-press action is an output of its own, so it is reported beside it.
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            Button(
                GamepadButton.Back,
                new KeyPressTarget(GamepadKey.Enter),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: new KeyPressTarget(GamepadKey.A))),
        };

        var messages = SpecialKeyConflicts.Analyze(mappings).Select(conflict => conflict.Message).ToList();

        Assert.Contains("Back 键上还有另一条映射（单击：Enter）：每次按下触发「Leader 键」的同时也会触发Enter。", messages);
        Assert.Contains("Back 键上还有另一条映射（长按：A）：长按该按钮时，「Leader 键」在该次按下时已经切换，按住期间还会触发A。", messages);
    }

    [Fact]
    public void A_Precision_Key_On_A_Gesture_Position_Never_Engages()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.LeftStick,
                new NoneTarget(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: Precision())),
        };

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("精准键", message);
        Assert.Contains("不会生效", message);
        Assert.Contains("单击", message);
    }

    [Fact]
    public void A_Precision_Key_On_The_Long_Press_Never_Engages()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.LeftStick,
                new NoneTarget(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: Precision(PrecisionMode.Toggle))),
        };

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("精准键", message);
        Assert.Contains("不会生效", message);
        Assert.Contains("长按", message);
    }

    [Fact]
    public void A_Leader_Key_On_A_Gesture_Position_Works_And_Is_No_Conflict()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new KeyPressTarget(GamepadKey.Enter),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: Leader())),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Double_Click_Leader_Fires_Beside_A_Sibling_Click_On_Both_Presses()
    {
        // The engine's per-mapping gesture state runs the sibling beside the key: the first
        // press fires Enter, the second fires Enter and the switch.
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: Leader())),
            Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter)),
        };

        Assert.Equal(
            "Back 键上还有另一条映射（单击：Enter）：双击触发「Leader 键」时，两次按下都会触发Enter，第二次按下与该切换同发。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Long_Press_Leader_Holds_A_Sibling_Click_While_Switching()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: Leader())),
            Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter)),
        };

        Assert.Equal(
            "Back 键上还有另一条映射（单击：Enter）：长按触发「Leader 键」期间，Enter一直被按住，与该切换同发。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Siblings_Double_Click_Action_Fires_Beside_A_Click_Leader()
    {
        // The sibling's own click is 无功能, but its double-click is a real key: the second
        // press fires two switches and the key, and the click-only view would miss it.
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: new KeyPressTarget(GamepadKey.A))),
        };

        Assert.Equal(
            "Back 键上还有另一条映射（双击：A）：双击该按钮时，每次按下都会触发「Leader 键」，第二次按下还会触发A。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Siblings_Long_Press_Action_Fires_Beside_A_Click_Leader()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: new KeyPressTarget(GamepadKey.A))),
        };

        Assert.Equal(
            "Back 键上还有另一条映射（长按：A）：长按该按钮时，「Leader 键」在该次按下时已经切换，按住期间还会触发A。",
            Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void A_Double_Click_Leader_And_A_Siblings_Long_Press_Never_Fire_Together()
    {
        // A double press releases before any long-press threshold, and a hold never
        // completes a double-click, so the two never meet and nothing is reported.
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: Leader())),
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: new KeyPressTarget(GamepadKey.A))),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Long_Press_Leader_And_A_Siblings_Double_Click_Never_Fire_Together()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: Leader())),
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: new KeyPressTarget(GamepadKey.A))),
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Precision_Key_On_A_Gesture_Position_Has_No_Sibling_Rule()
    {
        // Rule 6 already says the key never engages, so there is no trigger to collide with.
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new NoneTarget(),
                Gesture(
                    doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                    doubleClickAction: Precision())),
            Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter)),
        };

        var conflict = Assert.Single(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("不会生效", conflict.Message);
        Assert.DoesNotContain("另一条映射", conflict.Message);
    }

    [Fact]
    public void A_Double_Click_Window_The_Profile_Carries_Is_The_One_Named()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader(), Gesture(doubleClickWindow: 0.4f)),
        };

        Assert.Contains("双击窗口（0.4 秒）", Message(SpecialKeyConflicts.Analyze(mappings)));
    }

    [Fact]
    public void The_Same_Button_On_Different_Codes_Are_Different_Buttons()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.Extra, 7),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void A_Stick_Shared_With_A_Special_Key_Button_Is_No_Conflict()
    {
        var mappings = new[]
        {
            Button(GamepadButton.Back, Leader()),
            new MappingEntry
            {
                Source = new StickBinding(GamepadStick.Right),
                Target = new MouseMoveTarget(new MouseStickSettings()),
            },
        };

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
    }

    [Fact]
    public void Involving_Filters_By_Either_Side_Of_The_Collision()
    {
        var leader = Button(GamepadButton.Back, Leader());
        var sibling = Button(GamepadButton.Back, new KeyPressTarget(GamepadKey.Enter));
        var unrelated = Button(GamepadButton.A, new KeyPressTarget(GamepadKey.Escape));

        var conflicts = SpecialKeyConflicts.Analyze([leader, sibling, unrelated]);

        var conflict = Assert.Single(conflicts);
        Assert.Equal(leader.Id, conflict.SpecialEntryId);
        Assert.Equal(sibling.Id, conflict.OtherEntryId);

        Assert.Equal(conflict, Assert.Single(SpecialKeyConflicts.Involving(conflicts, leader.Id)));
        Assert.Equal(conflict, Assert.Single(SpecialKeyConflicts.Involving(conflicts, sibling.Id)));
        Assert.Empty(SpecialKeyConflicts.Involving(conflicts, unrelated.Id));
    }

    [Fact]
    public void The_Button_And_Global_Key_Are_Named_The_Way_The_Pickers_Name_Them()
    {
        var mappings = new[]
        {
            Button(GamepadButton.LeftStick, Precision()),
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftStick),
                Target = new KeyPressTarget(GamepadKey.Enter),
            },
        };

        var conflict = Assert.Single(SpecialKeyConflicts.Analyze(mappings));

        Assert.Equal("左摇杆按下", conflict.ButtonLabel);
        Assert.Equal("精准键", conflict.GlobalKeyLabel);
        Assert.Contains("左摇杆按下", conflict.Message);
        Assert.Contains("精准键", conflict.Message);
    }

    [Fact]
    public void Key_Of_Reads_The_Target_First_Then_The_Gesture_Actions()
    {
        var click = Button(GamepadButton.Back, Leader());
        var gesture = Button(
            GamepadButton.A,
            new KeyPressTarget(GamepadKey.Escape),
            Gesture(
                doubleClickWindow: ButtonGesture.DefaultDoubleClickWindow,
                doubleClickAction: Precision()));
        var none = Button(GamepadButton.B, new KeyPressTarget(GamepadKey.Enter));

        Assert.Equal(SpecialKeyKind.Leader, SpecialKeyConflicts.KeyOf(click)!.Kind);
        Assert.Equal(SpecialKeyKind.Precision, SpecialKeyConflicts.KeyOf(gesture)!.Kind);
        Assert.Null(SpecialKeyConflicts.KeyOf(none));
    }

    [Fact]
    public void Entry_For_Finds_The_First_Mapping_Of_One_Kind()
    {
        var precision = Button(GamepadButton.LeftStick, Precision());
        var leader = Button(GamepadButton.Back, Leader());
        var mappings = new[] { precision, leader };

        Assert.Same(precision, SpecialKeyConflicts.EntryFor(mappings, SpecialKeyKind.Precision));
        Assert.Same(leader, SpecialKeyConflicts.EntryFor(mappings, SpecialKeyKind.Leader));
        Assert.Null(SpecialKeyConflicts.EntryFor([Button(GamepadButton.A, new KeyPressTarget(GamepadKey.Escape))], SpecialKeyKind.Leader));
    }
}
