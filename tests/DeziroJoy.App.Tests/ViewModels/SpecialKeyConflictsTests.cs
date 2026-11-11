using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The conflict rules between the two global special keys and everything else on their
/// trigger buttons. Every expectation here is a statement about how the engine actually
/// behaves: a leader key switches from the mapping loop, so a gesture on the same mapping
/// delays or swallows the switch, while a precision key engages from the raw button and
/// ignores gestures entirely - but only when it sits on the mapping's own target, which is
/// the one position the engine registers. The hints only report; none of them rewrites the
/// configuration.
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
        MappingTarget? longPressAction = null)
        => new()
        {
            DoubleClickWindow = doubleClickWindow,
            LongPressThreshold = longPressThreshold,
            ClickRepeatInterval = clickRepeatInterval,
            DoubleClickAction = doubleClickAction,
            LongPressAction = longPressAction,
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

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("Leader 键", message);
        Assert.Contains("Back 键", message);
        Assert.Contains("双击窗口", message);
        Assert.Contains("切换预设", message);
    }

    [Fact]
    public void A_Leader_Key_With_A_Long_Press_That_Has_A_Function_Is_Swallowed()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                Leader(),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: new KeyPressTarget(GamepadKey.A))),
        };

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("长按", message);
        Assert.Contains("吞掉", message);
        Assert.Contains("A", message);
    }

    [Fact]
    public void A_Leader_Key_With_A_Plain_Long_Press_Still_Fires_On_A_Quick_Press()
    {
        // The long press carries no function of its own, so an early release still fires the
        // single - the engine's own rule, and the reason there is nothing to warn about.
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

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("长按重复触发", message);
        Assert.Contains("反复", message);
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
    public void A_Long_Press_Special_Key_Swallows_The_Entrys_Own_Click()
    {
        var mappings = new[]
        {
            Button(
                GamepadButton.Back,
                new KeyPressTarget(GamepadKey.Enter),
                Gesture(
                    longPressThreshold: ButtonGesture.DefaultLongPressThreshold,
                    longPressAction: Leader())),
        };

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("长按", message);
        Assert.Contains("Enter", message);
        Assert.Contains("吞掉", message);
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

        var message = Message(SpecialKeyConflicts.Analyze(mappings));

        Assert.Contains("另一条映射", message);
        Assert.Contains("Enter", message);
        Assert.Contains("同时", message);
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
    public void A_Sibling_Whose_Click_Its_Own_Long_Press_Swallows_Is_Not_A_Double_Fire()
    {
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

        Assert.Empty(SpecialKeyConflicts.Analyze(mappings));
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
