using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Sdl3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The SDL half of the control description: what SDL declares becomes what the panel shows, in
/// the Core's fixed arrangement, with SDL's own glyphs where it can read them and the generic
/// fallback where it cannot. Deterministic by construction - the declaration is scripted data,
/// so no device and no native call is involved.
/// </summary>
public sealed class Sdl3ControlSetTests
{
    private static Sdl3GamepadInfo Info(
        string name = "测试手柄",
        IReadOnlyList<string>? buttons = null,
        IReadOnlyList<string>? axes = null,
        IReadOnlyList<string>? faceButtonLabels = null)
        => new()
        {
            InstanceId = 1,
            Name = name,
            GuidString = "03000000000000000000000000000000",
            Vendor = 0x045e,
            Product = 0x0b20,
            ProductVersion = 0x0500,
            Serial = string.Empty,
            Path = string.Empty,
            Type = 3,
            TypeName = "XboxOne",
            Mapping = "test-mapping",
            MappingSource = "sdl-database",
            PlayerIndex = -1,
            ConnectionState = "Wireless",
            TouchpadCount = 0,
            TouchpadFingerCounts = [],
            Sensors = [],
            DeclaredButtons = buttons ?? FakeSdl3Api.DefaultButtonKeys,
            DeclaredButtonCount = 15,
            DeclaredAxes = axes ?? FakeSdl3Api.StandardAxisKeys,
            FaceButtonLabels = faceButtonLabels ?? ["A", "B", "X", "Y"],
        };

    private static IReadOnlyList<GamepadControlDescriptor> Build(Sdl3GamepadInfo info)
        => Sdl3ControlSet.Build(info);

    [Fact]
    public void A_Full_Declaration_Describes_Every_Standard_Control_In_The_Fixed_Order()
    {
        var controls = Build(Info(buttons: FakeSdl3Api.AllStandardButtonKeys));

        Assert.Equal(
            [
                "左摇杆", "右摇杆",
                "十字键 上", "十字键 下", "十字键 左", "十字键 右",
                "A", "B", "X", "Y",
                "左肩键", "右肩键", "左扳机", "右扳机",
                "Back / Select", "Start / Options", "Guide / Home", "左摇杆按下", "右摇杆按下",
                "按钮 16", "按钮 17", "按钮 18", "按钮 19", "按钮 20", "按钮 21",
                "按钮 22", "按钮 23", "按钮 24", "按钮 25", "按钮 26",
            ],
            controls.Select(control => control.Label));
    }

    [Fact]
    public void Zones_Come_In_The_Fixed_Order_For_An_Sdl_Device_Too()
    {
        var controls = Build(Info(buttons: FakeSdl3Api.AllStandardButtonKeys));

        Assert.Equal(
            [
                GamepadControlZone.Directions,
                GamepadControlZone.MainButtons,
                GamepadControlZone.Shoulders,
                GamepadControlZone.Functions,
                GamepadControlZone.Extensions,
            ],
            controls.Select(control => control.Zone).Distinct());
    }

    [Fact]
    public void Triggers_Are_Described_From_The_Declared_Axes_Not_The_Declared_Buttons()
    {
        // SDL has no trigger *button*: a trigger exists because its axis is declared. A mapping
        // that declares only the axes must still describe the triggers.
        var controls = Build(Info(buttons: [], axes: ["lefttrigger", "righttrigger"]));

        Assert.Equal(
            ["左扳机", "右扳机"],
            controls.Select(control => control.Label));
        Assert.All(controls, control => Assert.Equal(GamepadControlKind.Axis, control.Kind));
        Assert.All(controls, control => Assert.Equal(GamepadControlZone.Shoulders, control.Zone));
    }

    [Fact]
    public void A_Stick_Needs_Both_Of_Its_Axes_Declared()
    {
        Assert.Empty(Build(Info(buttons: [], axes: ["leftx"])));
        Assert.Empty(Build(Info(buttons: [], axes: ["lefty"])));

        var both = Build(Info(buttons: [], axes: ["leftx", "lefty"]));

        var stick = Assert.Single(both);
        Assert.Equal("左摇杆", stick.Label);
        Assert.Equal(GamepadControlKind.Direction, stick.Kind);
    }

