using System.Runtime.InteropServices;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Platform.Windows.SendInput;

internal static partial class SendInputNative
{
    public const uint InputMouse = 0;

    public const uint InputKeyboard = 1;

    public const uint MouseEventMove = 0x0001;

    public const uint MouseEventLeftDown = 0x0002;

    public const uint MouseEventLeftUp = 0x0004;

    public const uint MouseEventRightDown = 0x0008;

    public const uint MouseEventRightUp = 0x0010;

    public const uint MouseEventMiddleDown = 0x0020;

    public const uint MouseEventMiddleUp = 0x0040;

    public const uint MouseEventXDown = 0x0080;

    public const uint MouseEventXUp = 0x0100;

    public const uint MouseEventWheel = 0x0800;

    public const uint MouseEventHWheel = 0x1000;

    public const uint KeyEventFExtendedKey = 0x0001;

    public const uint KeyEventFKeyUp = 0x0002;

    public const uint KeyEventFScancode = 0x0008;

    public const uint MapVirtualKeyVkToScanCode = 0;

    public const int WheelDelta = 120;

    public const uint XButton1 = 1;

    public const uint XButton2 = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, Span<INPUT> inputs, int size);

    [LibraryImport("user32.dll")]
    public static partial uint MapVirtualKeyW(uint code, uint mapType);
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUT
{
    [FieldOffset(0)]
    public uint Type;

    [FieldOffset(8)]
    public MOUSEINPUT Mouse;

    [FieldOffset(8)]
    public KEYBDINPUT Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int Dx;

    public int Dy;

    public uint MouseData;

    public uint DwFlags;

    public uint Time;

    public nuint DwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort WVk;

    public ushort WScan;

    public uint DwFlags;

    public uint Time;

    public nuint DwExtraInfo;
}
