using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Experimental Linux provider: enumerates /dev/input, keeps a non-blocking reader thread
/// per gamepad and serves the latest normalized snapshot. Requires read access to the
/// evdev nodes (member of the "input" group or a udev rule) - when that is missing the scan
/// says so through <see cref="LastScanDiagnostic"/> instead of failing silently.
/// </summary>
public sealed class EvdevGamepadProvider : IGamepadProvider
{
    private readonly Lock _sync = new();
    private readonly List<Device> _devices = [];

    /// <summary>
    /// What the most recent scan found, and why it found it. Null until the first scan. A zero
    /// count means nothing on its own - "nothing plugged in", "no permission" and "no input
    /// subsystem" need three different fixes - so the daemon prints
    /// <see cref="EvdevScanDiagnostic.Guidance"/> and the GUI status area quotes
    /// <see cref="EvdevScanDiagnostic.Hint"/>.
    /// </summary>
    public EvdevScanDiagnostic? LastScanDiagnostic { get; private set; }

    public IReadOnlyList<GamepadDescriptor> GetConnected()
    {
        lock (_sync)
        {
            Scan();
            return _devices.Select(d => new GamepadDescriptor(d.Path, d.Name) { ButtonCount = d.ButtonCount }).ToList();
        }
    }

    public IGamepadState? Poll(string deviceId)
    {
        lock (_sync)
        {
            var device = _devices.FirstOrDefault(d => string.Equals(d.Path, deviceId, StringComparison.Ordinal));
            return device?.Tracker.Snapshot();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var device in _devices)
            {
                Stop(device);
            }

            _devices.Clear();
        }
    }

    /// <summary>
    /// One-shot diagnostic scan: opens every event node, classifies it and closes it again,
    /// keeping no readers and no devices. The GUI is a client of the daemon and owns no devices
    /// itself, so this is how it explains an empty device table with the same classification
    /// the daemon's own scan produces - same user, same nodes, same answer.
    /// </summary>
    /// <param name="deviceDirectory">
    /// Defaults to <c>/dev/input</c> on Linux. Tests point it at a fixture tree; passing a
    /// directory explicitly is also what makes the probe meaningful off Linux.
    /// </param>
    public static EvdevScanDiagnostic ProbeOnce(string? deviceDirectory = null)
    {
        var directory = deviceDirectory ?? EvdevNodes.DirectoryPath;
        var directoryExists = EvdevNodes.DirectoryExists(directory);
        var nodes = EvdevNodes.Enumerate(directory);

        var nodeCount = 0;
        var deniedCount = 0;
        var gamepadCount = 0;
        string? firstDeniedPath = null;
        int? firstErrno = null;

        foreach (var path in nodes)
        {
            nodeCount++;

            switch (TryOpen(path, out var device, out var errno))
            {
                case EvdevOpenResult.Opened:
                    gamepadCount++;

                    // The probe owns no readers, so the descriptor it just built is released
                    // immediately; only the classification survives.
                    EvdevNative.Close(device.Fd);
                    break;

                case EvdevOpenResult.AccessDenied:
                    deniedCount++;
                    firstDeniedPath ??= path;
                    firstErrno ??= errno;
                    break;
            }
        }

        return BuildDiagnostic(directoryExists, nodeCount, deniedCount, gamepadCount, firstDeniedPath, firstErrno);
    }

    private void Scan()
    {
        var directoryExists = EvdevNodes.DirectoryExists();
        var nodes = EvdevNodes.Enumerate();
        var present = new HashSet<string>(StringComparer.Ordinal);

        var nodeCount = 0;
        var deniedCount = 0;
        var gamepadCount = 0;
        string? firstDeniedPath = null;
        int? firstErrno = null;

        foreach (var path in nodes)
        {
            nodeCount++;
            present.Add(path);

            lock (_sync)
            {
                if (_devices.Any(d => string.Equals(d.Path, path, StringComparison.Ordinal)))
                {
                    // Already served: it passed the capability check when it was opened, so it
                    // still counts as a gamepad even though this scan opens nothing.
                    gamepadCount++;
                    continue;
                }
            }

            switch (TryOpen(path, out var device, out var errno))
            {
                case EvdevOpenResult.Opened:
                    lock (_sync)
                    {
                        _devices.Add(device);
                    }

                    StartReader(device);
                    gamepadCount++;
                    break;

                case EvdevOpenResult.AccessDenied:
                    deniedCount++;
                    firstDeniedPath ??= path;
                    firstErrno ??= errno;
                    break;
            }
        }

        lock (_sync)
        {
            for (var i = _devices.Count - 1; i >= 0; i--)
            {
                if (!present.Contains(_devices[i].Path))
                {
                    Stop(_devices[i]);
                    _devices.RemoveAt(i);
                }
            }
        }

        LastScanDiagnostic = BuildDiagnostic(directoryExists, nodeCount, deniedCount, gamepadCount, firstDeniedPath, firstErrno);
    }

    private static EvdevScanDiagnostic BuildDiagnostic(
        bool directoryExists,
        int nodeCount,
        int deniedCount,
        int gamepadCount,
        string? firstDeniedPath,
        int? firstErrno)
        => new()
        {
            Outcome = EvdevScanDiagnostic.ClassifyOutcome(directoryExists, nodeCount, deniedCount, gamepadCount),
            NodeCount = nodeCount,
            DeniedNodeCount = deniedCount,
            GamepadCount = gamepadCount,
            FirstDeniedPath = firstDeniedPath,
            Errno = firstErrno,
        };

    private enum EvdevOpenResult
    {
        Opened,

        /// <summary>The node opened but reported no gamepad capabilities (mouse, keyboard, pad).</summary>
        NotAGamepad,

        /// <summary>open() failed with EACCES: the node exists, this user may not read it.</summary>
        AccessDenied,

        /// <summary>open() failed for some other reason (vanished device, ENODEV, ...).</summary>
        OpenFailed,
    }

    private static EvdevOpenResult TryOpen(string path, out Device device, out int errno)
    {
        device = null!;
        errno = 0;

        var fd = EvdevNative.Open(path, EvdevNative.ORdOnly | EvdevNative.ONonBlock);
        if (fd < 0)
        {
            // File.Exists used to swallow this: a node the user cannot open is not an absent
            // node, and the two need opposite fixes.
            errno = Marshal.GetLastPInvokeError();
            return errno == EvdevNative.ErrnoAccessDenied ? EvdevOpenResult.AccessDenied : EvdevOpenResult.OpenFailed;
        }

        try
        {
            var name = ReadDeviceName(fd);
            if (!LooksLikeGamepad(fd))
            {
                EvdevNative.Close(fd);
                return EvdevOpenResult.NotAGamepad;
            }

            device = new Device
            {
                Path = path,
                Name = name,
                Tracker = new EvdevGamepadStateTracker(ReadAxisRanges(fd)),
                ButtonCount = ReadButtonCount(fd),
                Fd = fd,
            };

            return EvdevOpenResult.Opened;
        }
        catch
        {
            EvdevNative.Close(fd);
            throw;
        }
    }

    private static unsafe string ReadDeviceName(int fd)
    {
        Span<byte> buffer = stackalloc byte[256];

        fixed (byte* pointer = buffer)
        {
            if (EvdevNative.Ioctl(fd, EvdevNative.EvIocGName, pointer) < 0)
            {
                return "未知手柄";
            }
        }

        var end = buffer.IndexOf((byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer[..end].ToArray());
    }

    /// <summary>
    /// Reads both capability masks off a live descriptor and classifies it. The ioctls fail on
    /// anything that is not an evdev node, which is a "not a gamepad", never an error.
    /// </summary>
    private static unsafe bool LooksLikeGamepad(int fd)
    {
        Span<byte> absBits = stackalloc byte[8];

        fixed (byte* pointer = absBits)
        {
            if (EvdevNative.Ioctl(fd, EvdevNative.EvIocGBitAbs, pointer) < 0)
            {
                return false;
            }
        }

        Span<byte> keyBits = stackalloc byte[EvdevNative.KeyBitBufferSize];

        fixed (byte* pointer = keyBits)
        {
            if (EvdevNative.Ioctl(fd, EvdevNative.EvIocGBitKey, pointer) < 0)
            {
                return false;
            }
        }

        return LooksLikeGamepad(BinaryPrimitives.ReadUInt64LittleEndian(absBits), keyBits);
    }

    /// <summary>
    /// Capability classifier over the raw EVIOCGBIT masks - the one place the rules live, pure
    /// so both directions are pinned by tests.
    /// <para>
    /// A second stick (ABS_RX/ABS_RY) or an explicit gamepad button (BTN_JOYSTICK, the face
    /// cluster, d-pad keys, BTN_TRIGGER_HAPPY) makes a device a gamepad. ABS_X+ABS_Y alone does
    /// not: that shape is exactly what a touchpad (ABS_X/ABS_Y + BTN_TOUCH) and a drawing tablet
    /// report, while arcade sticks that lack a right stick still carry one of the explicit
    /// gamepad buttons. A device with no absolute axes at all is a keyboard or a mouse.
    /// </para>
    /// </summary>
    internal static bool LooksLikeGamepad(ulong absMask, ReadOnlySpan<byte> keyMask)
    {
        if (absMask == 0)
        {
            return false;
        }

        if (HasAbs(absMask, EvdevCodes.AbsRx) || HasAbs(absMask, EvdevCodes.AbsRy))
        {
            // A second stick is the strongest signal there is.
            return true;
        }

        // BTN_SOUTH is BTN_GAMEPAD (input-event-codes.h aliases 0x130), so the face cluster
        // covers both spellings; BTN_EAST/NORTH/WEST are the rest of the same cluster.
        return HasKey(keyMask, EvdevCodes.BtnJoystick)
            || HasKey(keyMask, EvdevCodes.BtnSouth)
            || HasKey(keyMask, EvdevCodes.BtnEast)
            || HasKey(keyMask, EvdevCodes.BtnNorth)
            || HasKey(keyMask, EvdevCodes.BtnWest)
            || HasKey(keyMask, EvdevCodes.BtnDpadUp)
            || HasKey(keyMask, EvdevCodes.BtnTriggerHappy1);
    }

    private static bool HasAbs(ulong mask, ushort code) => (mask & (1UL << code)) != 0;

    private static bool HasKey(ReadOnlySpan<byte> mask, ushort code)
        => code / 8 < mask.Length && (mask[code / 8] & (1 << (code % 8))) != 0;

    /// <summary>
    /// Asks the kernel how many buttons the device has, via EVIOCGBIT(EV_KEY). Returns null
    /// when the ioctl is unsupported, which keeps "unknown" distinct from "zero buttons".
    /// </summary>
    internal static unsafe int? ReadButtonCount(int fd)
    {
        Span<byte> bits = stackalloc byte[EvdevNative.KeyBitBufferSize];

        fixed (byte* pointer = bits)
        {
            if (EvdevNative.Ioctl(fd, EvdevNative.EvIocGBitKey, pointer) < 0)
            {
                return null;
            }
        }

        return CountSetBits(bits);
    }

    /// <summary>
    /// Popcounts an EVIOCGBIT(EV_KEY) bitmask: bit N of byte N/8 is set when key code N exists
    /// on the device, so the number of set bits is the device's button count.
    /// </summary>
    internal static int CountSetBits(ReadOnlySpan<byte> bits)
    {
        var count = 0;
        foreach (var value in bits)
        {
            count += BitOperations.PopCount(value);
        }

        return count;
    }

    private static unsafe Dictionary<ushort, EvdevAxisRange> ReadAxisRanges(int fd)
    {
        var ranges = new Dictionary<ushort, EvdevAxisRange>();

        foreach (var axis in new ushort[]
                 {
                     EvdevCodes.AbsX, EvdevCodes.AbsY, EvdevCodes.AbsRx, EvdevCodes.AbsRy, EvdevCodes.AbsZ, EvdevCodes.AbsRz,
                 })
        {
            var info = new InputAbsinfo();
            var pointer = &info;

            if (EvdevNative.Ioctl(fd, EvdevNative.EvIocGAbs | axis, pointer) < 0)
            {
                continue;
            }

            ranges[axis] = new EvdevAxisRange(info.Minimum, info.Maximum);
        }

        return ranges;
    }

    private void StartReader(Device device)
    {
        device.Running = true;
        device.Reader = new Thread(() => ReadLoop(device))
        {
            IsBackground = true,
            Name = $"evdev-reader-{device.Path}",
        };
        device.Reader.Start();
    }

    private unsafe void ReadLoop(Device device)
    {
        Span<byte> buffer = stackalloc byte[EvdevNative.EventSize * 32];

        while (device.Running)
        {
            nint read;
            fixed (byte* pointer = buffer)
            {
                read = EvdevNative.Read(device.Fd, pointer, (nuint)buffer.Length);
            }

            if (read == 0)
            {
                Remove(device);
                return;
            }

            if (read < 0)
            {
                Thread.Sleep(2);
                continue;
            }

            var events = (int)read / EvdevNative.EventSize;

            lock (_sync)
            {
                for (var i = 0; i < events; i++)
                {
                    device.Tracker.Apply(EvdevEventReader.Read(buffer, i));
                }
            }
        }
    }

    private void Remove(Device device)
    {
        lock (_sync)
        {
            Stop(device);
            _devices.Remove(device);
        }
    }

    private static void Stop(Device device)
    {
        device.Running = false;

        if (device.Fd >= 0)
        {
            EvdevNative.Close(device.Fd);
            device.Fd = -1;
        }
    }

    private sealed class Device
    {
        public required string Path { get; init; }

        public required string Name { get; init; }

        public required EvdevGamepadStateTracker Tracker { get; init; }

        public int? ButtonCount { get; init; }

        public int Fd { get; set; }

        public volatile bool Running;

        public Thread? Reader { get; set; }
    }
}
