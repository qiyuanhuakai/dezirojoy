using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Sdl3;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The per-device face-button map: which logical button each of SDL's four face positions is,
/// read from the glyphs SDL reports for the device.
/// <para>
/// The rule under test is that the glyph decides, not the position. A Nintendo pad prints B in
/// the south slot and X in the north slot, so the physical X has to be logical X - mapping the
/// north slot to a fixed Y is what made the panel call a button X while the mapping executed Y.
/// </para>
/// </summary>
public sealed class Sdl3FaceButtonMapTests
{
    private static Sdl3FaceButtonMap Map(params string[] glyphs)
    {
        var info = new Sdl3GamepadInfo
        {
            InstanceId = 1,
            Name = "测试手柄",
            GuidString = "03000000000000000000000000000000",
            Vendor = 0,
            Product = 0,
            ProductVersion = 0,
            Serial = string.Empty,
            Path = string.Empty,
            Type = 0,
            TypeName = "Unknown",
            Mapping = "m",
            MappingSource = "sdl-database",
            PlayerIndex = -1,
            ConnectionState = "Wired",
            TouchpadCount = 0,
            TouchpadFingerCounts = [],
            Sensors = [],
            DeclaredButtons = [],
            DeclaredButtonCount = 0,
            DeclaredAxes = [],
            FaceButtonLabels = glyphs,
        };

        return Sdl3FaceButtonMap.From(info);
    }

    [Fact]
    public void A_Nintendo_Pad_Maps_Each_Slot_To_The_Glyph_Printed_In_It()
    {
        // SDL answers BAYX for a Nintendo device: south=B, east=A, west=Y, north=X.
        var map = Map("B", "A", "Y", "X");

        Assert.Equal(GamepadButton.B, map.South);
        Assert.Equal(GamepadButton.A, map.East);
        Assert.Equal(GamepadButton.Y, map.West);
        Assert.Equal(GamepadButton.X, map.North);
    }

    [Fact]
    public void An_Xbox_Pad_Keeps_The_Positional_Answer()
    {
        var map = Map("A", "B", "X", "Y");

        Assert.Equal(GamepadButton.A, map.South);
        Assert.Equal(GamepadButton.B, map.East);
        Assert.Equal(GamepadButton.X, map.West);
        Assert.Equal(GamepadButton.Y, map.North);
    }

    [Fact]
    public void A_DualShock_Pad_Keeps_The_Positional_Answer_Through_Its_Own_Glyphs()
    {
        var map = Map("Cross", "Circle", "Square", "Triangle");

        Assert.Equal(GamepadButton.A, map.South);
        Assert.Equal(GamepadButton.B, map.East);
        Assert.Equal(GamepadButton.X, map.West);
        Assert.Equal(GamepadButton.Y, map.North);
    }

    [Fact]
    public void A_GameCube_Pad_Swaps_The_East_And_West_Slots_Because_Its_Glyphs_Do()
    {
        // SDL answers AXBY for a GameCube device: A bottom, X right, B left, Y top. The
        // positional table would bind the physical X to logical B.
        var map = Map("A", "X", "B", "Y");

        Assert.Equal(GamepadButton.A, map.South);
        Assert.Equal(GamepadButton.X, map.East);
        Assert.Equal(GamepadButton.B, map.West);
        Assert.Equal(GamepadButton.Y, map.North);
    }

    [Fact]
    public void A_Glyph_Sdl_Cannot_Read_Falls_Back_To_The_Position()
    {
        var map = Map("Unknown", "Unknown", "Unknown", "Unknown");

        Assert.Equal(Sdl3FaceButtonMap.Positional, map);
    }

    [Fact]
    public void A_Missing_Glyph_List_Falls_Back_To_The_Position()
    {
        Assert.Equal(Sdl3FaceButtonMap.Positional, Map());
        Assert.Equal(Sdl3FaceButtonMap.Positional, Map("A"));
    }

