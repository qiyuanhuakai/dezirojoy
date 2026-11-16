using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeziroJoy.Platform.Linux.Evdev;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// A controllable stand-in for the libc calls the evdev provider makes, so the scan, the
/// reader threads and the descriptor lifecycle can be driven deterministically: an open that
/// stalls on a gate, an event queued into a node, a descriptor number handed back out after a
/// close exactly the way the kernel reuses it. Every call is recorded in <see cref="Log"/>,
/// and the two races the provider must not commit - reading a descriptor after closing it,
/// closing one twice - are recorded in <see cref="Violations"/> instead of passing silently.
/// </summary>
internal sealed class FakeEvdevSystem : IEvdevSystem
{
    private const int ErrnoNoEntry = 2;
    private const int ErrnoBadFile = 9;
    private const int ErrnoAgain = 11;
    private const int ErrnoAccessDenied = 13;
    private const int ErrnoNotTypeTty = 25;

    /// <summary>
    /// Upper bound the fake poll waits even when the caller asks to wait forever, so a bug in
    /// the provider fails a test instead of hanging the run. The reader simply polls again.
    /// </summary>
    private const int MaxPollWaitMs = 30_000;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, FakeNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<string> _enumerated = [];
    private readonly Dictionary<int, FakeDescriptor> _open = [];
    private readonly List<string> _log = [];
    private readonly List<string> _violations = [];
    private bool _directoryExists = true;

    public bool DirectoryExists(string directory) => _directoryExists;

    public EvdevEnumeration EnumerateEventNodes(string directory)
    {
        lock (_sync)
        {
            Record("enumerate");

            if (ThrowOnNextEnumerate is { } thrown)
            {
                ThrowOnNextEnumerate = null;
                throw thrown;
            }

            if (NextEnumerationFailure is { } failure)
            {
                NextEnumerationFailure = null;
                return new EvdevEnumeration([], failure);
            }

            return new EvdevEnumeration([.. _enumerated], null);
        }
    }

    public int Open(string path, int flags)
    {
        FakeNode node;

        lock (_sync)
        {
            Record($"open:{path}");

            if (!_nodes.TryGetValue(path, out var found))
            {
                Marshal.SetLastPInvokeError(ErrnoNoEntry);
                return -1;
            }

            if (found.OpenErrno is int denied)
            {
                Marshal.SetLastPInvokeError(denied);
                return -1;
            }

            node = found;
        }

        // Slow device I/O, deterministically: the caller (the scan) blocks here until the
        // test releases the gate. Deliberately outside the lock - a stalled open must not
        // block the fake's other calls, or a test reading the log to see where the scan
        // reached would deadlock against the very stall it is observing.
        node.OpenGate?.Task.Wait();

        lock (_sync)
        {
            var fd = AllocateFdLocked();
            node.OpenFd = fd;
            _open[fd] = new FakeNodeDescriptor(node);
            Record($"opened:{path}={fd}");

            return fd;
        }
    }

    public int Close(int fd)
    {
        lock (_sync)
        {
            Record($"close:{fd}");

            if (!_open.Remove(fd, out var descriptor))
            {
                // Closing a descriptor that is not open: a double close, or a close of a
                // number the kernel has already handed to somebody else.
                _violations.Add($"close of unopened fd {fd}");
                return -1;
            }

            // The wait handle is deliberately left alive: a reader still blocked in poll()
            // on this number (the race this fake exists to catch) must observe a usable
            // handle rather than an ObjectDisposedException.
            return 0;
        }
    }

