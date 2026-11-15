using System.Runtime.InteropServices;

namespace DeziroJoy.Platform.Linux.Evdev;

internal static partial class EvdevNative
{
    public const int ORdOnly = 0;

    public const int ONonBlock = 0x800;

    /// <summary>EACCES on Linux: the node exists but this user may not open it.</summary>
    public const int ErrnoAccessDenied = 13;

    public const int EventSize = 24;

    public const uint EvIocGName = 0x81004506;

    /// <summary>
    /// EVIOCGABS(0) = 0x80184540. Derived with the kernel's _IOC macro
    /// (<c>_IOC_READ</c> = 2, type 'E' = 0x45, nr = 0x40 + abs, size = sizeof(struct
    /// input_absinfo) = 24): (2 &lt;&lt; 30) | (0x45 &lt;&lt; 8) | 0x40 | (24 &lt;&lt; 16). The axis
    /// number is OR'd onto the 0x40 nr base, so <see cref="EvIocGAbs"/> | axis is EVIOCGABS(axis)
    /// for every axis below 0x40 (ABS_MAX). The nr base is what the kernel dispatches on
    /// (<c>_IOC_NR(cmd) &amp; ~ABS_MAX == _IOC_NR(EVIOCGABS(0))</c>): with nr = 0x00..0x3f
    /// instead, every EVIOCGABS call fails EINVAL - or, for axis codes 0x20..0x3f, is
    /// mis-answered as EVIOCGBIT and fills the absinfo struct with bitmask bytes. Verified
    /// against the kernel header: gcc, EVIOCGABS(0) = 0x80184540.
    /// </summary>
    public const uint EvIocGAbs = 0x80184540;

    public const uint EvIocGBitAbs = 0x80084523;

    /// <summary>
    /// EVIOCGBIT(EV_KEY, 96) = 0x80604521. Derived with the kernel's _IOC macro
    /// (<c>_IOC_READ</c> = 2, type 'E' = 0x45, nr = 0x20 + EV_KEY = 0x21, size = 96):
    /// (2 &lt;&lt; 30) | (0x45 &lt;&lt; 8) | 0x21 | (96 &lt;&lt; 16). The same formula reproduces
    /// <see cref="EvIocGBitAbs"/> (EVIOCGBIT(EV_ABS, 8) = 0x80084523), which is the check that
    /// this constant is derived rather than guessed.
    /// </summary>
    public const uint EvIocGBitKey = 0x80604521;

    /// <summary>Bytes for a full EV_KEY bitmask: KEY_CNT (0x300) bits rounded up to whole bytes.</summary>
    public const int KeyBitBufferSize = 96;

    [LibraryImport("libc.so.6", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc.so.6", EntryPoint = "read", SetLastError = true)]
    public static unsafe partial nint Read(int fd, void* buffer, nuint count);

    [LibraryImport("libc.so.6", EntryPoint = "ioctl", SetLastError = true)]
    public static unsafe partial int Ioctl(int fd, uint request, void* arg);
}

[StructLayout(LayoutKind.Sequential)]
internal struct InputAbsinfo
{
    public int Value;

    public int Minimum;

    public int Maximum;

    public int Fuzz;

    public int Flat;

    public int Resolution;
}
