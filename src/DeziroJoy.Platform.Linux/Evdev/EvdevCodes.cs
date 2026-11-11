namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>Linux input event codes (input-event-codes.h) used by the evdev layer.</summary>
public static class EvdevCodes
{
    public const ushort EvSyn = 0x00;

    public const ushort EvKey = 0x01;

    public const ushort EvRel = 0x02;

    public const ushort EvAbs = 0x03;

    public const ushort AbsX = 0x00;

    public const ushort AbsY = 0x01;

    public const ushort AbsZ = 0x02;

    public const ushort AbsRx = 0x03;

    public const ushort AbsRy = 0x04;

    public const ushort AbsRz = 0x05;

    public const ushort AbsHat0X = 0x10;

    public const ushort AbsHat0Y = 0x11;

    // Face buttons. input-event-codes.h aliases the positional names onto these codes:
    //   BTN_A = BTN_SOUTH, BTN_B = BTN_EAST, BTN_X = BTN_NORTH, BTN_Y = BTN_WEST.
    // The aliasing is the trap: BTN_WEST is 0x134, not 0x132 (0x132 is BTN_C).
    public const ushort BtnSouth = 0x130; // BTN_SOUTH 0x130 (BTN_A)

    public const ushort BtnEast = 0x131; // BTN_EAST 0x131 (BTN_B)

    public const ushort BtnC = 0x132; // BTN_C 0x132 - no Xbox-layout position, stays a raw extra

    public const ushort BtnNorth = 0x133; // BTN_NORTH 0x133 (BTN_X)

    public const ushort BtnWest = 0x134; // BTN_WEST 0x134 (BTN_Y)

    public const ushort BtnZ = 0x135; // BTN_Z 0x135 - no Xbox-layout position, stays a raw extra

    public const ushort BtnTl = 0x136; // BTN_TL 0x136

    public const ushort BtnTr = 0x137; // BTN_TR 0x137

    public const ushort BtnTl2 = 0x138; // BTN_TL2 0x138

    public const ushort BtnTr2 = 0x139; // BTN_TR2 0x139

    public const ushort BtnSelect = 0x13a; // BTN_SELECT 0x13a

    public const ushort BtnStart = 0x13b; // BTN_START 0x13b

    public const ushort BtnMode = 0x13c; // BTN_MODE 0x13c

    public const ushort BtnThumbL = 0x13d; // BTN_THUMBL 0x13d

    public const ushort BtnThumbR = 0x13e; // BTN_THUMBR 0x13e

    // BTN_GEAR_DOWN 0x150 is dual-named: input-event-codes.h #defines it as BTN_WHEEL too,
    // so it is not reliably a menu button. Both gear codes stay raw extras.
    public const ushort BtnGearDown = 0x150; // BTN_GEAR_DOWN 0x150 (BTN_WHEEL)

    public const ushort BtnGearUp = 0x151; // BTN_GEAR_UP 0x151

    // D-pad reported as keys instead of the ABS_HAT0X/ABS_HAT0Y axes.
    public const ushort BtnDpadUp = 0x220; // BTN_DPAD_UP 0x220

    public const ushort BtnDpadDown = 0x221; // BTN_DPAD_DOWN 0x221

    public const ushort BtnDpadLeft = 0x222; // BTN_DPAD_LEFT 0x222

    public const ushort BtnDpadRight = 0x223; // BTN_DPAD_RIGHT 0x223

    // BTN_TRIGGER_HAPPY1 0x2c0 .. BTN_TRIGGER_HAPPY40 0x2e7: no logical equivalent exists,
    // so the whole range is deliberately left out of EvdevButtonMap and surfaces as raw
    // extras. The bounds are named here so tests can pin the range ends.
    public const ushort BtnTriggerHappy1 = 0x2c0; // BTN_TRIGGER_HAPPY1 0x2c0

    public const ushort BtnTriggerHappy40 = 0x2e7; // BTN_TRIGGER_HAPPY40 0x2e7

    public const ushort BtnLeft = 0x110;

    public const ushort BtnRight = 0x111;

    public const ushort BtnMiddle = 0x112;

    public const ushort BtnSide = 0x113;

    public const ushort BtnExtra = 0x114;

    /// <summary>BTN_TOUCH 0x14a - what a touchpad or touchscreen reports next to ABS_X/ABS_Y.</summary>
    public const ushort BtnTouch = 0x14a;

    /// <summary>
    /// BTN_JOYSTICK 0x120 - the explicit "this is a joystick" button, reported by arcade sticks
    /// and flight controls that carry no second stick.
    /// </summary>
    public const ushort BtnJoystick = 0x120;

    /// <summary>First BTN_* code (BTN_MISC). Codes below this are KEY_* keyboard keys.</summary>
    public const ushort BtnFirst = 0x100;

    /// <summary>Last BTN_* code (BTN_MAX).</summary>
    public const ushort BtnLast = 0x2FF;
}
