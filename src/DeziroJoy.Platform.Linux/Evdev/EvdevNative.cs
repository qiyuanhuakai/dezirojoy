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

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static unsafe partial nint Read(int fd, void* buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
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