    public int Ioctl(int fd, uint request, Span<byte> argument)
    {
        lock (_sync)
        {
            if (!_open.TryGetValue(fd, out var descriptor) || descriptor is not FakeNodeDescriptor nodeDescriptor)
            {
                Marshal.SetLastPInvokeError(ErrnoNotTypeTty);
                return -1;
            }

            var node = nodeDescriptor.Node;
            Record($"ioctl:{fd}:{request:x8}");

            if (request == EvdevNative.EvIocGName)
            {
                WriteName(argument, node.Name);
                return 0;
            }

            if (request == EvdevNative.EvIocGBitAbs)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(argument, node.AbsMask);
                return 0;
            }

            if (request == EvdevNative.EvIocGBitKey)
            {
                node.KeyMask.AsSpan().CopyTo(argument);
                return 0;
            }

            if (request >= EvdevNative.EvIocGAbs && request < EvdevNative.EvIocGAbs + 64)
            {
                var axis = (ushort)(request - EvdevNative.EvIocGAbs);
                if (!node.Ranges.TryGetValue(axis, out var range))
                {
                    Marshal.SetLastPInvokeError(ErrnoNotTypeTty);
                    return -1;
                }

                var info = new InputAbsinfo { Value = range.Value, Minimum = range.Min, Maximum = range.Max };
                MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref info, 1)).CopyTo(argument);
                return 0;
            }

            Marshal.SetLastPInvokeError(ErrnoNotTypeTty);
            return -1;
        }
    }

    public nint Read(int fd, Span<byte> buffer)
    {
        lock (_sync)
        {
            if (!_open.TryGetValue(fd, out var descriptor) || descriptor is not FakeNodeDescriptor nodeDescriptor)
            {
                // A read on a descriptor that is not open is the fd-reuse race catching up
                // with the provider: the number was closed, freed and possibly handed out
                // again while a reader was still using it.
                _violations.Add($"read of unopened fd {fd}");
                Marshal.SetLastPInvokeError(ErrnoBadFile);
                return -1;
            }

            var node = nodeDescriptor.Node;
            Record($"read:{fd}");

            if (node.ReadErrno is int readErrno)
            {
                // A dead device: the read fails with a fatal errno (ENODEV, EIO) instead of
                // returning data - the shape of a pad that vanished between the poll and the
                // read. EAGAIN and EINTR would be retried; anything else retires the device.
                Marshal.SetLastPInvokeError(readErrno);
                return -1;
            }

            var total = 0;
            while (total + EvdevNative.EventSize <= buffer.Length && node.Pending.Count > 0)
            {
                node.Pending.Dequeue().CopyTo(buffer.Slice(total));
                total += EvdevNative.EventSize;
            }

            if (total == 0)
            {
                Marshal.SetLastPInvokeError(ErrnoAgain);
                return -1;
            }

            return total;
        }
    }

    public int Poll(Span<PollFd> descriptors, int timeoutMilliseconds)
    {
        // Condition-spun rather than wait-handle driven: the fake must tolerate a reader
        // still blocked in here when the descriptor it polls gets closed (the race this fake
        // exists to catch), and a disposed handle would turn that into an
        // ObjectDisposedException on a background thread instead of a recorded violation.
        // The 1ms check interval is the wake latency a test observes; the kernel's poll is
        // event-driven, and no test depends on sub-millisecond wakeups.
        var timeout = timeoutMilliseconds < 0 ? MaxPollWaitMs : timeoutMilliseconds;
        var deadline = Stopwatch.StartNew();

        while (true)
        {
            Record($"poll:{string.Join(",", descriptors.ToArray().Select(descriptor => descriptor.Fd))}");

            FakeNode? gated = null;
            lock (_sync)
            {
                foreach (var descriptor in descriptors)
                {
                    if (_open.TryGetValue(descriptor.Fd, out var open) &&
                        open is FakeNodeDescriptor nodeDescriptor &&
                        nodeDescriptor.Node.PollGate is not null)
                    {
                        gated = nodeDescriptor.Node;
                    }
                }
            }

            if (gated?.PollGate is { } gate)
            {
                // A gated node parks the reader here, before any result is returned: the test
                // stages the reader mid-poll and releases it on command. The wait is outside
                // the lock - a stalled poll must not block the fake's other calls.
                Record($"poll-gate:{gated.OpenFd}");
                gate.Task.Wait();
            }

            var ready = 0;
            lock (_sync)
            {
                for (var i = 0; i < descriptors.Length; i++)
                {
                    descriptors[i].ReturnedEvents = 0;

                    if (!_open.TryGetValue(descriptors[i].Fd, out var descriptor))
                    {
                        // The kernel reports POLLNVAL for a closed descriptor still in the
                        // set; the provider treats that as a dead device and stops it.
                        descriptors[i].ReturnedEvents = EvdevNative.PollInvalid;
                        ready++;
                        continue;
                    }

                    if (descriptor is FakeNodeDescriptor node)
                    {
                        // Injected error conditions, sticky like the kernel's: once set they
                        // are reported on every poll until the descriptor is closed.
                        if (node.Node.PollErrorMask != 0)
                        {
                            descriptors[i].ReturnedEvents |= node.Node.PollErrorMask;
                            ready++;
                        }
                    }

                    if (descriptor.IsReady)
                    {
                        descriptors[i].ReturnedEvents |= EvdevNative.PollIn;
                        ready++;
                    }
                }
            }

            if (ready > 0)
            {
                return ready;
            }

            if (deadline.ElapsedMilliseconds >= timeout)
            {
                return 0;
            }

            Thread.Sleep(1);
        }
    }

    public int EventFd(uint initialValue, int flags)
    {
        lock (_sync)
        {
            var fd = AllocateFdLocked();
            _open[fd] = new FakeEventFdDescriptor();
            Record($"eventfd:{fd}");

            return fd;
        }
    }

    public void SignalEventFd(int fd)
    {
        Record($"signal:{fd}");

        // The gate parks the caller - a Stop from the reader's own error path or from the
        // scan's removal pass - inside the signal, so a test can hold two retirements of the
        // same device overlapping. The wait is outside the lock for the same reason the
        // poll gate's is.
        SignalGate?.Task.Wait();

        lock (_sync)
        {
            if (_open.TryGetValue(fd, out var descriptor) && descriptor is FakeEventFdDescriptor eventFd)
            {
                eventFd.Signaled = true;
            }
        }
    }

    /// <summary>Every call the fake has served, in order.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_sync)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>
    /// The races that must never happen: a read or a close touching a descriptor that is not
    /// open. Empty for a correct provider.
    /// </summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (_sync)
            {
                return [.. _violations];
            }
        }
    }

    /// <summary>How many descriptors the fake currently has open.</summary>
    public int OpenDescriptorCount
    {
        get
        {
            lock (_sync)
            {
                return _open.Count;
            }
        }
    }

    /// <summary>How many directory enumerations the provider has asked for.</summary>
    public int EnumerateCount
    {
        get
        {
            lock (_sync)
            {
                return _log.Count(entry => entry == "enumerate");
            }
        }
    }

    public void SetDirectoryExists(bool exists)
    {
        lock (_sync)
        {
            _directoryExists = exists;
        }
    }

    /// <summary>
    /// Configures a gamepad-shaped node: a dual stick, the canonical ranges, and an optional
    /// name. Returns the node so the test can gate its open, deny it or queue events into it.
    /// </summary>
    public FakeNode AddGamepadNode(string path, string name = "Fake Pad")
    {
        var node = ConfigureGamepadNode(path, name);
        PlugIn(path);

        return node;
    }

    /// <summary>
    /// Configures a node without plugging it in: the next scan will not see it until
    /// <see cref="PlugIn"/> runs, which is how a test stages a hot-plug (or a node whose
    /// open is gated) between two scans.
    /// </summary>
    public FakeNode ConfigureGamepadNode(string path, string name = "Fake Pad")
        => ConfigureNode(path, name, DualStickAbsMask, EmptyKeyMask, CanonicalRanges());

    public FakeNode ConfigureNode(string path, string name, ulong absMask, byte[] keyMask, Dictionary<ushort, FakeAxisRange> ranges)
    {
        lock (_sync)
        {
            var node = new FakeNode(name, absMask, keyMask, ranges);
            _nodes[path] = node;

            return node;
        }
    }

    /// <summary>Hot-plug in: the node joins the next enumeration.</summary>
    public void PlugIn(string path)
    {
        lock (_sync)
        {
            if (!_enumerated.Contains(path))
            {
                _enumerated.Add(path);
            }
        }
    }

    /// <summary>Hot-plug out: the node leaves the enumeration, keeping its configuration.</summary>
    public void Unplug(string path)
    {
        lock (_sync)
        {
            _enumerated.Remove(path);
        }
    }

    /// <summary>When set, the next enumeration throws this and clears it - a failing scan.</summary>
    public Exception? ThrowOnNextEnumerate { get; set; }

    /// <summary>
    /// When set, the next enumeration reports this failure instead of the node list, the way
    /// the real <see cref="EvdevNodes"/> reports a directory it could not list. Cleared once
    /// delivered, so the scan after it succeeds again.
    /// </summary>
    public Exception? NextEnumerationFailure { get; set; }

    /// <summary>
    /// When set, <see cref="SignalEventFd"/> parks on it until the test completes it, so two
    /// retirements of the same device can be held overlapping from different threads.
    /// </summary>
    public TaskCompletionSource? SignalGate { get; set; }

    /// <summary>True once an open of <paramref name="path"/> has been attempted.</summary>
    public bool ReachedOpen(string path)
    {
        lock (_sync)
        {
            return _log.Contains($"open:{path}");
        }
    }

    /// <summary>The descriptor number the fake last handed out for <paramref name="path"/>.</summary>
    public int? FdOf(string path)
    {
        lock (_sync)
        {
            return _nodes.TryGetValue(path, out var node) ? node.OpenFd : null;
        }
    }

    private static readonly ulong DualStickAbsMask =
        (1UL << EvdevCodes.AbsX) | (1UL << EvdevCodes.AbsY) | (1UL << EvdevCodes.AbsRx) | (1UL << EvdevCodes.AbsRy);

    private static byte[] EmptyKeyMask => new byte[EvdevNative.KeyBitBufferSize];

    private static Dictionary<ushort, FakeAxisRange> CanonicalRanges() => new()
    {
        [EvdevCodes.AbsX] = new FakeAxisRange(-32768, 32767, 0),
        [EvdevCodes.AbsY] = new FakeAxisRange(-32768, 32767, 0),
        [EvdevCodes.AbsRx] = new FakeAxisRange(-32768, 32767, 0),
        [EvdevCodes.AbsRy] = new FakeAxisRange(-32768, 32767, 0),
    };

    private static void WriteName(Span<byte> argument, string name)
    {
        argument.Clear();
        Encoding.UTF8.GetBytes(name).CopyTo(argument);
    }

    private int AllocateFdLocked()
    {
        // The kernel hands out the lowest free number, so a descriptor freed by a close is
        // the next one an open returns - exactly the reuse the provider must not race.
        var fd = 3;
        while (_open.ContainsKey(fd))
        {
            fd++;
        }

        return fd;
    }

    private void Record(string entry)
    {
        // Called from the provider's threads as well as the test thread, and sometimes from
        // outside the sections that already hold the lock, so it takes the lock itself.
        lock (_sync)
        {
            _log.Add(entry);
        }
    }

    public sealed record FakeAxisRange(int Min, int Max, int Value);
}