    [Fact]
    public void The_Face_Buttons_Use_Sdl_Glyphs_When_Sdl_Can_Read_Them()
    {
        var controls = Build(Info(faceButtonLabels: ["Cross", "Circle", "Square", "Triangle"]));

        Assert.Equal(
            ["Cross", "Circle", "Square", "Triangle"],
            controls.Where(control => control.Zone == GamepadControlZone.MainButtons)
                .Select(control => control.Label));
    }

    [Fact]
    public void The_Face_Buttons_Fall_Back_To_A_Generic_Number_When_Sdl_Cannot_Read_A_Glyph()
    {
        // SDL answers "Unknown" for most third-party pads. The roadmap forbids guessing the
        // brand's glyphs, so the control is named by its own ordinal instead - and the number
        // is the one a mapping string carries plus one.
        var controls = Build(Info(faceButtonLabels: ["Unknown", "Unknown", "Unknown", "Unknown"]));

        Assert.Equal(
            ["按钮 1", "按钮 2", "按钮 3", "按钮 4"],
            controls.Where(control => control.Zone == GamepadControlZone.MainButtons)
                .Select(control => control.Label));
    }

    [Fact]
    public void A_Missing_Glyph_List_Falls_Back_The_Same_Way()
    {
        var controls = Build(Info(faceButtonLabels: []));

        Assert.Equal(
            ["按钮 1", "按钮 2", "按钮 3", "按钮 4"],
            controls.Where(control => control.Zone == GamepadControlZone.MainButtons)
                .Select(control => control.Label));
    }

    [Fact]
    public void Non_Face_Buttons_Are_Named_Logically_Even_When_Sdl_Has_No_Glyph_For_Them()
    {
        // SDL only answers a label for the four face buttons; everything else keeps its logical
        // name, which is a position, not a brand claim.
        var controls = Build(Info(faceButtonLabels: ["Unknown", "Unknown", "Unknown", "Unknown"]));

        Assert.Contains(controls, control => control.Label == "Back / Select");
        Assert.Contains(controls, control => control.Label == "Guide / Home");
        Assert.Contains(controls, control => control.Label == "左肩键");
    }

    [Fact]
    public void Positionless_Controls_Are_Described_By_The_Raw_Code_The_State_Reports()
    {
        var controls = Build(Info(buttons: ["misc1", "paddle1", "touchpad"]));

        var extensions = controls.Where(control => control.Zone == GamepadControlZone.Extensions).ToList();

        Assert.Equal(3, extensions.Count);
        Assert.All(extensions, control => Assert.Equal(GamepadControlKind.Other, control.Kind));

        var codes = extensions
            .Select(control => Assert.IsType<ButtonBinding>(control.Source).Code)
            .ToArray();

        // The same numbers Sdl3GamepadStateTracker puts into IGamepadState.ExtraButtons, so a
        // described control is always the one a mapping binds to. SDL's paddle numbering is not
        // the physical side: "paddle1" is RightPaddle1 (16) and "paddle2" is LeftPaddle1 (17).
        Assert.Equal([15, 16, 20], codes);
        Assert.Equal(["按钮 16", "按钮 17", "按钮 21"], extensions.Select(control => control.Label));
    }

    [Fact]
    public void A_Positionless_Control_The_State_Reports_Is_Always_Described()
    {
        // Every positionless SDL button the tracker can report as an extra is describable.
        var controls = Build(Info(buttons: FakeSdl3Api.AllStandardButtonKeys));

        var described = controls
            .Where(control => control.Zone == GamepadControlZone.Extensions)
            .Select(control => Assert.IsType<ButtonBinding>(control.Source).Code)
            .ToHashSet();

        foreach (var button in Enum.GetValues<SDL3.SDL.GamepadButton>())
        {
            if (button is SDL3.SDL.GamepadButton.Invalid or SDL3.SDL.GamepadButton.Count)
            {
                continue;
            }

            if (Sdl3ButtonMap.TryToLogical(button, out _))
            {
                continue;
            }

            Assert.Contains((int)button, described);
        }
    }

    [Fact]
    public void An_Empty_Declaration_Describes_Nothing_Rather_Than_Inventing_Controls()
        => Assert.Empty(Build(Info(buttons: [], axes: [])));

    [Fact]
    public void Only_The_Declared_Controls_Are_Described()
    {
        var controls = Build(Info(buttons: ["a", "leftshoulder"], axes: []));

        // Directions first, then the face diamond, then the shoulders - a two-control device
        // still lands in the regions it belongs to.
        Assert.Equal(["A", "左肩键"], controls.Select(control => control.Label));
    }

