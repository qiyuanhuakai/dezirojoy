using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Sdl3;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// The end-to-end face-button semantic: the glyph printed on a button decides which logical
/// input it is, and the description, the state and the mapping all agree about it.
/// <para>
/// The reported bug was "the panel shows X but the mapping executes Y". On a Nintendo pad the
/// physical X sits in SDL's north slot, and a positional table made that slot logical Y - so the
/// control labelled X was bound to <see cref="ButtonBinding"/> of Y, and pressing X ran whatever
/// was mapped to Y. Fixing only the label would have left that in place, so these tests pin the
/// whole chain: glyph → logical button → descriptor source → state.
/// </para>
/// </summary>
[Collection("DeziroJoy.SdlNative")]
public sealed class Sdl3FaceButtonSemanticTests
{
    private static Sdl3GamepadInfo Pad(IReadOnlyList<string> faceButtonLabels)
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
            TypeName = "NintendoSwitchPro",
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

    /// <summary>The label the description gives one logical button.</summary>
    private static string LabelFor(IReadOnlyList<string> glyphs, GamepadButton logical)
        => Sdl3ControlSet.Build(Pad(glyphs))
            .Single(control => control.Source is ButtonBinding { Button: var button } && button == logical)
            .Label;

    /// <summary>The logical button the state reports when one SDL face position is held.</summary>
    private static GamepadButton? Pressed(Sdl3FaceButtonMap map, SDL.GamepadButton position)
    {
        var tracker = new Sdl3GamepadStateTracker(map);
        tracker.ApplyButton(position, true);
        return new[] { GamepadButton.A, GamepadButton.B, GamepadButton.X, GamepadButton.Y }
            .FirstOrDefault(button => tracker.Snapshot().PressedButtons.Has(button));
    }

    [Fact]
    public void A_Nintendo_Pad_Executes_The_Button_Whose_Glyph_Is_Printed_On_It()
    {
        // SDL answers BAYX for a Nintendo device: the south slot is printed B and the north slot
        // is printed X.
        var map = Sdl3FaceButtonMap.From(Pad(["B", "A", "Y", "X"]));

        Assert.Equal(GamepadButton.B, Pressed(map, SDL.GamepadButton.South));
        Assert.Equal(GamepadButton.A, Pressed(map, SDL.GamepadButton.East));
        Assert.Equal(GamepadButton.Y, Pressed(map, SDL.GamepadButton.West));
        Assert.Equal(GamepadButton.X, Pressed(map, SDL.GamepadButton.North));
    }

    [Fact]
    public void Pressing_The_Physical_X_Lights_The_Control_Labelled_X()
    {
        // The report, as an assertion: press the button with X printed on it, and the control
        // that lights is the one named X - which is bound to logical X, so the mapping that runs
        // is the one bound to X.
        var glyphs = new[] { "B", "A", "Y", "X" };
        var map = Sdl3FaceButtonMap.From(Pad(glyphs));

        var logical = Pressed(map, SDL.GamepadButton.North);
        Assert.Equal(GamepadButton.X, logical);
        Assert.Equal("X", LabelFor(glyphs, logical!.Value));

        var source = Assert.IsType<ButtonBinding>(Sdl3ControlSet.Build(Pad(glyphs))
            .Single(control => control.Label == "X")
            .Source);
        Assert.Equal(GamepadButton.X, source.Button);
    }

    [Fact]
    public void Every_Face_Control_Is_Labelled_With_The_Glyph_Of_The_Button_It_Is_Bound_To()
    {
        var glyphs = new[] { "B", "A", "Y", "X" };
        var map = Sdl3FaceButtonMap.From(Pad(glyphs));
        var controls = Sdl3ControlSet.Build(Pad(glyphs));

        foreach (var position in new[]
                 {
                     (SDL.GamepadButton.South, "B"),
                     (SDL.GamepadButton.East, "A"),
                     (SDL.GamepadButton.West, "Y"),
                     (SDL.GamepadButton.North, "X"),
                 })
        {
            var logical = Pressed(map, position.Item1);
            Assert.Equal(position.Item2, LabelFor(glyphs, logical!.Value));
        }
    }

    [Fact]
    public void An_Xbox_Pad_Behaves_Exactly_As_It_Did()
    {
        var glyphs = new[] { "A", "B", "X", "Y" };
        var map = Sdl3FaceButtonMap.From(Pad(glyphs));

        Assert.Equal(GamepadButton.A, Pressed(map, SDL.GamepadButton.South));
        Assert.Equal(GamepadButton.B, Pressed(map, SDL.GamepadButton.East));
        Assert.Equal(GamepadButton.X, Pressed(map, SDL.GamepadButton.West));
        Assert.Equal(GamepadButton.Y, Pressed(map, SDL.GamepadButton.North));

        Assert.Equal("A", LabelFor(glyphs, GamepadButton.A));
        Assert.Equal("Y", LabelFor(glyphs, GamepadButton.Y));
    }