/// <summary>
/// One fake evdev node: what it reports through the ioctls, how its open behaves, and the
/// event records a reader will get from it.
/// </summary>
internal sealed class FakeNode
{
    public FakeNode(string name, ulong absMask, byte[] keyMask, Dictionary<ushort, FakeEvdevSystem.FakeAxisRange> ranges)
    {
        Name = name;
        AbsMask = absMask;
        KeyMask = keyMask;
        Ranges = ranges;
    }

    public string Name { get; }

    public ulong AbsMask { get; }

    public byte[] KeyMask { get; }

    public Dictionary<ushort, FakeEvdevSystem.FakeAxisRange> Ranges { get; }

    /// <summary>When set, open() fails with this errno (13 = EACCES, the permission wall).</summary>
    public int? OpenErrno { get; set; }

    /// <summary>When set, open() blocks on this gate until the test completes it - a slow device,
    /// deterministically, with no timing involved. Null (the default) opens immediately.
    /// </summary>
    public TaskCompletionSource? OpenGate { get; set; }

    /// <summary>
    /// Error conditions poll() reports for this node (POLLERR, POLLHUP), sticky like the
    /// kernel's: once set, every poll reports them until the descriptor is closed. The reader
    /// treats them as a dead device and retires it from its own thread.
    /// </summary>
    public short PollErrorMask { get; set; }

