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
/// <para>
/// The 250ms rescan is deliberately kept off the sampling path. It used to run inside
/// <see cref="GetConnected"/> under the one lock that also guarded <see cref="Poll"/> and
/// every reader's event application, so a slow walk of /dev/input - hundreds of nodes, each
/// an open plus a handful of ioctls - stalled the 2ms tick and every reader for the whole
/// scan, which is what a joystick moving in visible segments feels like. Now the connected
/// list is a cache rebuilt only when the device set changes, and every scan after the first
/// runs as a single background job that holds the list lock for list work only - never
/// across a directory walk or device I/O - so neither a scan nor another device's reader
/// can stall sampling, and each device's tracker has its own lock.
/// </para>
/// <para>
/// Documented lock exception: the very first scan runs inline on the calling thread with the
/// list lock held across its device I/O. The daemon's startup summary reads
/// <see cref="LastScanDiagnostic"/> immediately after its first <see cref="GetConnected"/>,
/// and that contract lives in the App layer this provider must not change - so startup pays
/// for one synchronous walk, once, and every later scan is background work. It is also the
/// only scan whose failure propagates to the caller.
/// </para>
/// <para>
/// A scan that learns nothing retires nothing. A listing that could not complete is not a
/// report that the pads are gone: the scan keeps every device it is serving, keeps the last
/// diagnostic it published, and records the reason in <see cref="LastScanError"/> instead of
/// stopping readers and dropping connected pads - and with them every held button - on one
/// transient failure.
/// </para>
/// </summary>
public sealed class EvdevGamepadProvider : IGamepadProvider
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a stop waits for a reader thread to observe its stop signal and exit before
    /// closing its descriptors anyway. The reader polls on a non-blocking device descriptor
    /// plus a stop eventfd, so it always wakes immediately; the timeout only keeps a wedged
    /// kernel from hanging shutdown.
    /// </summary>
    private static readonly TimeSpan ReaderStopTimeout = TimeSpan.FromSeconds(2);

    private readonly IEvdevSystem _system;
    private readonly Func<long> _timestamp;
    private readonly long _clockFrequency;

    /// <summary>
    /// Guards <see cref="_devices"/>, <see cref="_connected"/> and the scan schedule. It is
    /// held for list work only - a lookup, an add, a cache rebuild - and never across a
    /// directory walk, an open or an ioctl, which is what keeps <see cref="Poll"/> answering
    /// in microseconds while a scan is in flight.
    /// </summary>
    private readonly Lock _sync = new();
    private readonly List<Device> _devices = [];
    private IReadOnlyList<GamepadDescriptor> _connected = [];
    private long _nextScanTimestamp;
    private bool _firstScanDone;
    private Task? _scanTask;
    private int _generation;
    private bool _disposed;

    private volatile EvdevScanDiagnostic? _lastScanDiagnostic;

    private volatile string? _lastScanError;

    /// <summary>
    /// What the most recent scan found, and why it found it. Null until the first scan. A zero
    /// count means nothing on its own - "nothing plugged in", "no permission" and "no input
    /// subsystem" need three different fixes - so the daemon prints
    /// <see cref="EvdevScanDiagnostic.Guidance"/> and the GUI status area quotes
    /// <see cref="EvdevScanDiagnostic.Hint"/>.
    /// </summary>
    public EvdevScanDiagnostic? LastScanDiagnostic
    {
        get => _lastScanDiagnostic;
        private set => _lastScanDiagnostic = value;
    }

    /// <summary>
    /// Why the most recent scan could not complete, or null while scans succeed. A failed scan
    /// retires nothing - it keeps every device it was serving and the last diagnostic it
    /// published - so this is what says the device list and the diagnostic are stale rather
    /// than current. Cleared by the next successful scan.
    /// </summary>
    public string? LastScanError
    {
        get => _lastScanError;
        private set => _lastScanError = value;
    }

    public EvdevGamepadProvider()
        : this(LibcEvdevSystem.Shared, System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency)
    {
    }

    /// <summary>
    /// Test seam: the native calls and the clock are injectable, so the 250ms cadence, an
    /// open that stalls mid-scan and a queued event are all deterministic instead of timing
    /// races. Production uses the parameterless constructor's libc system and real stopwatch.
    /// </summary>
    internal EvdevGamepadProvider(IEvdevSystem system, Func<long> timestamp, long clockFrequency)
    {
        _system = system;
        _timestamp = timestamp;
        _clockFrequency = clockFrequency;
    }

    /// <summary>
    /// Test seam: the directory <see cref="GetConnected"/>'s rescan walks. Production leaves
    /// this at the live <c>/dev/input</c>; tests point it at a fixture tree, the same way
    /// <see cref="Scan(string)"/> and <see cref="ProbeOnce"/> already can.
    /// </summary>
    internal string DeviceDirectory { get; init; } = EvdevNodes.DirectoryPath;

    public IReadOnlyList<GamepadDescriptor> GetConnected()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return _connected;
            }

            var now = _timestamp();

            if (!_firstScanDone)
            {
                // The daemon's startup summary reads LastScanDiagnostic right after its first
                // GetConnected, so the very first scan runs on the calling thread and finishes
                // before this returns - the one documented exception to "no device I/O under
                // the list lock". Every scan after it is background work.
                _firstScanDone = true;
                _nextScanTimestamp = now + IntervalTicks;

                try
                {
                    Scan(DeviceDirectory, Volatile.Read(ref _generation));
                }
                catch (Exception exception)
                {
                    // Recorded before the rethrow so the failure is visible on the provider
                    // even when the caller is the daemon's startup path.
                    LastScanError = DescribeScanFailure(DeviceDirectory, exception);
                    throw;
                }

                return _connected;
            }

            if (now < _nextScanTimestamp)
            {
                return _connected;
            }

            // Due, and not the first scan: launch one background scan and return the cache
            // immediately. Single-flight - an in-flight scan keeps the slot busy, so a scan
            // slower than the interval cannot pile up behind it.
            _nextScanTimestamp = now + IntervalTicks;
            _scanTask ??= Task.Run(ScanWorker);
            return _connected;
        }
    }

    public IGamepadState? Poll(string deviceId)
    {
        lock (_sync)
        {
            var device = _devices.FirstOrDefault(d => string.Equals(d.Path, deviceId, StringComparison.Ordinal));
            if (device is null)
            {
                return null;
            }

            // The tracker's lock is per device. The reader holds it for one event batch and
            // the scan never takes it at all, so sampling waits on input application only -
            // never on enumeration or device I/O.
            lock (device.Sync)
            {
                return device.Tracker.Snapshot();
            }
        }
    }

    public void Dispose()
    {
        List<Device> devices;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // A scan in flight aborts at its next checkpoint and releases whatever it opened;
            // the generation bump is what tells it this provider is gone.
            _generation++;
            devices = [.. _devices];
            _devices.Clear();
            _connected = [];
        }

        // Stopped outside the lock: each stop waits for the device's reader thread, and that
        // wait must never be done while holding the lock every Poll takes.
        foreach (var device in devices)
        {
            Stop(device);
        }
    }

    /// <summary>
    /// One-shot diagnostic scan: opens every event node, classifies it and closes it again,
    /// keeping no readers and no devices. The GUI is a client of the daemon and owns no devices
    /// itself, so this is how it explains an empty device table with the same classification
    /// the daemon's own scan produces - same user, same nodes, same answer. Always the real
    /// libc system: it is the one-shot, one-thread probe the permission guidance is built on.
    /// </summary>
    /// <param name="deviceDirectory">
    /// Defaults to <c>/dev/input</c> on Linux. Tests point it at a fixture tree; passing a
    /// directory explicitly is also what makes the probe meaningful off Linux.
    /// </param>
    public static EvdevScanDiagnostic ProbeOnce(string? deviceDirectory = null)
    {
        var system = LibcEvdevSystem.Shared;
        var directory = deviceDirectory ?? EvdevNodes.DirectoryPath;
        var directoryExists = system.DirectoryExists(directory);

        // The probe owns no devices, so a directory it cannot list answers the same as an
        // empty one - "no nodes" with the guidance that points at the group fix, which is the
        // usual cause. Only the scan needs the failure-aware form, because only the scan has
        // devices to lose.
        var nodes = system.EnumerateEventNodes(directory).Nodes;

        var nodeCount = 0;
        var deniedCount = 0;
        var gamepadCount = 0;
        var unhandledAxes = new List<EvdevUnhandledAxis>();
        string? firstDeniedPath = null;
        int? firstErrno = null;

        foreach (var path in nodes)
        {
            nodeCount++;

            switch (TryOpen(system, path, out var device, out var errno))
            {
                case EvdevOpenResult.Opened:
                    gamepadCount++;
                    unhandledAxes.AddRange(device.UnhandledAxes);

                    // The probe owns no readers, so the descriptor it just built is released
                    // immediately; only the classification survives.
                    system.Close(device.Fd);
                    device.Fd = -1;
                    break;

                case EvdevOpenResult.AccessDenied:
                    deniedCount++;
                    firstDeniedPath ??= path;
                    firstErrno ??= errno;
                    break;
            }
        }

        return BuildDiagnostic(directoryExists, nodeCount, deniedCount, gamepadCount, firstDeniedPath, firstErrno, unhandledAxes);
    }

    /// <summary>
    /// The scan itself, over <paramref name="deviceDirectory"/> instead of the live
    /// <c>/dev/input</c>: tests point it at a fixture tree, the same way
    /// <see cref="ProbeOnce"/> does.
    /// </summary>
    internal void Scan(string deviceDirectory) => Scan(deviceDirectory, Volatile.Read(ref _generation));

    /// <summary>
    /// One walk of <paramref name="deviceDirectory"/>: open what is not served yet, stop what
    /// vanished, and publish the diagnostic. The directory walk and every open/ioctl happen
    /// with no lock held at all - the served set is snapshotted once up front, so a walk over
    /// hundreds of nodes never hands the list lock back and forth under a waiting
    /// <see cref="Poll"/>. The lock is taken again only to add a newly opened device and to
    /// run the removal pass.
    /// <paramref name="generation"/> is the provider generation this walk belongs to: a
    /// Dispose during the walk bumps it, and the walk then releases whatever it opened and
    /// stops without publishing devices nobody will poll.
    /// </summary>
    private void Scan(string deviceDirectory, int generation)
    {
        var directoryExists = _system.DirectoryExists(deviceDirectory);
        var enumeration = _system.EnumerateEventNodes(deviceDirectory);

        if (enumeration.Failure is { } failure)
        {
            // The scan could not even list the directory, so it knows nothing about presence -
            // and "nothing" is exactly what it must not conclude. Reading a failed listing as
            // an empty one would run the removal pass over every served device: readers
            // stopped, pads unlisted, and with them every held button released, all because
            // one transient failure made /dev/input unreadable. Keep the devices, keep the
            // last diagnostic, record why the state is stale; the next due scan retries.
            LastScanError = DescribeEnumerationFailure(deviceDirectory, failure);
            return;
        }

        LastScanError = null;
        var nodes = enumeration.Nodes;
        var present = new HashSet<string>(StringComparer.Ordinal);

        Dictionary<string, Device> served;
        lock (_sync)
        {
            served = _devices.ToDictionary(d => d.Path, StringComparer.Ordinal);
        }

        var nodeCount = 0;
        var deniedCount = 0;
        var gamepadCount = 0;
        var unhandledAxes = new List<EvdevUnhandledAxis>();
        string? firstDeniedPath = null;
        int? firstErrno = null;

        foreach (var path in nodes)
        {
            nodeCount++;
            present.Add(path);

            if (served.TryGetValue(path, out var existing))
            {
                // Already served: it passed the capability check when it was opened, so it
                // still counts as a gamepad even though this scan opens nothing - and its
                // axis diagnostic carries over, so a re-scan (hot-plug, or the periodic
                // GetConnected refresh) does not report a served pad as having no
                // unmapped axes.
                gamepadCount++;
                unhandledAxes.AddRange(existing.UnhandledAxes);
                continue;
            }

            switch (TryOpen(_system, path, out var device, out var errno))
            {
                case EvdevOpenResult.Opened:
                    lock (_sync)
                    {
                        if (_disposed || Volatile.Read(ref _generation) != generation)
                        {
                            // The provider went away while this node was open: release the
                            // descriptor and stop walking instead of serving a device nobody
                            // will ever poll.
                            CloseFd(device);
                            return;
                        }

                        if (_devices.Any(d => string.Equals(d.Path, path, StringComparison.Ordinal)))
                        {
                            // Registered as served while this walk was already running (the
                            // test seam does exactly that): keep the registration, release
                            // the duplicate this walk opened.
                            CloseFd(device);
                            break;
                        }

                        _devices.Add(device);
                        RebuildConnectedLocked();

                        // Started under the same lock section as the add so a concurrent
                        // Dispose either sees the device fully started or never sees it at
                        // all - never a started reader on a descriptor it already closed.
                        StartReader(device);
                    }

                    gamepadCount++;
                    unhandledAxes.AddRange(device.UnhandledAxes);
                    break;

                case EvdevOpenResult.AccessDenied:
                    deniedCount++;
                    firstDeniedPath ??= path;
                    firstErrno ??= errno;
                    break;
            }
        }

        List<Device> vanished;
        lock (_sync)
        {
            vanished = [];
            for (var i = _devices.Count - 1; i >= 0; i--)
            {
                if (!present.Contains(_devices[i].Path))
                {
                    vanished.Add(_devices[i]);
                    _devices.RemoveAt(i);
                }
            }

            if (vanished.Count > 0)
            {
                RebuildConnectedLocked();
            }
        }

        foreach (var device in vanished)
        {
            Stop(device);
        }

        if (Volatile.Read(ref _generation) == generation && !_disposed)
        {
            LastScanDiagnostic = BuildDiagnostic(
                directoryExists, nodeCount, deniedCount, gamepadCount, firstDeniedPath, firstErrno, unhandledAxes);
        }
    }

    /// <summary>
    /// The body of one background scan. A scan that throws must not take the daemon down:
    /// the readers already serving the connected pads keep running untouched, and the next
    /// due scan retries the walk. Whatever the scan learned before the throw stands - nothing
    /// is retired on a failed scan. Clearing the slot in the finally is safe because launches
    /// are single-flight under <see cref="_sync"/>: while this worker is alive the slot stays
    /// busy, so no second worker can be running to clear on its behalf.
    /// </summary>
    private void ScanWorker()
    {
        try
        {
            Scan(DeviceDirectory, Volatile.Read(ref _generation));
        }
        catch (Exception exception)
        {
            // Same rule as a failed enumeration: the scan learned nothing, so nothing moves.
            LastScanError = DescribeScanFailure(DeviceDirectory, exception);
        }
        finally
        {
            lock (_sync)
            {
                _scanTask = null;
            }
        }
    }

    private static string DescribeEnumerationFailure(string deviceDirectory, Exception failure)
        => $"枚举 {deviceDirectory} 失败（保留当前设备与上次诊断）：{failure.GetType().Name}: {failure.Message}";

    private static string DescribeScanFailure(string deviceDirectory, Exception failure)
        => $"扫描 {deviceDirectory} 异常（保留当前设备与上次诊断）：{failure.GetType().Name}: {failure.Message}";

    /// <summary>
    /// Test seam: registers a device as already served, the way a previous scan's open left
    /// it, so a test can drive the "already served" branch of <see cref="Scan(string)"/>.
    /// A fixture tree cannot open as a gamepad on its own - regular files answer every
    /// evdev ioctl with ENOTTY - so without this the re-scan path is unreachable off a real
    /// kernel with a real pad attached. No reader thread is started; the scan only reads
    /// the device's path and its diagnostic.
    /// </summary>
    internal void RegisterServedDevice(Device device)
    {
        lock (_sync)
        {
            _devices.Add(device);
            RebuildConnectedLocked();
        }
    }

    /// <summary>
    /// Test seam: waits for the background scan (if any) to finish, so a test can assert on
    /// the state a completed async scan published without sleeping for the interval. Returns
    /// false when no scan finished within <paramref name="timeout"/>.
    /// </summary>
    internal bool WaitForScanCompletion(TimeSpan timeout)
    {
        Task? scan;

        lock (_sync)
        {
            scan = _scanTask;
        }

        return scan is null || scan.Wait(timeout);
    }

    private long IntervalTicks => (long)(RescanInterval.TotalSeconds * _clockFrequency);

    private void RebuildConnectedLocked()
        => _connected = [.. _devices.Select(d => new GamepadDescriptor(d.Path, d.Name) { ButtonCount = d.ButtonCount })];

    private static EvdevScanDiagnostic BuildDiagnostic(
        bool directoryExists,
        int nodeCount,
        int deniedCount,
        int gamepadCount,
        string? firstDeniedPath,
        int? firstErrno,
        List<EvdevUnhandledAxis> unhandledAxes)
        => new()
        {
            Outcome = EvdevScanDiagnostic.ClassifyOutcome(directoryExists, nodeCount, deniedCount, gamepadCount),
            NodeCount = nodeCount,
            DeniedNodeCount = deniedCount,
            GamepadCount = gamepadCount,
            FirstDeniedPath = firstDeniedPath,
            Errno = firstErrno,
            UnhandledAxes = unhandledAxes,
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

    private static EvdevOpenResult TryOpen(IEvdevSystem system, string path, out Device device, out int errno)
    {
        device = null!;
        errno = 0;

        var fd = system.Open(path, EvdevNative.ORdOnly | EvdevNative.ONonBlock);
        if (fd < 0)
        {
            // File.Exists used to swallow this: a node the user cannot open is not an absent
            // node, and the two need opposite fixes.
            errno = Marshal.GetLastPInvokeError();
            return errno == EvdevNative.ErrnoAccessDenied ? EvdevOpenResult.AccessDenied : EvdevOpenResult.OpenFailed;
        }

        try
        {
            var name = ReadDeviceName(system, fd);
            if (!LooksLikeGamepad(system, fd))
            {
                system.Close(fd);
                return EvdevOpenResult.NotAGamepad;
            }

            var ranges = ReadAxisRanges(system, fd);

            device = new Device
            {
                Path = path,
                Name = name,
                Tracker = new EvdevGamepadStateTracker(HandledAxisRanges(ranges)),
                ButtonCount = ReadButtonCount(system, fd),
                UnhandledAxes = FindUnhandledAxes(path, ranges),
                Fd = fd,
            };

            return EvdevOpenResult.Opened;
        }
        catch
        {
            system.Close(fd);
            throw;
        }
    }

    /// <summary>
    /// Narrows the device's full axis table to the codes the tracker maps, so the tracker
    /// sees exactly its own axes and nothing else.
    /// </summary>
    private static Dictionary<ushort, EvdevAxisRange> HandledAxisRanges(Dictionary<ushort, EvdevAxisRange> ranges)
        => ranges.Where(pair => EvdevGamepadStateTracker.IsHandledAbsCode(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>
    /// The ABS axes this device reports that the tracker does not map - the raw codes and
    /// declared ranges a user can line up against <c>evtest</c> output. An axis that arrives
    /// here is one the app can never light up, which is otherwise indistinguishable from a
    /// broken stick; listing it turns "nothing happens" into a code to search for. Pure over
    /// the range table so both directions are pinned by tests.
    /// </summary>
    internal static IReadOnlyList<EvdevUnhandledAxis> FindUnhandledAxes(
        string devicePath,
        IReadOnlyDictionary<ushort, EvdevAxisRange> ranges)
        => ranges.Where(pair => !EvdevGamepadStateTracker.IsHandledAbsCode(pair.Key))
            .OrderBy(pair => pair.Key)
            .Select(pair => new EvdevUnhandledAxis(pair.Key, pair.Value.Min, pair.Value.Max, pair.Value.Rest, devicePath))
            .ToList();

    private static string ReadDeviceName(IEvdevSystem system, int fd)
    {
        Span<byte> buffer = stackalloc byte[256];

        if (system.Ioctl(fd, EvdevNative.EvIocGName, buffer) < 0)
        {
            return "未知手柄";
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
    private static bool LooksLikeGamepad(IEvdevSystem system, int fd)
    {
        Span<byte> absBits = stackalloc byte[8];

        if (system.Ioctl(fd, EvdevNative.EvIocGBitAbs, absBits) < 0)
        {
            return false;
        }

        Span<byte> keyBits = stackalloc byte[EvdevNative.KeyBitBufferSize];

        if (system.Ioctl(fd, EvdevNative.EvIocGBitKey, keyBits) < 0)
        {
            return false;
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
    internal static int? ReadButtonCount(int fd) => ReadButtonCount(LibcEvdevSystem.Shared, fd);

    internal static int? ReadButtonCount(IEvdevSystem system, int fd)
    {
        Span<byte> bits = stackalloc byte[EvdevNative.KeyBitBufferSize];

        if (system.Ioctl(fd, EvdevNative.EvIocGBitKey, bits) < 0)
        {
            return null;
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

    /// <summary>
    /// Reads EVIOCGABS for every absolute axis the device declares, keeping each axis's
    /// current value as its seeded rest. Walking the EVIOCGBIT(EV_ABS) mask instead of a
    /// fixed code list is what lets an unrecognized axis be reported with its real range,
    /// and what keeps the rest value that used to be dropped on the floor.
    /// </summary>
    private static Dictionary<ushort, EvdevAxisRange> ReadAxisRanges(IEvdevSystem system, int fd)
    {
        var ranges = new Dictionary<ushort, EvdevAxisRange>();
        Span<byte> absBits = stackalloc byte[8];

        if (system.Ioctl(fd, EvdevNative.EvIocGBitAbs, absBits) < 0)
        {
            return ranges;
        }

        var absMask = BinaryPrimitives.ReadUInt64LittleEndian(absBits);

        for (ushort axis = 0; axis < 64; axis++)
        {
            if ((absMask & (1UL << axis)) == 0)
            {
                continue;
            }

            var info = new InputAbsinfo();
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref info, 1));

            if (system.Ioctl(fd, EvdevNative.EvIocGAbs | axis, bytes) < 0)
            {
                continue;
            }

            ranges[axis] = new EvdevAxisRange(info.Minimum, info.Maximum, info.Value);
        }

        return ranges;
    }

    private void StartReader(Device device)
    {
        device.StopFd = _system.EventFd(0, EvdevNative.EventFdNonBlock | EvdevNative.EventFdCloseOnExec);
        device.Running = true;
        device.Reader = new Thread(() => ReadLoop(device))
        {
            IsBackground = true,
            Name = $"evdev-reader-{device.Path}",
        };
        device.Reader.Start();
    }

    private void ReadLoop(Device device)
    {
        Span<byte> buffer = stackalloc byte[EvdevNative.EventSize * 32];
        Span<PollFd> descriptors = stackalloc PollFd[2];

        try
        {
            while (device.Running)
            {
                descriptors[0] = new PollFd { Fd = device.Fd, Events = EvdevNative.PollIn };
                descriptors[1] = new PollFd
                {
                    Fd = device.StopFd,
                    Events = EvdevNative.PollIn,
                };
                var count = device.StopFd >= 0 ? 2 : 1;
                var ready = _system.Poll(descriptors[..count], device.StopFd >= 0 ? -1 : 250);

                if (!device.Running)
                {
                    return;
                }

                if (ready < 0)
                {
                    if (Marshal.GetLastPInvokeError() == EvdevNative.ErrnoInterrupted)
                    {
                        continue;
                    }

                    Remove(device);
                    return;
                }

                if ((descriptors[0].ReturnedEvents & (EvdevNative.PollError | EvdevNative.PollHangUp | EvdevNative.PollInvalid)) != 0)
                {
                    Remove(device);
                    return;
                }

                if ((descriptors[0].ReturnedEvents & EvdevNative.PollIn) == 0)
                {
                    continue;
                }

                var read = _system.Read(device.Fd, buffer);

                if (read > 0)
                {
                    var events = (int)read / EvdevNative.EventSize;

                    // The per-device lock, not the provider's: a scan in flight holds the list
                    // lock for list work only, so events keep merging into the tracker while
                    // /dev/input is being walked.
                    lock (device.Sync)
                    {
                        for (var i = 0; i < events; i++)
                        {
                            device.Tracker.Apply(EvdevEventReader.Read(buffer, i));
                        }
                    }

                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (read < 0 && error is EvdevNative.ErrnoInterrupted or EvdevNative.ErrnoTryAgain)
                {
                    continue;
                }

                Remove(device);
                return;
            }
        }
        finally
        {
            // Belt and braces: every exit above is covered by a Stop (this thread's own error
            // path, or the stopping thread's join), and CloseFd is exactly-once, so this only
            // matters if the loop ever dies to an unexpected exception - the descriptors must
            // not outlive the reader either way.
            CloseFd(device);
        }
    }

    private void Remove(Device device)
    {
        lock (_sync)
        {
            if (_devices.Remove(device))
            {
                RebuildConnectedLocked();
            }
        }

        // Outside the lock: Stop waits for this device's reader thread (a no-op when the
        // reader itself is the caller), and that wait must never be done while holding the
        // lock every Poll takes.
        Stop(device);
    }

    /// <summary>
    /// Retires one device: wakes its reader, waits for the reader to exit, then closes its
    /// descriptors - in that order, always. The wait is what makes the close safe: a reader
    /// still inside read() on a descriptor this method closed could have the freed number
    /// handed to the next open (the next scan, the next reader's eventfd) and would then
    /// read a different device's events. The exchange makes each close exactly once, so a
    /// scan's removal pass and a reader's own error path stopping the same device at the
    /// same time cannot double-close or close a reused number.
    /// </summary>
    private void Stop(Device device)
    {
        device.Running = false;

        var stopFd = Interlocked.Exchange(ref device.StopFd, -1);
        if (stopFd >= 0)
        {
            _system.SignalEventFd(stopFd);
        }

        var reader = device.Reader;
        if (reader is not null && !ReferenceEquals(reader, Thread.CurrentThread))
        {
            reader.Join(ReaderStopTimeout);
        }

        // The stop eventfd this call claimed, closed after the reader it woke has exited -
        // and only by the caller that claimed it, so it closes exactly once.
        if (stopFd >= 0)
        {
            _system.Close(stopFd);
        }

        CloseFd(device);
    }

    /// <summary>
    /// Closes a device's descriptors exactly once, whichever path stopped it.
    /// </summary>
    private void CloseFd(Device device)
    {
        var fd = Interlocked.Exchange(ref device.Fd, -1);
        if (fd >= 0)
        {
            _system.Close(fd);
        }

        var stopFd = Interlocked.Exchange(ref device.StopFd, -1);
        if (stopFd >= 0)
        {
            _system.Close(stopFd);
        }
    }

    internal sealed class Device
    {
        public required string Path { get; init; }

        public required string Name { get; init; }

        public required EvdevGamepadStateTracker Tracker { get; init; }

        public int? ButtonCount { get; init; }

        /// <summary>ABS axes the tracker does not map, with raw codes and ranges (may be empty).</summary>
        public required IReadOnlyList<EvdevUnhandledAxis> UnhandledAxes { get; init; }

        /// <summary>
        /// The device descriptor, or -1 once closed. Only ever touched through
        /// <see cref="Interlocked.Exchange(ref int)"/> so exactly one path closes it.
        /// </summary>
        public int Fd = -1;

        /// <summary>
        /// The reader's stop eventfd, or -1 once consumed. Same single-owner rule as
        /// <see cref="Fd"/>.
        /// </summary>
        public int StopFd = -1;

        public volatile bool Running;

        public Thread? Reader { get; set; }

        /// <summary>
        /// The tracker's lock, one per device: the reader applies event batches under it and
        /// Poll takes snapshots under it. The scan and the connected-list operations never
        /// take it, so neither enumeration nor another device's reader can stall sampling or
        /// event application for this one.
        /// </summary>
        public Lock Sync { get; } = new();
    }
}
