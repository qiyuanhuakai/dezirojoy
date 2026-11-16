namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// The native surface the evdev provider touches, behind an interface so tests can drive the
/// scan, the reader and the descriptor lifecycle deterministically - a gated open, a queued
/// event, a recorded close - without a kernel that has pads attached. Production wires
/// <see cref="LibcEvdevSystem"/>; every method keeps the exact contract of the
/// <see cref="EvdevNative"/> P/Invoke it wraps, including the errno the caller reads back
/// through <see cref="System.Runtime.InteropServices.Marshal.GetLastPInvokeError"/>.
/// </summary>
/// <remarks>
/// Pointer-free on purpose: the provider passes spans and structs, and only this file's libc
/// implementation needs <c>unsafe</c>. That keeps the fake a plain managed class.
/// </remarks>
internal interface IEvdevSystem
{
    bool DirectoryExists(string directory);

    /// <summary>
    /// Lists the event nodes under <paramref name="directory"/>. A listing that could not
    /// complete comes back as a failed <see cref="EvdevEnumeration"/> carrying the exception,
    /// so the caller can tell it from a directory that genuinely holds no nodes.
    /// </summary>
    EvdevEnumeration EnumerateEventNodes(string directory);

    /// <summary>open(2); returns the descriptor or -1 with the errno in the last P/Invoke error.</summary>
    int Open(string path, int flags);

    /// <summary>close(2); returns 0 or -1. Closing an already-closed descriptor is a bug the caller must not commit.</summary>
    int Close(int fd);

    /// <summary>ioctl(2); returns 0 or -1 with the errno in the last P/Invoke error.</summary>
    int Ioctl(int fd, uint request, Span<byte> argument);

    /// <summary>read(2); returns the byte count, or -1 with the errno in the last P/Invoke error.</summary>
    nint Read(int fd, Span<byte> buffer);

    /// <summary>
    /// poll(2) over <paramref name="descriptors"/>, writing the returned events back into each
    /// entry. Blocks up to <paramref name="timeoutMilliseconds"/> (-1 = forever), exactly like
    /// the libc call: a descriptor closed by another thread does not wake a poll in progress,
    /// which is why the reader keeps an eventfd beside the device descriptor.
    /// </summary>
    int Poll(Span<PollFd> descriptors, int timeoutMilliseconds);

    int EventFd(uint initialValue, int flags);

    /// <summary>Wakes everything polling this eventfd (the reader's stop signal).</summary>
    void SignalEventFd(int fd);
}

/// <summary>
/// The production <see cref="IEvdevSystem"/>: every call is the libc P/Invoke it wraps, with
/// the enumeration and existence check delegated to <see cref="EvdevNodes"/> so the directory
/// rules live in one place.
/// </summary>
internal sealed class LibcEvdevSystem : IEvdevSystem
{
    public static IEvdevSystem Shared { get; } = new LibcEvdevSystem();

    public bool DirectoryExists(string directory) => EvdevNodes.DirectoryExists(directory);

    public EvdevEnumeration EnumerateEventNodes(string directory) => EvdevNodes.Enumerate(directory);

    public int Open(string path, int flags) => EvdevNative.Open(path, flags);

    public int Close(int fd) => EvdevNative.Close(fd);

    public unsafe int Ioctl(int fd, uint request, Span<byte> argument)
    {
        fixed (byte* pointer = argument)
        {
            return EvdevNative.Ioctl(fd, request, pointer);
        }
    }

    public unsafe nint Read(int fd, Span<byte> buffer)
    {
        fixed (byte* pointer = buffer)
        {
            return EvdevNative.Read(fd, pointer, (nuint)buffer.Length);
        }
    }

    public unsafe int Poll(Span<PollFd> descriptors, int timeoutMilliseconds)
    {
        fixed (PollFd* pointer = descriptors)
        {
            return EvdevNative.Poll(pointer, (nuint)descriptors.Length, timeoutMilliseconds);
        }
    }

    public int EventFd(uint initialValue, int flags) => EvdevNative.EventFd(initialValue, flags);

    public unsafe void SignalEventFd(int fd)
    {
        ulong signal = 1;
        EvdevNative.Write(fd, &signal, sizeof(ulong));
    }
}
