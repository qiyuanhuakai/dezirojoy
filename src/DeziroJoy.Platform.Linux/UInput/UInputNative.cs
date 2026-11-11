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

    // UI_SET_EVBIT/KEYBIT/RELBIT are _IOW('U', 100..102, int) from the kernel uapi header
    // (linux/uinput.h): direction _IOC_WRITE, size sizeof(int), type 'U' (0x55), nr 100..102.
    // The nr digit is what a wrong constant gets wrong - 0x40045500/01/02 decode as
    // nr 0..2, which the kernel rejects with EINVAL on the first ioctl, so every
    // injected event is silently dropped. Verified against the installed kernel headers.
    public const uint UiSetEvBit = 0x40045564;

    public const uint UiSetKeyBit = 0x40045565;

    public const uint UiSetRelBit = 0x40045566;

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
