using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Input;

/// <summary>
/// The generic control description: the fixed zone arrangement, the fallback labelling, and the
/// rule that a description is a declaration rather than an observation. Deterministic by
/// construction - the builder takes a declaration and nothing else, so no platform and no device
/// is involved.
/// </summary>
public class GamepadControlSetTests
{
    private static GamepadControlDeclaration Declaration(
        GamepadButtonMask buttons,
        IReadOnlyList<GamepadStick>? sticks = null,
        IReadOnlyList<int>? extras = null,
        IReadOnlyDictionary<GamepadButton, string>? labels = null)
        => new()
        {
            Buttons = buttons,
            Sticks = sticks ?? [],
            ExtraButtons = extras ?? [],
            Labels = labels ?? new Dictionary<GamepadButton, string>(),
        };

    /// <summary>Every standard logical button a full pad declares.</summary>
    private const GamepadButtonMask FullMask = GamepadButtonMask.A | GamepadButtonMask.B
        | GamepadButtonMask.X | GamepadButtonMask.Y
        | GamepadButtonMask.LeftBumper | GamepadButtonMask.RightBumper
        | GamepadButtonMask.LeftTrigger | GamepadButtonMask.RightTrigger
        | GamepadButtonMask.Back | GamepadButtonMask.Start | GamepadButtonMask.Guide
        | GamepadButtonMask.LeftStick | GamepadButtonMask.RightStick
        | GamepadButtonMask.DPadUp | GamepadButtonMask.DPadDown
        | GamepadButtonMask.DPadLeft | GamepadButtonMask.DPadRight;

    private static IReadOnlyList<GamepadControlDescriptor> Full()
        => GamepadControlSet.Build(Declaration(FullMask, [GamepadStick.Left, GamepadStick.Right]));

    [Fact]
    public void A_Full_Declaration_Produces_Every_Standard_Control_In_The_Fixed_Order()
    {
        var controls = Full();

        Assert.Equal(
            [
                "左摇杆", "右摇杆",
                "十字键 上", "十字键 下", "十字键 左", "十字键 右",
                "A", "B", "X", "Y",
                "左肩键", "右肩键", "左扳机", "右扳机",
                "Back / Select", "Start / Options", "Guide / Home", "左摇杆按下", "右摇杆按下",
            ],
            controls.Select(control => control.Label));
    }

    /// <summary>The full declaration plus the positionless cluster, so every zone is occupied.</summary>
    private static IReadOnlyList<GamepadControlDescriptor> FullWithExtras()
        => GamepadControlSet.Build(Declaration(FullMask, [GamepadStick.Left, GamepadStick.Right], [15, 20]));

    [Fact]
    public void Zones_Appear_In_The_Fixed_Order_And_Keep_Their_Order_Inside_A_Zone()
    {
        var controls = FullWithExtras();
        var zones = controls.Select(control => control.Zone).Distinct().ToArray();

        Assert.Equal(GamepadControlSet.ZoneOrder, zones);

        // Grouping by zone and rendering the zones in order reproduces the build order, so a
        // panel needs no second ordering rule to keep in step.
        var grouped = controls
            .GroupBy(control => control.Zone)
            .OrderBy(group => Array.IndexOf(GamepadControlSet.ZoneOrder.ToArray(), group.Key))
            .SelectMany(group => group)
            .ToArray();

        Assert.Equal(controls, grouped);
    }

    [Fact]
    public void Extensions_Come_Last_And_Are_The_Only_Zone_For_Positionless_Controls()
    {
        var controls = FullWithExtras();

        Assert.All(
            controls.Where(control => control.Zone == GamepadControlZone.Extensions),
            control => Assert.Equal(GamepadControlKind.Other, control.Kind));
        Assert.Equal(GamepadControlZone.Extensions, controls[^1].Zone);
        Assert.DoesNotContain(
            controls.Where(control => control.Zone != GamepadControlZone.Extensions),
            control => control.Kind == GamepadControlKind.Other);
    }

    [Fact]
    public void Every_Zone_Is_Listed_Exactly_Once_In_The_Order()
        => Assert.Equal(
            [
                GamepadControlZone.Directions,
                GamepadControlZone.MainButtons,
                GamepadControlZone.Shoulders,
                GamepadControlZone.Functions,
                GamepadControlZone.Extensions,
            ],
            GamepadControlSet.ZoneOrder);

