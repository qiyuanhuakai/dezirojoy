using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The sensitivity panels must explain what each option actually changes. These tests pin the
/// wording to the engine's real math so a description cannot drift into generic filler.
/// </summary>
public class MappingDescriptionsTests
{
    private static readonly ResponseCurve[] EveryCurve = Enum.GetValues<ResponseCurve>();

    [Fact]
    public void Every_Mouse_Option_Has_A_Description()
    {
        var descriptions = new[]
        {
            MappingDescriptions.MouseMaxSpeed(),
            MappingDescriptions.MouseDeadZone(),
            MappingDescriptions.MouseCurveStrength(),
            MappingDescriptions.MouseResponseTime(),
            MappingDescriptions.MousePrecisionScale(),
            MappingDescriptions.MouseInvertY(),
        };

        Assert.All(descriptions, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(descriptions.Length, descriptions.Distinct().Count());
    }

    [Fact]
    public void Every_Scroll_Option_Has_A_Description()
    {
        var descriptions = new[]
        {
            MappingDescriptions.ScrollRate(),
            MappingDescriptions.ScrollDeadZone(),
            MappingDescriptions.ScrollResponseTime(),
            MappingDescriptions.ScrollInvertY(),
            MappingDescriptions.ScrollEnableHorizontal(),
        };

        Assert.All(descriptions, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(descriptions.Length, descriptions.Distinct().Count());
    }

    [Fact]
    public void Every_Curve_Has_Its_Own_Description()
    {
        var descriptions = EveryCurve.Select(MappingDescriptions.MouseCurve).ToArray();

        Assert.All(descriptions, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(EveryCurve.Length, descriptions.Distinct().Count());
    }

    [Theory]
    [InlineData(ResponseCurve.Linear, "线性")]
    [InlineData(ResponseCurve.Precise, "平滑")]
    [InlineData(ResponseCurve.Aggressive, "二次")]
    [InlineData(ResponseCurve.Responsive, "平方根")]
    public void The_Curve_Description_Names_The_Actual_Shape(ResponseCurve curve, string expected)
    {
        Assert.Contains(expected, MappingDescriptions.MouseCurve(curve));
    }

    [Fact]
    public void Mouse_Speed_Is_Described_In_Physical_Pixels_Per_Second_Decoupled_From_DPI()
    {
        var text = MappingDescriptions.MouseMaxSpeed();

        Assert.Contains("像素/秒", text);
        Assert.Contains("DPI", text);
        Assert.Contains("分辨率", text);
    }

    [Fact]
    public void The_Physical_Unit_Note_States_The_Projects_Key_Differentiator()
    {
        var text = MappingDescriptions.PhysicalSpeedUnit();

        Assert.Contains("像素/秒", text);
        Assert.Contains("DPI", text);
        Assert.Contains("分辨率", text);
    }

    [Fact]
    public void The_Physical_Unit_Note_No_Longer_Claims_Full_Decoupling()
    {
        // The output is a relative displacement, so the OS pointer-speed setting scales it
        // afterwards: "完全解耦" promised a screen speed the app cannot guarantee.
        var text = MappingDescriptions.PhysicalSpeedUnit();

        Assert.DoesNotContain("完全解耦", text);
        Assert.Contains("相对位移", text);
        Assert.Contains("指针", text);
    }

    [Fact]
    public void The_Max_Speed_Description_Admits_The_OS_Pointer_Speed_Scales_The_Output()
    {
        var text = MappingDescriptions.MouseMaxSpeed();

        Assert.Contains("相对位移", text);
        Assert.Contains("指针", text);
        Assert.DoesNotContain("完全解耦", text);
    }

    [Fact]
    public void The_Deadzone_Description_Says_What_It_Removes_And_That_Top_Speed_Survives()
    {
        var mouse = MappingDescriptions.MouseDeadZone();
        var scroll = MappingDescriptions.ScrollDeadZone();

        // Radial, not per-axis: the engine clamps the result to unit magnitude.
        Assert.Contains("圆", mouse);
        Assert.Contains("圆", scroll);

        // The deadzone discards the inner region and rescales the rest onto full deflection,
        // so raising it costs fine control but never the maximum speed.
        Assert.Contains("最高速度", mouse);
        Assert.Contains("速率", scroll);
    }

    [Fact]
    public void The_Response_Time_Description_Names_The_Exponential_Smoothing_Constant()
    {
        Assert.Contains("指数", MappingDescriptions.MouseResponseTime());
        Assert.Contains("指数", MappingDescriptions.ScrollResponseTime());
        Assert.Contains("秒", MappingDescriptions.MouseResponseTime());
    }

    [Fact]
    public void The_Curve_Strength_Description_Explains_The_Blend_With_Linear()
    {
        var text = MappingDescriptions.MouseCurveStrength();

        Assert.Contains("线性", text);
        Assert.Contains("0", text);
        Assert.Contains("1", text);
    }

    [Fact]
    public void The_Curve_Strength_Description_No_Longer_Promises_Exaggeration_Beyond_Full()
    {
        // Strength is clamped to [0,1], so the copy must not claim a value above 1 doubles
        // the curve - that path no longer exists and reading it would mislead the user.
        var text = MappingDescriptions.MouseCurveStrength();

        Assert.DoesNotContain("加倍", text);
        Assert.DoesNotContain("大于 1", text);
    }

    [Fact]
    public void The_Precision_Description_Says_It_Is_A_Multiplier_While_The_Button_Is_Held()
    {
        var text = MappingDescriptions.MousePrecisionScale();

        Assert.Contains("倍率", text);
        Assert.Contains("按住", text);
    }

    [Fact]
    public void The_Invert_Descriptions_Say_The_Direction_Is_Flipped()
    {
        Assert.Contains("取反", MappingDescriptions.MouseInvertY());
        Assert.Contains("取反", MappingDescriptions.ScrollInvertY());
    }

    [Fact]
    public void The_Scroll_Rate_Description_Uses_Wheel_Notches_Per_Second()
    {
        var text = MappingDescriptions.ScrollRate();

        Assert.Contains("格/秒", text);
        Assert.Contains("累积", text);
    }

    [Fact]
    public void The_Horizontal_Scroll_Description_Says_What_Unchecking_Costs()
    {
        var text = MappingDescriptions.ScrollEnableHorizontal();

        Assert.Contains("水平", text);
        Assert.Contains("忽略", text);
    }

    [Fact]
    public void The_Mouse_Panel_Exposes_Every_Description_To_The_View()
    {
        var vm = new MouseSensitivityViewModel();

        Assert.Equal(MappingDescriptions.MouseMaxSpeed(), vm.MaxSpeedHint);
        Assert.Equal(MappingDescriptions.MouseDeadZone(), vm.DeadZoneHint);
        Assert.Equal(MappingDescriptions.MouseCurveStrength(), vm.CurveStrengthHint);
        Assert.Equal(MappingDescriptions.MouseResponseTime(), vm.ResponseTimeHint);
        Assert.Equal(MappingDescriptions.MousePrecisionScale(), vm.PrecisionScaleHint);
        Assert.Equal(MappingDescriptions.MouseInvertY(), vm.InvertYHint);
        Assert.Equal(MappingDescriptions.PhysicalSpeedUnit(), vm.PhysicalUnitHint);
    }

    [Fact]
    public void The_Scroll_Panel_Exposes_Every_Description_To_The_View()
    {
        var vm = new ScrollSensitivityViewModel();

        Assert.Equal(MappingDescriptions.ScrollRate(), vm.RateHint);
        Assert.Equal(MappingDescriptions.ScrollDeadZone(), vm.DeadZoneHint);
        Assert.Equal(MappingDescriptions.ScrollResponseTime(), vm.ResponseTimeHint);
        Assert.Equal(MappingDescriptions.ScrollInvertY(), vm.InvertYHint);
        Assert.Equal(MappingDescriptions.ScrollEnableHorizontal(), vm.EnableHorizontalHint);
    }

    [Fact]
    public void The_Curve_Hint_Follows_The_Selected_Curve()
    {
        var mouse = new MouseSensitivityViewModel();
        var scroll = new ScrollSensitivityViewModel();

        Assert.Equal(MappingDescriptions.MouseCurve(ResponseCurve.Precise), mouse.CurveHint);
        Assert.Equal(MappingDescriptions.ScrollCurve(ResponseCurve.Precise), scroll.CurveHint);

        mouse.Curve = ResponseCurve.Aggressive;
        scroll.Curve = ResponseCurve.Responsive;

        Assert.Equal(MappingDescriptions.MouseCurve(ResponseCurve.Aggressive), mouse.CurveHint);
        Assert.Equal(MappingDescriptions.ScrollCurve(ResponseCurve.Responsive), scroll.CurveHint);
    }

    [Fact]
    public void The_Source_Description_Names_The_Direction_With_The_Pickers_Own_Label()
    {
        // The two cross combinations carry a direction, and the description names it with
        // the direction picker's own labels, so the editor header and the mapping rows can
        // never disagree with the picker about which deflection a mapping uses.
        Assert.Equal(
            "左摇杆（上）",
            MappingDescriptions.Source(new StickBinding(GamepadStick.Left) { Direction = InputDirection.Up }));
        Assert.Equal(
            "A 键（右）",
            MappingDescriptions.Source(new ButtonBinding(GamepadButton.A) { Direction = InputDirection.Right }));

        foreach (var direction in Enum.GetValues<InputDirection>())
        {
            var label = DirectionCategories.Label(direction);

            Assert.Contains(
                label,
                MappingDescriptions.Source(new StickBinding(GamepadStick.Left) { Direction = direction }));
            Assert.Contains(
                label,
                MappingDescriptions.Source(new ButtonBinding(GamepadButton.A) { Direction = direction }));
        }
    }

    [Fact]
    public void A_Native_Combination_Reads_As_The_Source_Alone()
    {
        // A direction is meaningless for a native pairing and none is written, so the
        // description carries no direction either - 上/下/左/右 stays invisible exactly
        // where it has no meaning.
        Assert.Equal("左摇杆", MappingDescriptions.Source(new StickBinding(GamepadStick.Left)));
        Assert.Equal("右摇杆", MappingDescriptions.Source(new StickBinding(GamepadStick.Right)));
        Assert.Equal("A 键", MappingDescriptions.Source(new ButtonBinding(GamepadButton.A)));
        Assert.Equal("十字键 ←", MappingDescriptions.Source(new ButtonBinding(GamepadButton.DPadLeft)));
    }

    [Fact]
    public void A_Complex_Shortcut_Reads_As_Plain_Language_Not_Record_Text()
    {
        // The script is DSL text, so its raw form would read as code in a mapping row.
        // The row shows what the program does instead - and never the record ToString.
        var text = MappingDescriptions.Target(new ComplexShortcutTarget { Script = "hold Alt\nevery 1s tap Tab" });

        Assert.Equal("复杂快捷键：持续按住 Alt；每 1 秒点按 Tab", text);
        Assert.DoesNotContain("ComplexShortcutTarget", text);
        Assert.DoesNotContain("Script", text);
    }

    [Fact]
    public void The_Complex_Shortcut_Summary_Names_Every_Step_In_Order()
    {
        var steps = ComplexShortcutDsl.Parse("tap A\nhold B\nrelease B\nwait 500ms\nevery 2s tap C");

        Assert.Equal(
            "点按 A；持续按住 B；松开 B；等待 0.5 秒；每 2 秒点按 C",
            MappingDescriptions.ComplexShortcutSteps(steps));
    }

    [Fact]
    public void An_Unparseable_Complex_Shortcut_Still_Names_The_Kind()
    {
        // A hand-edited profile the engine refuses must not leak its parse failure into
        // a mapping row: the kind alone is what the row can honestly say.
        Assert.Equal(
            "复杂快捷键",
            MappingDescriptions.Target(new ComplexShortcutTarget { Script = "frobnicate" }));
    }

    [Theory]
    [InlineData(SpecialKeyKind.Precision, "精准键")]
    [InlineData(SpecialKeyKind.Leader, "Leader 键")]
    [InlineData(SpecialKeyKind.Keyboard, "屏幕键盘")]
    public void A_Special_Key_Target_Reads_As_Its_Picker_Name_Not_Record_Text(SpecialKeyKind kind, string expected)
    {
        // The Core record's ToString would read as debug output in a mapping row, and the
        // editor's header already names the kind its own picker uses - the row must say the
        // same thing, so the two can never disagree about what the mapping does.
        var text = MappingDescriptions.Target(new SpecialKeyTarget(kind));

        Assert.Equal(expected, text);
        Assert.DoesNotContain("SpecialKeyTarget", text);
        Assert.DoesNotContain("Kind", text);
    }

    [Fact]
    public void A_Leader_Key_Target_Keeps_Its_Slot_And_Mode_Fields_Out_Of_The_Summary()
    {
        // The slot pair and the switch mode are configured in the editor's own pickers; the
        // summary the row and the editor header share stays the kind alone, so no record
        // field leaks into it however the leader is configured.
        var text = MappingDescriptions.Target(new SpecialKeyTarget(
            SpecialKeyKind.Leader, PrecisionMode.Hold, LeaderSwitchMode.Hold, SlotA: 3, SlotB: 4));

        Assert.Equal("Leader 键", text);
        Assert.DoesNotContain("SlotA", text);
        Assert.DoesNotContain("SlotB", text);
        Assert.DoesNotContain("Hold", text);
    }

    [Fact]
    public void The_Precision_Key_Target_Reads_The_Same_Under_Every_Mode()
    {
        Assert.Equal(
            "精准键",
            MappingDescriptions.Target(new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Hold)));
        Assert.Equal(
            "精准键",
            MappingDescriptions.Target(new SpecialKeyTarget(SpecialKeyKind.Precision, PrecisionMode.Toggle)));
    }

    [Fact]
    public void A_Layer_Leader_Target_Names_The_Layer_It_Switches()
    {
        // A layer leader's row must say it switches the temporary layer: there is exactly
        // one, so the summary names it without a number - the same button does something
        // different on the layer, and which layer is the whole configuration.
        Assert.Equal(
            "临时层 Leader 键",
            MappingDescriptions.Target(
                new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold, Action: LeaderAction.Layer)));
        Assert.DoesNotContain("SpecialKeyTarget", MappingDescriptions.Target(
            new SpecialKeyTarget(SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold, Action: LeaderAction.Layer)));
    }

    [Fact]
    public void A_Deprecated_Layer_Key_Still_Names_The_Kind()
    {
        // The kind the merged leader replaced can still reach a profile through a hand edit;
        // a row must stay readable for it rather than falling back to the record text.
        Assert.Equal("临时层键", MappingDescriptions.Target(new SpecialKeyTarget(SpecialKeyKind.Layer)));
    }

    [Theory]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.C, "复制")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.V, "粘贴")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.X, "剪切")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.Z, "撤销")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.Y, "重做")]
    [InlineData(KeyModifiers.Ctrl | KeyModifiers.Shift, GamepadKey.Z, "重做")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.S, "保存")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.A, "全选")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.F, "查找")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.P, "打印")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.N, "新建")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.O, "打开")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.W, "关闭")]
    [InlineData(KeyModifiers.Alt, GamepadKey.F4, "关闭窗口")]
    [InlineData(KeyModifiers.Alt, GamepadKey.Tab, "切换窗口")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.Tab, "切换标签页")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.B, "加粗")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.I, "斜体")]
    [InlineData(KeyModifiers.Ctrl, GamepadKey.U, "下划线")]
    public void A_Common_Combo_Reads_As_The_Action_Name_Not_The_Key_Combo(
        KeyModifiers modifiers, GamepadKey key, string expected)
    {
        // The layer hint overlay names what a button DOES, not which keys it presses:
        // "复制" answers the question a player has mid-fight, "Ctrl + C" does not.
        Assert.Equal(expected, MappingDescriptions.ActionName(new KeyComboTarget(modifiers, key)));
    }

    [Fact]
    public void An_Unknown_Combo_Falls_Back_To_The_Key_Combo()
    {
        // No application gives Ctrl+Q a universal meaning, so the hint must not invent one:
        // the raw combo is the honest answer.
        Assert.Equal("Ctrl + Q", MappingDescriptions.ActionName(new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.Q)));
    }

    [Fact]
    public void A_Single_Key_Target_Reads_As_The_Key_Itself()
    {
        Assert.Equal("F5", MappingDescriptions.ActionName(new KeyPressTarget(GamepadKey.F5)));
        Assert.Equal("←", MappingDescriptions.ActionName(new KeyPressTarget(GamepadKey.ArrowLeft)));
    }

    [Fact]
    public void A_Mouse_Or_Wheel_Target_Names_The_Device_Part_It_Drives()
    {
        Assert.Equal("鼠标左键", MappingDescriptions.ActionName(new MouseButtonTarget(MouseButton.Left)));
        Assert.Equal("鼠标移动", MappingDescriptions.ActionName(new MouseMoveTarget(new MouseStickSettings())));
        Assert.Equal("滚轮滚动", MappingDescriptions.ActionName(new WheelScrollTarget(new ScrollStickSettings())));
    }

    [Theory]
    [InlineData(GamepadKey.VolumeUp, "音量加")]
    [InlineData(GamepadKey.VolumeDown, "音量减")]
    [InlineData(GamepadKey.VolumeMute, "静音")]
    [InlineData(GamepadKey.MediaPlayPause, "播放/暂停")]
    [InlineData(GamepadKey.MediaStop, "停止播放")]
    [InlineData(GamepadKey.MediaPrevTrack, "上一曲")]
    [InlineData(GamepadKey.MediaNextTrack, "下一曲")]
    [InlineData(GamepadKey.BrowserBack, "浏览器后退")]
    [InlineData(GamepadKey.BrowserForward, "浏览器前进")]
    [InlineData(GamepadKey.BrowserRefresh, "刷新")]
    [InlineData(GamepadKey.BrowserStop, "停止加载")]
    [InlineData(GamepadKey.BrowserHome, "浏览器主页")]
    [InlineData(GamepadKey.BrowserSearch, "浏览器搜索")]
    [InlineData(GamepadKey.BrowserFavorites, "收藏夹")]
    [InlineData(GamepadKey.LaunchMail, "打开邮件")]
    [InlineData(GamepadKey.LaunchApp1, "启动应用1")]
    [InlineData(GamepadKey.LaunchApp2, "启动应用2")]
    [InlineData(GamepadKey.Sleep, "睡眠")]
    [InlineData(GamepadKey.PrintScreen, "截屏")]
    public void A_Media_Browser_Or_System_Key_Reads_As_Chinese_Not_The_Enum_Name(
        GamepadKey key, string expected)
    {
        // The shared table was written before the media keys existed, so its raw form is
        // the enum's own name - VolumeUp, BrowserRefresh - which reads as debug output in
        // a mapping row and in the layer hint overlay. Every surface that names a key goes
        // through here, so the picker, the row and the gesture-action list cannot drift.
        Assert.Equal(expected, MappingDescriptions.Key(key));
        Assert.Equal(expected, MappingDescriptions.Target(new KeyPressTarget(key)));
        Assert.Equal(expected, MappingDescriptions.ActionName(new KeyPressTarget(key)));
    }

    [Fact]
    public void A_Text_Target_Reads_As_The_String_It_Types()
    {
        // A text target is the string it types, so the row names the string: 文本输出：hello
        // says what the press does, where the kind alone would only say which picker built
        // it. The record's own ToString would read as 输入文本「hello」 - a third wording
        // for the same thing, which is exactly what a shared table exists to prevent.
        Assert.Equal("文本输出：hello", MappingDescriptions.Target(new TextTarget("hello")));
        Assert.Equal("文本输出：hello", MappingDescriptions.ActionName(new TextTarget("hello")));
        Assert.DoesNotContain("TextTarget", MappingDescriptions.Target(new TextTarget("hello")));
    }

    [Fact]
    public void An_Empty_Text_Target_Reads_As_The_Kind_Alone()
    {
        // A draft that carries no string yet has nothing to name, so the row says the kind
        // rather than trailing a colon into nothing.
        Assert.Equal("文本输出", MappingDescriptions.Target(new TextTarget(string.Empty)));
    }

    [Fact]
    public void A_Text_Step_Inside_A_Program_Reads_As_The_String_It_Types()
    {
        // The DSL's own text step names the string it types, so a program never shows the
        // step as a record dump.
        Assert.Equal(
            "点按 A；输入文本「hi」",
            MappingDescriptions.ComplexShortcutSteps(ComplexShortcutDsl.Parse("tap A\ntext hi")));
        Assert.DoesNotContain("TextStep", MappingDescriptions.ComplexShortcutSteps(
            ComplexShortcutDsl.Parse("text hi")));
    }
}
