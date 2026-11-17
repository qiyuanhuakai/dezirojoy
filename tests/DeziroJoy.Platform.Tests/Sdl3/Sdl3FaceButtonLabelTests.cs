using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Sdl3;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The face-button labels: what SDL reads off the device, how that lines up with the Core's
/// position semantics, and what the panel ends up showing.
/// <para>
/// The whole point of the position abstraction is that one generic code path serves every
/// layout SDL recognizes: SDL's south/east/west/north name the four *positions* in the diamond,
/// and the Core's A/B/X/Y name the same four positions in the Xbox convention. A Switch pad
/// therefore presses logical B when the user presses the physical A, and that is correct - the
/// logical position is what a mapping binds to, and it must not be swapped per brand, or every
/// profile would mean something different on a different pad.
/// </para>
/// <para>
/// What has to be brand-correct is the *label*. SDL reads the glyph actually printed on the
/// device (a Switch Pro is BAYX, a DualSense is SONY, an Xbox pad is ABXY), and the description
/// has to show that glyph next to the logical position. A panel that shows the logical Xbox
/// letter instead is what produces "I press X and Y lights up": on a Switch pad the physical X
/// sits in the north position, which is logical Y, so an Xbox-letter panel lights Y.
/// </para>
/// </summary>
public sealed class Sdl3FaceButtonLabelTests
{
    /// <summary>SDL's own label table, by face style, in south/east/west/north order.</summary>
    /// <remarks>
    /// Transcribed from <c>SDL_GetGamepadButtonLabelForFaceStyle</c>. SDL assigns a Switch pad
    /// <c>BAYX</c> and a PlayStation pad <c>SONY</c>; everything else is <c>ABXY</c>. A
    /// transcription silently rots if SDL changes its table, so
    /// <see cref="The_Switch_Glyph_Table_Matches_What_The_Linked_Sdl_Answers"/> asks the linked
    /// SDL directly, through a virtual joystick mapped as a Switch Pro.
    /// </remarks>
    private static readonly Dictionary<string, string[]> FaceStyles = new(StringComparer.Ordinal)
    {
        ["ABXY"] = ["A", "B", "X", "Y"],
        ["BAYX"] = ["B", "A", "Y", "X"],
        ["SONY"] = ["Cross", "Circle", "Square", "Triangle"],
        ["AXBY"] = ["A", "X", "B", "Y"],
    };

    private static Sdl3GamepadInfo Pad(string typeName, IReadOnlyList<string> faceButtonLabels)
        => new()
        {
            InstanceId = 1,
            Name = "测试手柄",
            GuidString = "030000007e0500000920000000000000",
            Vendor = 0x057e,
            Product = 0x2009,
            ProductVersion = 1,
            Serial = string.Empty,
            Path = string.Empty,
            Type = 7,
            TypeName = typeName,
            Mapping = "test-mapping",
            MappingSource = "sdl-database",
            PlayerIndex = -1,
            ConnectionState = "Wireless",
            TouchpadCount = 0,
            TouchpadFingerCounts = [],
            Sensors = [],
            DeclaredButtons = ["a", "b", "x", "y"],
            DeclaredButtonCount = 4,
            DeclaredAxes = [],
            FaceButtonLabels = faceButtonLabels,
        };

    /// <summary>The label the description gives one logical position.</summary>
    private static string LabelFor(IReadOnlyList<string> faceButtonLabels, GamepadButton logical)
        => Sdl3ControlSet.Build(Pad("NintendoSwitchPro", faceButtonLabels))
            .Single(control => control.Source is ButtonBinding { Button: var button } && button == logical)
            .Label;

    [Fact]
    public void A_Switch_Pad_Labels_Each_Position_With_The_Glyph_Printed_On_It()
    {
        // SDL answers BAYX for a Nintendo Switch device, so in south/east/west/north order the
        // glyphs are B (bottom), A (right), Y (left), X (top).
        var labels = FaceStyles["BAYX"];

        // The logical position is the Xbox convention; the label is the device's own glyph.
        Assert.Equal("B", LabelFor(labels, GamepadButton.A));
        Assert.Equal("A", LabelFor(labels, GamepadButton.B));
        Assert.Equal("Y", LabelFor(labels, GamepadButton.X));
        Assert.Equal("X", LabelFor(labels, GamepadButton.Y));
    }

