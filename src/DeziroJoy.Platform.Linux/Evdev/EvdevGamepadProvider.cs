using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>
/// Experimental Linux provider: scans /dev/input/event*, keeps a non-blocking reader thread
/// per gamepad and serves the latest normalized snapshot. Requires read access to the
/// evdev nodes (member of the "input" group or a udev rule).
/// </summary>
public sealed class EvdevGamepadProvider : IGamepadProvider
{
    private readonly Lock _sync = new();
    private readonly List<Device> _devices = [];

    public IReadOnlyList<GamepadDescriptor> GetConnected()
    {
        lock (_sync)
        {
            Scan();
            return _devices.Select(d => new GamepadDescriptor(d.Path, d.Name)).ToList();
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

    private void Scan()
    {
        var present = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < EvdevNative.MaxEventDevices; i++)
        {
            var path = $"/dev/input/event{i}";
            if (!File.Exists(path))
            {
                continue;
            }

            present.Add(path);

            lock (_sync)
            {
                if (_devices.Any(d => string.Equals(d.Path, path, StringComparison.Ordinal)))
                {
                    continue;
                }
            }

            if (TryOpen(path, out var device))
            {
                lock (_sync)
                {
                    _devices.Add(device);
                }

                StartReader(device);
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
    }

    private static bool TryOpen(string path, out Device device)
    {
        device = null!;

        var fd = EvdevNative.Open(path, EvdevNative.ORdOnly | EvdevNative.ONonBlock);
        if (fd < 0)
        {
            return false;
        }

        try
        {
            var name = ReadDeviceName(fd);
            if (!LooksLikeGamepad(fd, name))
            {
                EvdevNative.Close(fd);
                return false;
            }

            device = new Device
            {
                Path = path,
                Name = name,
                Tracker = new EvdevGamepadStateTracker(ReadAxisRanges(fd)),
                Fd = fd,
            };

            return true;
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

    private static unsafe bool LooksLikeGamepad(int fd, string name)
    {
        Span<byte> bits = stackalloc byte[8];

        fixed (byte* pointer = bits)
        {
            if (EvdevNative.Ioctl(fd, EvdevNative.EvIocGBitAbs, pointer) < 0)
            {
                return false;
            }
        }

        var mask = BinaryPrimitives.ReadUInt64LittleEndian(bits);
        var hasLeftStick = (mask & (1UL << EvdevCodes.AbsX)) != 0;
        var hasRightStick = (mask & (1UL << EvdevCodes.AbsRx)) != 0;

        return hasLeftStick && hasRightStick;
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
                    device.Tracker.Apply(ParseEvent(buffer, i));
                }
            }
        }
    }

    private static EvdevEvent ParseEvent(ReadOnlySpan<byte> buffer, int index)
    {
        var offset = index * EvdevNative.EventSize;

        return new EvdevEvent(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 16)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 18)..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 20)..]));
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

        public int Fd { get; set; }

        public volatile bool Running;

        public Thread? Reader { get; set; }
    }
}