    [Fact]
    public void One_Unreadable_Glyph_Costs_That_Slot_Only()
    {
        // SDL reads the face style once per device, so in practice it is all four glyphs or none
        // - a partial set is what a hand-written evidence record looks like. Where the positional
        // answer for the unreadable slot does not collide, it is used and the readable slots keep
        // what they said.
        var map = Map("A", "Unknown", "X", "Y");

        Assert.Equal(GamepadButton.A, map.South);
        // East keeps its positional answer when its glyph is unreadable.
        Assert.Equal(GamepadButton.B, map.East);
        Assert.Equal(GamepadButton.X, map.West);
        Assert.Equal(GamepadButton.Y, map.North);
    }

    [Fact]
    public void A_Partial_Set_Whose_Positional_Fallback_Collides_Falls_Back_Whole()
    {
        // The readable slots are right, but the positional answer for the unreadable north slot
        // is Y - which the readable west slot already claimed. Keeping it would bind two
        // positions to one logical button and leave the other unreachable, so the whole map
        // falls back rather than lose a button.
        var map = Map("B", "A", "Y", "Unknown");

        Assert.Equal(Sdl3FaceButtonMap.Positional, map);
    }

    [Fact]
    public void A_Map_That_Collapses_Two_Slots_Onto_One_Button_Falls_Back_Whole()
    {
        // A device reporting something unusable must not silently lose a button: two positions
        // bound to one logical button would leave the other unbindable, and the positional guess
        // at least keeps all four reachable.
        var map = Map("A", "A", "X", "Y");

        Assert.Equal(Sdl3FaceButtonMap.Positional, map);
    }

    [Fact]
    public void Glyphs_Match_Case_Insensitively_And_Ignore_Surrounding_Space()
    {
        var map = Map(" b ", "A", "y", "X");

        Assert.Equal(GamepadButton.B, map.South);
        Assert.Equal(GamepadButton.Y, map.West);
    }

    [Fact]
    public void A_Glyph_This_Build_Does_Not_Know_Is_Treated_Exactly_Like_An_Unreadable_One()
    {
        // "Question" is not a glyph any layout uses, so the slot takes its positional answer. On
        // this Nintendo-shaped set that collides with a slot which did report - and the whole-map
        // fallback is what keeps all four buttons reachable.
        var map = Map("Question", "A", "Y", "X");

        Assert.Equal(Sdl3FaceButtonMap.Positional, map);
    }

    [Fact]
    public void An_Unrecognized_Glyph_That_Does_Not_Collide_Costs_Only_Its_Own_Slot()
    {
        var map = Map("Question", "B", "X", "Y");

        Assert.Equal(GamepadButton.A, map.South);
        Assert.Equal(GamepadButton.B, map.East);
        Assert.Equal(GamepadButton.X, map.West);
        Assert.Equal(GamepadButton.Y, map.North);
    }

    [Fact]
    public void Every_Non_Face_Button_Keeps_The_Positional_Table()
    {
        var map = Map("B", "A", "Y", "X");

        Assert.True(map.TryLogical(SDL.GamepadButton.Back, out var back));
        Assert.Equal(GamepadButton.Back, back);

        Assert.True(map.TryLogical(SDL.GamepadButton.LeftShoulder, out var shoulder));
        Assert.Equal(GamepadButton.LeftBumper, shoulder);

        Assert.True(map.TryLogical(SDL.GamepadButton.DPadUp, out var dpad));
        Assert.Equal(GamepadButton.DPadUp, dpad);

        Assert.False(map.TryLogical(SDL.GamepadButton.Misc1, out _));
        Assert.False(map.TryLogical(SDL.GamepadButton.Touchpad, out _));
    }

    [Fact]
    public void The_Positional_Table_Is_The_Documented_Fallback()
    {
        var map = Sdl3FaceButtonMap.Positional;

        Assert.Equal(GamepadButton.A, map.South);
        Assert.Equal(GamepadButton.B, map.East);
        Assert.Equal(GamepadButton.X, map.West);
        Assert.Equal(GamepadButton.Y, map.North);
    }

    [Fact]
    public void Building_Requires_The_Devices_Evidence()
        => Assert.Throws<ArgumentNullException>(() => Sdl3FaceButtonMap.From(null!));
}
