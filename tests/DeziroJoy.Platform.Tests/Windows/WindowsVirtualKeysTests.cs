using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Windows.Keys;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

public class WindowsVirtualKeysTests
{
    [Fact]
    public void Every_GamepadKey_Maps_To_A_Virtual_Key()
    {
        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            Assert.NotEqual(WindowsVirtualKeys.Unmapped, WindowsVirtualKeys.ToVirtualKey(key));
        }
    }

    [Fact]
    public void Letters_Map_To_Ascii_Virtual_Key_Codes()
    {
        Assert.Equal(0x41, WindowsVirtualKeys.ToVirtualKey(GamepadKey.A));
        Assert.Equal(0x5A, WindowsVirtualKeys.ToVirtualKey(GamepadKey.Z));
    }

    [Theory]
    [InlineData(GamepadKey.Enter, 0x0D)]
    [InlineData(GamepadKey.Escape, 0x1B)]
    [InlineData(GamepadKey.Space, 0x20)]
    [InlineData(GamepadKey.ArrowUp, 0x26)]
    [InlineData(GamepadKey.ArrowDown, 0x28)]
    [InlineData(GamepadKey.ArrowLeft, 0x25)]
    [InlineData(GamepadKey.ArrowRight, 0x27)]
    [InlineData(GamepadKey.LeftCtrl, 0xA2)]
    [InlineData(GamepadKey.LeftShift, 0xA0)]
    [InlineData(GamepadKey.LeftAlt, 0x12)]
    [InlineData(GamepadKey.LeftWin, 0x5B)]
    public void Named_Keys_Use_Their_Windows_Codes(GamepadKey key, ushort expected)
        => Assert.Equal(expected, WindowsVirtualKeys.ToVirtualKey(key));

    [Theory]
    [InlineData(GamepadKey.ArrowUp, true)]
    [InlineData(GamepadKey.Delete, true)]
    [InlineData(GamepadKey.Home, true)]
    [InlineData(GamepadKey.Enter, false)]
    [InlineData(GamepadKey.A, false)]
    public void Extended_Key_Flag_Matches_Windows_Requirements(GamepadKey key, bool expected)
        => Assert.Equal(expected, WindowsVirtualKeys.IsExtendedKey(key));
}