    [Fact]
    public void A_DualShock_Pad_Behaves_Exactly_As_It_Did()
    {
        var glyphs = new[] { "Cross", "Circle", "Square", "Triangle" };
        var map = Sdl3FaceButtonMap.From(Pad(glyphs));

        Assert.Equal(GamepadButton.A, Pressed(map, SDL.GamepadButton.South));
        Assert.Equal(GamepadButton.B, Pressed(map, SDL.GamepadButton.East));
        Assert.Equal(GamepadButton.X, Pressed(map, SDL.GamepadButton.West));
        Assert.Equal(GamepadButton.Y, Pressed(map, SDL.GamepadButton.North));
    }

    [Fact]
    public void A_Glyph_Sdl_Cannot_Read_Falls_Back_To_The_Position()
    {
        var glyphs = new[] { "Unknown", "Unknown", "Unknown", "Unknown" };
        var map = Sdl3FaceButtonMap.From(Pad(glyphs));

        Assert.Equal(GamepadButton.A, Pressed(map, SDL.GamepadButton.South));
        Assert.Equal(GamepadButton.Y, Pressed(map, SDL.GamepadButton.North));
        Assert.Equal("A", LabelFor(glyphs, GamepadButton.A));
        Assert.Equal("Y", LabelFor(glyphs, GamepadButton.Y));
    }

    [Fact]
    public void The_Label_And_The_Logical_Button_Are_The_Same_Glyph_For_Every_Face_Key()
    {
        // The invariant behind the fix, on every layout SDL knows: the label a control shows is
        // the glyph of the slot that logical button comes from. A label naming a different button
        // than the one it runs is the bug, whatever the cause.
        foreach (var glyphs in new[]
                 {
                     new[] { "B", "A", "Y", "X" },
                     new[] { "A", "B", "X", "Y" },
                     new[] { "Cross", "Circle", "Square", "Triangle" },
                     new[] { "A", "X", "B", "Y" },
                     new[] { "Unknown", "Unknown", "Unknown", "Unknown" },
                 })
        {
            var map = Sdl3FaceButtonMap.From(Pad(glyphs));
            var slots = new[] { map.South, map.East, map.West, map.North };

            foreach (var control in Sdl3ControlSet.Build(Pad(glyphs)))
            {
                if (control.Source is not ButtonBinding { Button: var button } || button == GamepadButton.Extra)
                {
                    continue;
                }

                var slot = Array.IndexOf(slots, button);
                var readable = slot >= 0
                    && !string.IsNullOrWhiteSpace(glyphs[slot])
                    && !string.Equals(glyphs[slot], "Unknown", StringComparison.Ordinal);

                Assert.Equal(
                    readable ? glyphs[slot] : GamepadControlLabels.Button(button),
                    control.Label);
            }
        }
    }

    [Fact]
    public void The_Label_Table_Matches_What_The_Linked_Sdl_Answers()
    {
        // The glyph tables in Sdl3FaceButtonMap and in this test are transcriptions of SDL's own.
        // Rather than trust them, ask the linked SDL: a mapping may carry "type:", which is what
        // decides a device's face style, so a virtual joystick mapped as a Switch Pro answers
        // with SDL's own glyphs.
        if (!Sdl3JoystickEndToEndTests.RuntimeAvailableForTest())
        {
            return;
        }

        const ushort vendorId = 0x2b10;
        var descriptor = new SDL.VirtualJoystickDesc
        {
            Version = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<SDL.VirtualJoystickDesc>(),
            Type = SDL.JoystickType.Unknown,
            VendorID = vendorId,
            ProductID = 0x000b,
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

                // What SDL reports, and therefore what the map this build derives from it is.
                Assert.Equal(["B", "A", "Y", "X"], answered);

                // And pressing the physical X - the button the mapping binds to SDL's north
                // slot, which is "y:b3" - has to come back as logical X, not Y. The virtual
                // driver stages the press and SDL_UpdateGamepads is what publishes it, the same
                // reason the provider pumps once per tick.
                var joystick = SDL.GetGamepadJoystick(gamepad);
                Assert.True(SDL.SetJoystickVirtualButton(joystick, 3, true));
                SDL.UpdateGamepads();

                Assert.True(SDL.GetGamepadButton(gamepad, SDL.GamepadButton.North));
                Assert.Equal(
                    GamepadButton.X,
                    Pressed(Sdl3FaceButtonMap.From(Pad(answered)), SDL.GamepadButton.North));

                SDL.SetJoystickVirtualButton(joystick, 3, false);
                SDL.UpdateGamepads();
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
}
