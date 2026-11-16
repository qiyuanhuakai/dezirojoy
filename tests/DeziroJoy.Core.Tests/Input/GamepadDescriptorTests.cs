using DeziroJoy.Core.Input;
using Xunit;

namespace DeziroJoy.Core.Tests.Input;

/// <summary>
/// The optional button count on <see cref="GamepadDescriptor"/>: null means "unknown", which
/// is what XInput must report because its 14-button set is fixed and closed.
/// </summary>
public class GamepadDescriptorTests
{
    [Fact]
    public void Descriptor_Defaults_To_An_Unknown_Button_Count()
    {
        var descriptor = new GamepadDescriptor("dev-1", "Xbox Wireless Controller");

        Assert.Null(descriptor.ButtonCount);
    }

    [Fact]
    public void Descriptor_Carries_A_Known_Button_Count()
    {
        var descriptor = new GamepadDescriptor("dev-1", "Generic USB Gamepad") { ButtonCount = 17 };

        Assert.Equal(17, descriptor.ButtonCount);
    }

    [Fact]
    public void Button_Count_Participates_In_Descriptor_Identity()
    {
        var unknown = new GamepadDescriptor("dev-1", "Generic USB Gamepad");
        var known = new GamepadDescriptor("dev-1", "Generic USB Gamepad") { ButtonCount = 17 };

        Assert.NotEqual(unknown, known);
        Assert.Equal(known, new GamepadDescriptor("dev-1", "Generic USB Gamepad") { ButtonCount = 17 });
    }

    [Fact]
    public void Layout_Detection_Is_Unaffected_By_The_Button_Count()
    {
        var descriptor = new GamepadDescriptor("dev-1", "Sony DualSense Wireless Controller") { ButtonCount = 17 };

        Assert.Equal(GamepadLayout.DualShock, descriptor.Layout);
    }

    [Fact]
    public void Controls_Default_To_Empty_And_The_Positional_Constructor_Still_Works()
    {
        // A platform that has not written a description yet - XInput, evdev - constructs the
        // descriptor exactly as it always did, and reads back as "no description", which is not
        // the same claim as "no controls".
        var descriptor = new GamepadDescriptor("xinput:0", "Xbox 兼容手柄 #1");

        Assert.Empty(descriptor.Controls);
        Assert.Equal("xinput:0", descriptor.DeviceId);
        Assert.Equal("Xbox 兼容手柄 #1", descriptor.Name);
    }

    [Fact]
    public void Controls_Participate_In_Descriptor_Identity()
    {
        var controls = GamepadControlSet.Build(new GamepadControlDeclaration
        {
            Buttons = GamepadButtonMask.A,
        });

        var bare = new GamepadDescriptor("dev-1", "Generic USB Gamepad");
        var described = bare with { Controls = controls };

        Assert.NotEqual(bare, described);
        Assert.Equal(described, bare with { Controls = controls });
    }

    [Fact]
    public void Controls_Are_Descriptions_And_Carry_No_Live_State()
    {
        var controls = GamepadControlSet.Build(new GamepadControlDeclaration
        {
            Buttons = GamepadButtonMask.A | GamepadButtonMask.LeftTrigger,
            Sticks = [GamepadStick.Left],
        });

        // Nothing here moves: the same declaration describes the same controls however many
        // times it is asked, and no sample of pressed buttons changes the answer.
        Assert.Equal(controls, GamepadControlSet.Build(new GamepadControlDeclaration
        {
            Buttons = GamepadButtonMask.A | GamepadButtonMask.LeftTrigger,
            Sticks = [GamepadStick.Left],
        }));

        var descriptor = new GamepadDescriptor("dev-1", "Generic USB Gamepad") { Controls = controls };

        Assert.Equal(3, descriptor.Controls.Count);
        Assert.DoesNotContain(
            typeof(IGamepadState).GetProperties().Select(property => property.Name),
            name => typeof(GamepadControlDescriptor).GetProperty(name) is not null);
    }
}
