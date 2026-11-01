using System.Runtime.InteropServices;

namespace DeziroJoy.Platform.Linux.Evdev;

internal static partial class EvdevNative
{
    public const int ORdOnly = 0;

    public const int ONonBlock = 0x800;

    public const int MaxEventDevices = 32;

    public const int EventSize = 24;

    public const uint EvIocGName = 0x81004506;

    public const uint EvIocGAbs = 0x80184500;

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