    [Fact]
    public void Pressing_A_Physical_Button_Lights_The_Control_Named_After_It()
    {
        // The user-visible rule, end to end: press the button with a glyph printed on it, and
        // the control that lights is the one named after that glyph. This is the whole
        // "I press X and Y lights up" report, expressed as an assertion.
        var labels = FaceStyles["BAYX"];

        foreach (var (sdlButton, glyph) in new[]
                 {
                     (SDL.GamepadButton.South, "B"),
                     (SDL.GamepadButton.East, "A"),
                     (SDL.GamepadButton.West, "Y"),
                     (SDL.GamepadButton.North, "X"),
                 })
        {
            Assert.True(Sdl3ButtonMap.TryToLogical(sdlButton, out var logical));
            Assert.Equal(glyph, LabelFor(labels, logical));
        }
    }

    [Fact]
    public void An_Xbox_Pad_Keeps_Its_Own_Glyphs_And_Is_Unaffected()
    {
        var labels = FaceStyles["ABXY"];

        Assert.Equal("A", LabelFor(labels, GamepadButton.A));
        Assert.Equal("B", LabelFor(labels, GamepadButton.B));
        Assert.Equal("X", LabelFor(labels, GamepadButton.X));
        Assert.Equal("Y", LabelFor(labels, GamepadButton.Y));
    }

    [Fact]
    public void A_DualShock_Pad_Shows_The_Sony_Glyphs_On_The_Same_Positions()
    {
        var labels = FaceStyles["SONY"];

        Assert.Equal("Cross", LabelFor(labels, GamepadButton.A));
        Assert.Equal("Circle", LabelFor(labels, GamepadButton.B));
        Assert.Equal("Square", LabelFor(labels, GamepadButton.X));
        Assert.Equal("Triangle", LabelFor(labels, GamepadButton.Y));
    }

    [Fact]
    public void The_Core_Position_Semantics_Are_Not_Swapped_Per_Brand()
    {
        // South/East/West/North are positions; A/B/X/Y are the same positions in the Xbox
        // convention. Swapping these per brand would make one profile mean something different
        // on a different pad, which is exactly the failure this table exists to prevent.
        Assert.Equal(GamepadButton.A, Logical(SDL.GamepadButton.South));
        Assert.Equal(GamepadButton.B, Logical(SDL.GamepadButton.East));
        Assert.Equal(GamepadButton.X, Logical(SDL.GamepadButton.West));
        Assert.Equal(GamepadButton.Y, Logical(SDL.GamepadButton.North));
    }

