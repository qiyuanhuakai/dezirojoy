using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Keys;

namespace DeziroJoy.Platform.Linux.UInput;

internal static partial class UInputNative
{
    public const int OWrOnly = 1;

    public const ushort BusVirtual = 0x06;

    public const uint UiSetEvBit = 0x40045500;

    public const uint UiSetKeyBit = 0x40045501;

    public const uint UiSetRelBit = 0x40045502;

    public const uint UiDevSetup = 0x405C5503;

    public const uint UiDevCreate = 0x5501;

    public const uint UiDevDestroy = 0x5502;

    [LibraryImport("libc.so.6", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc.so.6", EntryPoint = "close", SetLastError = true)]
    public static partial int Close(int fd);

    [LibraryImport("libc.so.6", EntryPoint = "ioctl", SetLastError = true)]
    public static unsafe partial int Ioctl(int fd, uint request, void* arg);

    [LibraryImport("libc.so.6", EntryPoint = "write", SetLastError = true)]
    public static unsafe partial nint Write(int fd, void* buffer, nuint count);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UinputSetup
{
    public ushort Bustype;

    public ushort Vendor;

    public ushort Product;

    public ushort Version;

    public fixed byte Name[80];

    public uint FfEffectsMax;
}
