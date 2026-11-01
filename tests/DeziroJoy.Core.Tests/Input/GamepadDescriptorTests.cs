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
}
