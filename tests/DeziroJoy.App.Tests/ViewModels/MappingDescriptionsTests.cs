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
}
