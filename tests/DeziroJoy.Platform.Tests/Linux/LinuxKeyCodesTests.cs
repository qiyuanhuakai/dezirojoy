using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Keys;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

public class LinuxKeyCodesTests
{
    [Fact]
    public void Every_GamepadKey_Maps_To_An_Evdev_Code()
    {
        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            Assert.NotEqual(LinuxKeyCodes.Unmapped, LinuxKeyCodes.ToEvdevCode(key));
        }
    }

    [Theory]
    [InlineData(GamepadKey.Enter, 28)]
    [InlineData(GamepadKey.Escape, 1)]
    [InlineData(GamepadKey.Space, 57)]
    [InlineData(GamepadKey.A, 30)]
    [InlineData(GamepadKey.C, 46)]
    [InlineData(GamepadKey.V, 47)]
    [InlineData(GamepadKey.LeftCtrl, 29)]
    [InlineData(GamepadKey.LeftShift, 42)]
    [InlineData(GamepadKey.LeftAlt, 56)]
    [InlineData(GamepadKey.LeftWin, 125)]
    [InlineData(GamepadKey.ArrowUp, 103)]
    [InlineData(GamepadKey.ArrowDown, 108)]
    [InlineData(GamepadKey.ArrowLeft, 105)]
    [InlineData(GamepadKey.ArrowRight, 106)]
    public void Named_Keys_Use_Their_Evdev_Codes(GamepadKey key, ushort expected)
        => Assert.Equal(expected, LinuxKeyCodes.ToEvdevCode(key));
}
