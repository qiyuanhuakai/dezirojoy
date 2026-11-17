using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.Core.Tests.Input;

/// <summary>
/// The backend's compatibility label: what a surface that draws a fixed layout - and therefore
/// has no channel for a control description - can ask for instead.
/// </summary>
public class GamepadDescriptorLabelTests
{
    private static GamepadDescriptor Descriptor(IReadOnlyList<string> faceGlyphs)
    {
        var labels = new Dictionary<GamepadButton, string>();
        var positions = new[] { GamepadButton.A, GamepadButton.B, GamepadButton.X, GamepadButton.Y };
        for (var i = 0; i < positions.Length && i < faceGlyphs.Count; i++)
        {
            labels[positions[i]] = faceGlyphs[i];
        }

        var controls = GamepadControlSet.Build(new GamepadControlDeclaration
        {
            Buttons = GamepadButtonMask.A | GamepadButtonMask.B | GamepadButtonMask.X | GamepadButtonMask.Y,
            Labels = labels,
        });

        return new GamepadDescriptor("sdl:guid", "Nintendo Switch Pro Controller") { Controls = controls };
    }

    [Fact]
    public void A_Switch_Pad_Relabels_An_Xbox_Drawing_With_Its_Own_Glyphs()
    {
        // The hand-drawn pad is drawn once with the Xbox letters baked into the artwork. On a
        // Switch pad the physical X sits in the north position - logical Y - so that drawing
        // lights its "Y" when the user presses X. Relabelling fixes the display.
        var descriptor = Descriptor(["B", "A", "Y", "X"]);

        Assert.Equal("B", descriptor.ControlLabel(GamepadButton.A));
        Assert.Equal("A", descriptor.ControlLabel(GamepadButton.B));
        Assert.Equal("Y", descriptor.ControlLabel(GamepadButton.X));
        Assert.Equal("X", descriptor.ControlLabel(GamepadButton.Y));
    }

    [Fact]
    public void An_Xbox_Pad_Relabels_To_The_Same_Glyphs_The_Drawing_Already_Has()
    {
        var descriptor = Descriptor(["A", "B", "X", "Y"]);

        Assert.Equal("A", descriptor.ControlLabel(GamepadButton.A));
        Assert.Equal("B", descriptor.ControlLabel(GamepadButton.B));
        Assert.Equal("X", descriptor.ControlLabel(GamepadButton.X));
        Assert.Equal("Y", descriptor.ControlLabel(GamepadButton.Y));
    }

    [Fact]
    public void A_Descriptor_With_No_Description_Falls_Back_To_The_Logical_Name()
    {
        // XInput and evdev have not written a description yet. They still get a usable answer -
        // the logical name - rather than an empty string a surface would render as a blank key.
        var descriptor = new GamepadDescriptor("xinput:0", "Xbox 兼容手柄 #1");

        Assert.Equal("A", descriptor.ControlLabel(GamepadButton.A));
        Assert.Equal("Y", descriptor.ControlLabel(GamepadButton.Y));
    }

    [Fact]
    public void A_Control_The_Description_Does_Not_Carry_Falls_Back_Rather_Than_Throwing()
    {
        var descriptor = Descriptor(["Cross", "Circle", "Square", "Triangle"]);

        // A DualShock has no guide-button glyph in this description, so the logical name stands.
        Assert.Equal("Guide / Home", descriptor.ControlLabel(GamepadButton.Guide));
        Assert.Equal("左扳机", descriptor.ControlLabel(GamepadButton.LeftTrigger));
    }

    [Fact]
    public void Relabelling_Never_Touches_What_A_Mapping_Binds_To()
    {
        // The whole point: the display changes, the input does not. A profile bound to logical A
        // still means logical A on a Switch pad, so the same profile keeps working across brands.
        var descriptor = Descriptor(["B", "A", "Y", "X"]);

        var binding = Assert.IsType<ButtonBinding>(descriptor.Controls
            .Single(control => control.Label == "B")
            .Source);

        Assert.Equal(GamepadButton.A, binding.Button);
    }

    [Fact]
    public void The_Label_Comes_From_The_Description_So_The_Two_Cannot_Disagree()
    {
        var descriptor = Descriptor(["Cross", "Circle", "Square", "Triangle"]);

        foreach (var control in descriptor.Controls)
        {
            if (control.Source is not ButtonBinding binding)
            {
                continue;
            }

            Assert.Equal(control.Label, descriptor.ControlLabel(binding.Button));
        }
    }
}