    [Fact]
    public void Sticks_Are_Directions_And_Triggers_Are_Single_Axes()
    {
        var controls = Full();

        Assert.All(
            controls.Where(control => control.Label is "左摇杆" or "右摇杆"),
            control => Assert.Equal(GamepadControlKind.Direction, control.Kind));
        Assert.All(
            controls.Where(control => control.Label is "左扳机" or "右扳机"),
            control => Assert.Equal(GamepadControlKind.Axis, control.Kind));
        Assert.All(
            controls.Where(control => control.Label is "左肩键" or "右肩键" or "A" or "十字键 上"),
            control => Assert.Equal(GamepadControlKind.Button, control.Kind));
    }

    [Fact]
    public void A_Stick_Is_One_Two_Axis_Control_Not_Two_Single_Axes()
    {
        var controls = Full();

        var left = controls.Where(control => control.Source is StickBinding { Stick: GamepadStick.Left }).ToList();

        var stick = Assert.Single(left);
        Assert.Equal(GamepadControlKind.Direction, stick.Kind);
        Assert.Equal(GamepadControlZone.Directions, stick.Zone);
    }

    [Fact]
    public void Only_Declared_Controls_Are_Described()
    {
        // A two-bumper remote: the panel still shows the two controls it has, in the regions
        // they belong to, and nothing invented for the ones it does not.
        var controls = GamepadControlSet.Build(
            Declaration(GamepadButtonMask.LeftBumper | GamepadButtonMask.RightBumper));

        Assert.Equal(2, controls.Count);
        Assert.All(controls, control => Assert.Equal(GamepadControlZone.Shoulders, control.Zone));
        Assert.Equal(["左肩键", "右肩键"], controls.Select(control => control.Label));
    }

    [Fact]
    public void A_Lone_Stick_Axis_Is_Not_A_Stick()
    {
        // Both axes have to be declared for a two-axis editor to make sense; the provider is
        // what decides that, and this pins the builder's half of the rule.
        var controls = GamepadControlSet.Build(Declaration(GamepadButtonMask.None));

        Assert.Empty(controls);
    }

    [Fact]
    public void The_Label_Falls_Back_To_The_Logical_Name_When_The_Platform_Supplies_None()
    {
        var controls = Full();

        Assert.Equal("A", controls.Single(control => control.Label == "A").Label);
        Assert.Equal("十字键 上", controls.Single(control => control.Label == "十字键 上").Label);
    }

    [Fact]
    public void The_Label_Comes_From_The_Platform_When_It_Supplies_One()
    {
        var buttons = GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.X | GamepadButtonMask.Y;
        var labels = new Dictionary<GamepadButton, string>
        {
            [GamepadButton.A] = "Cross",
            [GamepadButton.B] = "Circle",
            [GamepadButton.X] = "Square",
            [GamepadButton.Y] = "Triangle",
        };

        var controls = GamepadControlSet.Build(Declaration(buttons, labels: labels));

        Assert.Equal(["Cross", "Circle", "Square", "Triangle"], controls.Select(control => control.Label));
    }

    [Fact]
    public void A_Blank_Platform_Label_Falls_Back_Rather_Than_Rendering_An_Empty_Control()
    {
        var controls = GamepadControlSet.Build(
            Declaration(
                GamepadButtonMask.A,
                labels: new Dictionary<GamepadButton, string> { [GamepadButton.A] = "   " }));

        Assert.Equal("A", Assert.Single(controls).Label);
    }

