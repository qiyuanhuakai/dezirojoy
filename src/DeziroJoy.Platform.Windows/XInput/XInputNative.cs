using System.Runtime.InteropServices;

namespace DeziroJoy.Platform.Windows.XInput;

/// <summary>
/// XInput 1.4 bindings (Windows 8+). Only the joystick read path is needed;
/// vibration is intentionally omitted.
/// </summary>
internal static partial class XInputNative
{
    public const int ErrorSuccess = 0;

    public const int MaxControllerCount = 4;

    public const ushort DpadUp = 0x0001;

    public const ushort DpadDown = 0x0002;

    public const ushort DpadLeft = 0x0004;

    public const ushort DpadRight = 0x0008;

    public const ushort Start = 0x0010;

    public const ushort Back = 0x0020;

    public const ushort LeftThumb = 0x0040;

    public const ushort RightThumb = 0x0080;

    public const ushort LeftShoulder = 0x0100;

    public const ushort RightShoulder = 0x0200;

    /// <summary>
    /// Not a real XInput bit: XINPUT_GAMEPAD.wButtons defines no 0x0400 flag (the real bits
    /// jump from 0x0200 RIGHT_SHOULDER straight to 0x1000 A), so XInputGetState never sets
    /// it and this mask is always 0 - XInput cannot report the Guide button (M53; the same
    /// gap exists in Windows.Gaming.Input.GamepadButtons). Kept only as a placeholder so the
    /// mapper's per-bit walk stays aligned; never read it as "Windows can deliver Guide".
    /// The only viable path is Raw Input HID parsing - see the backlog note in
    /// docs/QA-CHECKLIST.md.
    /// </summary>
    public const ushort Guide = 0x0400;

    public const ushort A = 0x1000;

    public const ushort B = 0x2000;

    public const ushort X = 0x4000;

    public const ushort Y = 0x8000;

    public const byte TriggerPressedThreshold = 128;

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static partial int GetState(int userIndex, ref XInputState state);
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint PacketNumber;

    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputGamepad
{
    public ushort Buttons;

    public byte LeftTrigger;

    public byte RightTrigger;

    public short ThumbLX;

    public short ThumbLY;

    public short ThumbRX;

    public short ThumbRY;
}
