using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Capture;
using DeziroJoy.Platform.Windows.Keys;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The input half of the virtual-key table: virtual-key code to portable key, modifier
/// recognition and the modifier flags read off the held set. Pure arithmetic, so it is checked
/// here rather than against a real keyboard, and the round-trip with the output table
/// (<see cref="WindowsVirtualKeys"/>) is asserted so a key a profile can emit is also a key a
/// capture can read.
/// </summary>
public sealed class WindowsKeyCaptureTests
{
    [Fact]
    public void Every_Portable_Key_Round_Trips_Through_Its_Virtual_Key()
    {
        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            var vk = WindowsVirtualKeys.ToVirtualKey(key);

            Assert.NotEqual(WindowsVirtualKeys.Unmapped, vk);
            Assert.Equal(key, WindowsKeyCapture.ToGamepadKey(vk));
        }
    }

    [Theory]
    [InlineData(0x41, GamepadKey.A)]
    [InlineData(0x5A, GamepadKey.Z)]
    [InlineData(0x30, GamepadKey.D0)]
    [InlineData(0x39, GamepadKey.D9)]
    [InlineData(0x70, GamepadKey.F1)]
    [InlineData(0x7B, GamepadKey.F12)]
    [InlineData(0x7C, GamepadKey.F13)]
    [InlineData(0x87, GamepadKey.F24)]
    [InlineData(0x60, GamepadKey.Numpad0)]
    [InlineData(0x69, GamepadKey.Numpad9)]
    [InlineData(0x1B, GamepadKey.Escape)]
    [InlineData(0x0D, GamepadKey.Enter)]
    [InlineData(0x20, GamepadKey.Space)]
    [InlineData(0x26, GamepadKey.ArrowUp)]
    [InlineData(0xBD, GamepadKey.Minus)]
    [InlineData(0xBB, GamepadKey.Equal)]
    [InlineData(0xBA, GamepadKey.Semicolon)]
    [InlineData(0xDE, GamepadKey.Apostrophe)]
    [InlineData(0xC0, GamepadKey.Grave)]
    [InlineData(0xAF, GamepadKey.VolumeUp)]
    [InlineData(0xA6, GamepadKey.BrowserBack)]
    [InlineData(0x2C, GamepadKey.PrintScreen)]
    public void Virtual_Key_Maps_To_Its_Portable_Key(ushort vk, GamepadKey expected)
        => Assert.Equal(expected, WindowsKeyCapture.ToGamepadKey(vk));

    [Fact]
    public void An_Unassigned_Virtual_Key_Has_No_Portable_Key()
        => Assert.Null(WindowsKeyCapture.ToGamepadKey(0x88));

    [Theory]
    [InlineData(0x10)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0x11)]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    [InlineData(0x12)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void Modifier_Virtual_Keys_Are_Recognised(ushort vk)
        => Assert.True(WindowsKeyCapture.IsModifier(vk));

    [Theory]
    [InlineData(0x41)]
    [InlineData(0x1B)]
    [InlineData(0x88)]
    public void Non_Modifier_Virtual_Keys_Are_Not_Modifiers(ushort vk)
        => Assert.False(WindowsKeyCapture.IsModifier(vk));

    [Fact]
    public void Held_Modifiers_Read_As_Flags()
    {
        var held = new[] { WindowsKeyCapture.VkLControl, WindowsKeyCapture.VkLShift, WindowsKeyCapture.VkLWin };

        Assert.Equal(KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Win, WindowsKeyCapture.ToModifiers(held));
    }

    [Fact]
    public void Either_Side_Of_A_Modifier_Counts_The_Same()
    {
        var left = WindowsKeyCapture.ToModifiers([WindowsKeyCapture.VkLControl]);
        var right = WindowsKeyCapture.ToModifiers([WindowsKeyCapture.VkRControl]);
        var generic = WindowsKeyCapture.ToModifiers([WindowsKeyCapture.VkControl]);

        Assert.Equal(KeyModifiers.Ctrl, left);
        Assert.Equal(KeyModifiers.Ctrl, right);
        Assert.Equal(KeyModifiers.Ctrl, generic);
    }

    [Fact]
    public void Alt_Counts_From_The_Generic_Menu_Code_As_Well()
    {
        Assert.Equal(KeyModifiers.Alt, WindowsKeyCapture.ToModifiers([WindowsKeyCapture.VkMenu]));
        Assert.Equal(KeyModifiers.Alt, WindowsKeyCapture.ToModifiers([WindowsKeyCapture.VkRMenu]));
    }

    [Fact]
    public void No_Held_Modifiers_Reads_As_None()
        => Assert.Equal(KeyModifiers.None, WindowsKeyCapture.ToModifiers([]));
}
