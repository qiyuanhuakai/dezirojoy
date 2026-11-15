using DeziroJoy.Core.Mapping;
using DeziroJoy.Platform.Linux.Keys;
using DeziroJoy.Platform.Linux.Output;
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

    [Fact]
    public void No_GamepadKey_Maps_To_A_Mouse_Button_Code()
    {
        // The keyboard vocabulary and the mouse-button vocabulary are translated by two
        // different tables (LinuxKeyCodes and EvdevMouseButtons) that must never collide:
        // one code claimed by both would make a key press and a mouse click the same
        // physical output on the virtual device.
        var buttonCodes = Enum.GetValues<MouseButton>()
            .Select(button => EvdevMouseButtons.TryGetButtonCode(button, out var code) ? code : (ushort)0)
            .Where(code => code != 0)
            .ToArray();

        foreach (var key in Enum.GetValues<GamepadKey>())
        {
            Assert.DoesNotContain(LinuxKeyCodes.ToEvdevCode(key), buttonCodes);
        }
    }
}
