using System.Runtime.InteropServices;
using DeziroJoy.Platform.Windows.SendInput;
using DeziroJoy.Platform.Windows.XInput;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// Characterization tests pinning the P/Invoke struct layouts to the Win32 ABI.
/// A wrong FieldOffset here silently corrupts input, so these are load-bearing.
/// </summary>
public class NativeLayoutTests
{
    [Fact]
    public void Input_Struct_Matches_The_Win32_Layout()
    {
        Assert.Equal(40, Marshal.SizeOf<INPUT>());
        Assert.Equal(0, Marshal.OffsetOf<INPUT>(nameof(INPUT.Type)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<INPUT>(nameof(INPUT.Mouse)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<INPUT>(nameof(INPUT.Keyboard)).ToInt32());
    }

    [Fact]
    public void MouseInput_Struct_Is_32_Bytes_With_Dx_First()
    {
        Assert.Equal(32, Marshal.SizeOf<MOUSEINPUT>());
        Assert.Equal(0, Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.Dx)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.Dy)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.MouseData)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.DwFlags)).ToInt32());
    }

    [Fact]
    public void KeybdInput_Struct_Is_24_Bytes()
    {
        Assert.Equal(24, Marshal.SizeOf<KEYBDINPUT>());
        Assert.Equal(0, Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.WVk)).ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.WScan)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.DwFlags)).ToInt32());
    }

    [Fact]
    public void XInputGamepad_Struct_Matches_The_Win32_Layout()
    {
        Assert.Equal(12, Marshal.SizeOf<XInputGamepad>());
        Assert.Equal(0, Marshal.OffsetOf<XInputGamepad>(nameof(XInputGamepad.Buttons)).ToInt32());
        Assert.Equal(2, Marshal.OffsetOf<XInputGamepad>(nameof(XInputGamepad.LeftTrigger)).ToInt32());
        Assert.Equal(3, Marshal.OffsetOf<XInputGamepad>(nameof(XInputGamepad.RightTrigger)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<XInputGamepad>(nameof(XInputGamepad.ThumbLX)).ToInt32());
        Assert.Equal(10, Marshal.OffsetOf<XInputGamepad>(nameof(XInputGamepad.ThumbRY)).ToInt32());
    }

    [Fact]
    public void XInputState_Struct_Is_16_Bytes()
    {
        Assert.Equal(16, Marshal.SizeOf<XInputState>());
        Assert.Equal(4, Marshal.OffsetOf<XInputState>(nameof(XInputState.Gamepad)).ToInt32());
    }
}