    [Fact]
    public void The_Description_Is_A_Function_Of_The_Declaration_Not_The_Device_Name()
    {
        var xbox = Build(Info("Xbox Wireless Controller"));
        var unknown = Build(Info("Unknown Device"));

        Assert.Equal(xbox, unknown);
    }

    [Fact]
    public void A_Stick_And_Its_Click_Are_Separate_Declarations()
    {
        // The stick comes from its two axes, the click from the "leftstick" button. Conflating
        // them would describe a click on every pad that merely has a stick.
        var axesOnly = Build(Info(buttons: [], axes: ["leftx", "lefty"]));

        Assert.Equal(["左摇杆"], axesOnly.Select(control => control.Label));

        var clickOnly = Build(Info(buttons: ["leftstick"], axes: []));

        Assert.Equal(["左摇杆按下"], clickOnly.Select(control => control.Label));
        Assert.Equal(GamepadControlZone.Functions, clickOnly[0].Zone);
    }

    [Fact]
    public void A_Pad_With_A_Stick_But_No_Click_Describes_The_Stick_Anyway()
    {
        var controls = Build(Info(buttons: [], axes: FakeSdl3Api.StandardAxisKeys));

        Assert.Equal(["左摇杆", "右摇杆", "左扳机", "右扳机"], controls.Select(control => control.Label));
    }

    [Fact]
    public void Every_Key_A_Captured_Mapping_Uses_Is_One_Sdl_Can_Declare()
    {
        // DInput mapping-supplement compatibility: the capture flow writes mappings with these
        // keys, and a device SDL accepts such a mapping for reaches this code through the same
        // vocabulary - so a hand-mapped pad describes correctly the moment it becomes a gamepad.
        foreach (var step in Sdl3MappingStep.CreateDefault())
        {
            var isButton = Sdl3ControlNames.TryButton(step.Key, out _);
            var isAxis = Sdl3ControlNames.TryAxis(step.Key, out _);

            Assert.True(isButton || isAxis, $"映射键「{step.Key}」不是 SDL 能声明的标准控件");
        }
    }

    [Fact]
    public void Control_Names_Round_Trip_Through_The_Mapping_Keys()
    {
        foreach (var button in Enum.GetValues<SDL3.SDL.GamepadButton>())
        {
            if (button is SDL3.SDL.GamepadButton.Invalid or SDL3.SDL.GamepadButton.Count)
            {
                continue;
            }

            var key = Sdl3ControlNames.Key(button);

            Assert.True(Sdl3ControlNames.TryButton(key, out var parsed), $"按钮「{key}」无法解析");
            Assert.Equal(button, parsed);
        }

        foreach (var axis in Enum.GetValues<SDL3.SDL.GamepadAxis>())
        {
            if (axis is SDL3.SDL.GamepadAxis.Invalid or SDL3.SDL.GamepadAxis.Count)
            {
                continue;
            }

            var key = Sdl3ControlNames.Key(axis);

            Assert.True(Sdl3ControlNames.TryAxis(key, out var parsed), $"轴「{key}」无法解析");
            Assert.Equal(axis, parsed);
        }
    }

    [Fact]
    public void The_Key_Vocabulary_Matches_The_Mapping_Strings_Sdl_Accepts()
    {
        // SDL spells the face buttons a/b/x/y in a mapping string; the enum's South/East/West/
        // North are not accepted keys and would be silently dropped.
        Assert.Equal("a", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.South));
        Assert.Equal("b", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.East));
        Assert.Equal("x", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.West));
        Assert.Equal("y", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.North));
        Assert.Equal("dpup", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.DPadUp));
        Assert.Equal("paddle1", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.RightPaddle1));
        Assert.Equal("paddle2", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.LeftPaddle1));
        Assert.Equal("touchpad", Sdl3ControlNames.Key(SDL3.SDL.GamepadButton.Touchpad));
        Assert.Equal("leftx", Sdl3ControlNames.Key(SDL3.SDL.GamepadAxis.LeftX));
        Assert.Equal("lefttrigger", Sdl3ControlNames.Key(SDL3.SDL.GamepadAxis.LeftTrigger));
    }

}
