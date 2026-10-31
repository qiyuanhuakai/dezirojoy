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

    public const ushort BtnSouth = 0x130;

    public const ushort BtnEast = 0x131;

    public const ushort BtnWest = 0x132;

    public const ushort BtnNorth = 0x133;

    public const ushort BtnTl = 0x136;

    public const ushort BtnTr = 0x137;

    public const ushort BtnTl2 = 0x138;

    public const ushort BtnTr2 = 0x139;

    public const ushort BtnSelect = 0x13a;

    public const ushort BtnStart = 0x13b;

    public const ushort BtnMode = 0x13c;

    public const ushort BtnThumbL = 0x13d;

    public const ushort BtnThumbR = 0x13e;

    public const ushort BtnLeft = 0x110;

    public const ushort BtnRight = 0x111;

    public const ushort BtnMiddle = 0x112;

    public const ushort BtnSide = 0x113;

    public const ushort BtnExtra = 0x114;

    /// <summary>First BTN_* code (BTN_MISC). Codes below this are KEY_* keyboard keys.</summary>
    public const ushort BtnFirst = 0x100;

    /// <summary>Last BTN_* code (BTN_MAX).</summary>
    public const ushort BtnLast = 0x2FF;
}