    [Fact]
    public void The_Same_Declaration_Describes_The_Same_Controls_Whatever_The_Device_Is_Called()
    {
        // The arrangement is a function of the declaration alone: no name, vendor id or model
        // lookup feeds it, which is what lets one panel serve a pad nobody has seen.
        var first = Full();
        var second = Full();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Two_Brands_Agree_On_The_Arrangement_And_Differ_Only_In_The_Face_Labels()
    {
        var buttons = GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.X | GamepadButtonMask.Y
            | GamepadButtonMask.LeftBumper;

        var xbox = GamepadControlSet.Build(Declaration(
            buttons,
            labels: new Dictionary<GamepadButton, string>
            {
                [GamepadButton.A] = "A", [GamepadButton.B] = "B",
                [GamepadButton.X] = "X", [GamepadButton.Y] = "Y",
            }));

        var dualShock = GamepadControlSet.Build(Declaration(
            buttons,
            labels: new Dictionary<GamepadButton, string>
            {
                [GamepadButton.A] = "Cross", [GamepadButton.B] = "Circle",
                [GamepadButton.X] = "Square", [GamepadButton.Y] = "Triangle",
            }));

        Assert.Equal(
            xbox.Select(control => (control.Kind, control.Zone)),
            dualShock.Select(control => (control.Kind, control.Zone)));
        Assert.NotEqual(
            xbox.Select(control => control.Label),
            dualShock.Select(control => control.Label));
    }

    [Fact]
    public void Extras_Are_Described_In_Ascending_Code_Order_Without_Duplicates()
    {
        var controls = GamepadControlSet.Build(
            Declaration(GamepadButtonMask.None, extras: [20, 15, 15, 17]));

        Assert.Equal(
            ["按钮 16", "按钮 18", "按钮 21"],
            controls.Select(control => control.Label));
        Assert.All(controls, control =>
        {
            Assert.Equal(GamepadControlKind.Other, control.Kind);
            Assert.Equal(GamepadControlZone.Extensions, control.Zone);
        });
    }

    [Fact]
    public void An_Extra_Is_Addressed_By_Its_Raw_Code_The_Same_Way_The_State_Reports_It()
    {
        var controls = GamepadControlSet.Build(Declaration(GamepadButtonMask.None, extras: [15]));

        var control = Assert.Single(controls);
        var source = Assert.IsType<ButtonBinding>(control.Source);
        Assert.Equal(GamepadButton.Extra, source.Button);
        Assert.Equal(15, source.Code);
    }

    [Fact]
    public void A_Stick_Is_Addressed_By_The_Stick_The_State_Reports()
    {
        var controls = Full();

        var right = controls.Single(control => control.Source is StickBinding { Stick: GamepadStick.Right });

        Assert.IsType<StickBinding>(right.Source);
        Assert.Equal(GamepadStick.Right, ((StickBinding)right.Source).Stick);
    }

    [Fact]
    public void The_Generic_Fallback_Numbers_From_The_Raw_Control()
    {
        // The number a user reads is the number the mapping carries plus one, so "按钮 8" and
        // "b7" are recognizably the same control.
        Assert.Equal("按钮 1", GamepadControlLabels.Generic(0));
        Assert.Equal("按钮 8", GamepadControlLabels.Generic(7));
        Assert.Equal("按钮 16", GamepadControlLabels.Generic(15));
    }

    [Fact]
    public void Every_Label_Is_Non_Empty()
    {
        Assert.All(Full(), control => Assert.False(string.IsNullOrWhiteSpace(control.Label)));
        Assert.All(
            Enum.GetValues<GamepadButton>().Where(button => button != GamepadButton.Extra),
            button => Assert.False(string.IsNullOrWhiteSpace(GamepadControlLabels.Button(button))));
    }

    [Fact]
    public void A_Description_Carries_No_Observed_State()
    {
        // The descriptor has exactly the four description components: what it binds to, what it
        // is called, what kind it is and where it belongs. No pressed flag, no "seen" flag, no
        // live value - those live in IGamepadState, and a control nobody has touched yet must
        // stay described and configurable rather than vanish.
        var components = typeof(GamepadControlDescriptor).GetConstructors().Single().GetParameters();

        Assert.Equal(
            ["Source", "Label", "Kind", "Zone"],
            components.Select(component => component.Name));
        Assert.DoesNotContain(components, component =>
            component.ParameterType.Name.Contains("press", StringComparison.OrdinalIgnoreCase)
            || component.ParameterType.Name.Contains("detect", StringComparison.OrdinalIgnoreCase)
            || component.ParameterType.Name.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_Declaration_With_Nothing_Declared_Describes_Nothing()
        => Assert.Empty(GamepadControlSet.Build(Declaration(GamepadButtonMask.None)));

    [Fact]
    public void Building_Requires_A_Declaration()
        => Assert.Throws<ArgumentNullException>(() => GamepadControlSet.Build(null!));
}