    /// <summary>When set, read() fails with this errno instead of returning data.</summary>
    public int? ReadErrno { get; set; }

    /// <summary>
    /// When set, poll() parks on it before returning any result, so a test can hold a reader
    /// mid-poll and release it on command.
    /// </summary>
    public TaskCompletionSource? PollGate { get; set; }

    /// <summary>The descriptor number this node was last opened as, or null if never opened.</summary>
    public int? OpenFd { get; set; }

    /// <summary>Serialized 24-byte input_event records waiting to be read.</summary>
    public Queue<byte[]> Pending { get; } = [];

    /// <summary>Queues one event record the way the kernel would deliver it.</summary>
    public void EnqueueEvent(ushort type, ushort code, int value)
    {
        var record = new byte[EvdevNative.EventSize];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), type);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), code);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), value);
        Pending.Enqueue(record);
    }
}

internal abstract class FakeDescriptor
{
    public abstract bool IsReady { get; }
}

internal sealed class FakeNodeDescriptor : FakeDescriptor
{
    public FakeNodeDescriptor(FakeNode node) => Node = node;

    public FakeNode Node { get; }

    /// <summary>A node is readable exactly while it has records waiting, like evdev.</summary>
    public override bool IsReady => Node.Pending.Count > 0;
}

internal sealed class FakeEventFdDescriptor : FakeDescriptor
{
    public bool Signaled { get; set; }

    /// <summary>
    /// Sticky like the kernel's eventfd counter: the provider writes the stop signal and
    /// never reads it back, so once signaled every later poll reports it readable.
    /// </summary>
    public override bool IsReady => Signaled;
}

/// <summary>
/// A clock the tests advance by hand, so the 250ms rescan cadence is exact and instant
/// instead of a sleep.
/// </summary>
internal sealed class FakeClock
{
    private long _now;

    public static long Frequency => System.Diagnostics.Stopwatch.Frequency;

    public long Now() => Volatile.Read(ref _now);

    public void Advance(TimeSpan delta) => Interlocked.Add(ref _now, (long)(delta.TotalSeconds * Frequency));

    public void AdvanceMilliseconds(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));
}