    [Fact]
    public void The_Switch_Glyph_Table_Matches_What_The_Linked_Sdl_Answers()
    {
        // The face-style table above is a transcription of SDL's own. Rather than trust it, ask
        // the linked SDL: a mapping may carry "type:", which is what decides a device's face
        // style, so a virtual joystick mapped as a Switch Pro answers with SDL's own glyphs.
        if (!Sdl3JoystickEndToEndTests.RuntimeAvailableForTest())
        {
            return;
        }

        const ushort vendorId = 0x2a10;
        var descriptor = new SDL.VirtualJoystickDesc
        {
            Version = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SDL.VirtualJoystickDesc>(),
            Type = SDL.JoystickType.Unknown,
            VendorID = vendorId,
            ProductID = 0x000a,
            NAxes = 4,
            NButtons = 12,
            NBalls = 0,
            NHats = 1,
            ButtonMask = 0,
            AxisMask = 0,
        };

        var instanceId = SDL.AttachVirtualJoystick(in descriptor);
        Assert.NotEqual(0u, instanceId);

        try
        {
            Span<byte> buffer = stackalloc byte[33];
            SDL.GUIDToString(SDL.GetJoystickGUIDForID(instanceId), buffer, buffer.Length);
            var guid = System.Text.Encoding.UTF8.GetString(buffer[..32]);

            var mapping = string.Join(',',
                guid,
                "Switch Label Probe",
                "type:switchpro",
                "platform:Linux",
                "a:b0", "b:b1", "x:b2", "y:b3",
                "leftx:a0", "lefty:a1", "rightx:a2", "righty:a3",
                "lefttrigger:+a4", "righttrigger:+a5",
                "start:b7", "back:b6", "guide:b8",
                "leftshoulder:b4", "rightshoulder:b5",
                "leftstick:b9", "rightstick:b10",
                "dpup:h0.1", "dpdown:h0.4", "dpleft:h0.8", "dpright:h0.2");

            Assert.True(SDL.AddGamepadMapping(mapping) >= 0);

            var gamepad = SDL.OpenGamepad(instanceId);
            Assert.NotEqual(nint.Zero, gamepad);

            try
            {
                Assert.Equal(SDL.GamepadType.NintendoSwitchPro, SDL.GetGamepadType(gamepad));

                var answered = Enum.GetValues<SDL.GamepadButton>()
                    .Take(4)
                    .Select(button => SDL.GetGamepadButtonLabel(gamepad, button).ToString())
                    .ToArray();

                Assert.Equal(FaceStyles["BAYX"], answered);
            }
            finally
            {
                SDL.CloseGamepad(gamepad);
            }
        }
        finally
        {
            SDL.DetachVirtualJoystick(instanceId);
        }
    }

    [Fact]
    public void A_Face_Button_The_State_Reports_Is_The_One_The_Description_Labels()
    {
        // The description and the state have to agree, or a control can light up while the
        // mapping bound to a different one fires.
        var labels = FaceStyles["BAYX"];
        var tracker = new Sdl3GamepadStateTracker();

        tracker.ApplyButton(SDL.GamepadButton.North, true);

        var state = tracker.Snapshot();
        var logical = Assert.Single(
            new[] { GamepadButton.A, GamepadButton.B, GamepadButton.X, GamepadButton.Y },
            button => state.PressedButtons.Has(button));

        // The physical X on a Switch pad is the north position, which is logical Y, and the
        // control named "X" is the one bound to logical Y.
        Assert.Equal(GamepadButton.Y, logical);
        Assert.Equal("X", LabelFor(labels, logical));
    }

    [Fact]
    public void A_Glyph_Sdl_Cannot_Read_Falls_Back_To_The_Logical_Position_Not_A_Guessed_Brand()
    {
        // SDL answers "Unknown" for most third-party pads. Naming the logical position is the
        // one honest answer left: it is a position, not a claim about what is printed there.
        var labels = new[] { "Unknown", "Unknown", "Unknown", "Unknown" };

        Assert.Equal("A", LabelFor(labels, GamepadButton.A));
        Assert.Equal("B", LabelFor(labels, GamepadButton.B));
        Assert.Equal("X", LabelFor(labels, GamepadButton.X));
        Assert.Equal("Y", LabelFor(labels, GamepadButton.Y));
    }

    [Fact]
    public void An_Empty_Glyph_List_Falls_Back_The_Same_Way()
    {
        Assert.Equal("A", LabelFor([], GamepadButton.A));
        Assert.Equal("Y", LabelFor([], GamepadButton.Y));
    }

    [Fact]
    public void Only_The_Face_Buttons_Read_A_Glyph()
    {
        // SDL answers a label for the four face buttons only; everything else keeps its logical
        // name, which is a position rather than a brand claim.
        var controls = Sdl3ControlSet.Build(Pad("NintendoSwitchPro", FaceStyles["BAYX"]));

        Assert.All(
            controls.Where(control => control.Label is not ("A" or "B" or "X" or "Y")),
            control => Assert.NotEqual("Unknown", control.Label));
    }

    private static GamepadButton Logical(SDL.GamepadButton button)
    {
        Assert.True(Sdl3ButtonMap.TryToLogical(button, out var logical));
        return logical;
    }
}
