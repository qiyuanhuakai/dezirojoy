using System.Diagnostics;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The failure boundaries of the evdev provider, in the two directions that matter in
/// production:
/// <para>
/// A scan that learns nothing must retire nothing. The directory listing used to swallow an
/// unreadable /dev/input into an empty list, and the scan read that as "nothing is plugged
/// in": every reader stopped, every pad was unlisted, and every held button the daemon was
/// injecting was released - silently, once the scan moved off the calling thread. A failed
/// enumeration is now distinct from an empty directory, and a failed scan keeps the served
/// devices, the last diagnostic, and records <see cref="EvdevGamepadProvider.LastScanError"/>.
/// </para>
/// <para>
/// A device whose reader retires it - POLLERR, POLLHUP, a fatal read - races the scan's
/// removal pass stopping the same device. Both paths close descriptors; exactly one close per
/// descriptor may happen, and no reader may touch a descriptor after it is closed, because
/// the kernel hands the freed number to the next open and a stale reader would apply a
/// different device's events.
/// </para>
/// </summary>
public class EvdevProviderFailurePathTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void A_Failed_Enumeration_Keeps_Every_Device_The_Diagnostic_And_The_Held_State()
    {
        var system = new FakeEvdevSystem();
        var pad = system.AddGamepadNode("event0");

        using var provider = CreateProvider(system);

        // The last trustworthy state: one good scan, a diagnostic, and a stick the user is
        // holding off-center.
        Assert.Equal(["event0"], Ids(provider.GetConnected()));
        var goodDiagnostic = provider.LastScanDiagnostic;
        Assert.NotNull(goodDiagnostic);

        pad.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, 32767);
        WaitFor(() => provider.Poll("event0") is { LeftStickX: >= 0.999f }, TimeSpan.FromSeconds(5));

        // The next scan cannot even list the directory - the unreadable-/dev/input shape.
        system.NextEnumerationFailure = new UnauthorizedAccessException("Access to the path '/dev/input' is denied.");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        // Nothing was retired: the device, its reader, the held stick position and the last
        // diagnostic all survive, and the staleness is diagnosable.
        Assert.Equal(["event0"], Ids(provider.GetConnected()));
        Assert.NotNull(provider.Poll("event0"));
        Assert.Equal(1f, provider.Poll("event0")!.LeftStickX);
        Assert.Same(goodDiagnostic, provider.LastScanDiagnostic);
        Assert.NotNull(provider.LastScanError);
        Assert.Contains("UnauthorizedAccessException", provider.LastScanError);
        Assert.Contains("/dev/input", provider.LastScanError);

        // The reader is still live: an event still lands while the scans are failing.
        pad.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768);
        WaitFor(() => provider.Poll("event0") is { LeftStickX: <= -0.999f }, TimeSpan.FromSeconds(5));

        // The next successful scan clears the error.
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));
        Assert.Null(provider.LastScanError);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));
        Assert.Empty(system.Violations);
    }

    [Fact]
    public void A_Failed_Enumeration_Does_Not_Release_A_Held_Button()
    {
        // The user-visible half of the same guarantee: the daemon releases everything it was
        // injecting when a device disappears from the connected list. A failed scan must not
        // make the device disappear.
        var system = new FakeEvdevSystem();
        var pad = system.AddGamepadNode("event0");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        pad.EnqueueEvent(EvdevCodes.EvKey, EvdevCodes.BtnSouth, 1);
        WaitFor(() => provider.Poll("event0") is { PressedButtons: not GamepadButtonMask.None }, TimeSpan.FromSeconds(5));

        system.NextEnumerationFailure = new IOException("simulated /dev/input read failure");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        var held = provider.Poll("event0");
        Assert.NotNull(held);
        Assert.True(held.PressedButtons.HasFlag(GamepadButtonMask.A), "the held button survived the failed scan");
        Assert.Equal(["event0"], Ids(provider.GetConnected()));
    }

    [Fact]
    public void An_Unreadable_Directory_Is_Distinguished_From_An_Empty_One()
    {
        // The production path, end to end on the real libc enumeration: a directory that
        // exists but cannot be listed is a failed listing, and the scan over it keeps the
        // device it is serving - while a directory that lists cleanly and holds no nodes
        // really does retire it.
        // A plain guard, not Assert.SkipWhen: the Unix-only file-mode API below needs the
        // analyzer to see the platform check in control flow.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX directory permissions only exist on Linux");
            return;
        }

        var directory = TempDirectory();

        try
        {
            var node = Path.Combine(directory, "event0");
            File.WriteAllText(node, string.Empty);

            // The shape a user without the input group sees on the directory itself.
            File.SetUnixFileMode(directory, UnixFileMode.None);
            Assert.SkipWhen(!IsUnlistable(directory), "root ignores directory permissions");

            var failed = EvdevNodes.Enumerate(directory);
            Assert.True(failed.Failed);
            Assert.Empty(failed.Nodes);
            Assert.IsType<UnauthorizedAccessException>(failed.Failure);

            // The scan reads that as "could not look", not as "nothing is plugged in": the
            // device it is already serving survives.
            using var provider = new EvdevGamepadProvider { DeviceDirectory = directory };
            provider.RegisterServedDevice(ServedDevice(node));

            Assert.Equal([node], Ids(provider.GetConnected()));
            Assert.NotNull(provider.LastScanError);
            Assert.Contains("UnauthorizedAccessException", provider.LastScanError);

            // The one-shot probe, which owns no devices to lose, still reports the same
            // directory as "no nodes" - its documented reading of an unlistable directory.
            var probed = EvdevGamepadProvider.ProbeOnce(directory);
            Assert.Equal(EvdevScanOutcome.NoEventNodes, probed.Outcome);
            Assert.Equal(0, probed.NodeCount);
        }
        finally
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void An_Empty_But_Listable_Directory_Still_Retires_Devices()
    {
        // The other side of the distinction: a directory that lists cleanly and holds no
        // nodes is a real "nothing is plugged in", and the scan must still retire the served
        // device - hot-plug out - rather than treating every empty listing as a failure.
        // A plain guard, not Assert.SkipWhen: the real enumeration P/Invoke below only
        // exists on Linux, and the analyzer needs to see the platform check in control flow.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("the real enumeration P/Invoke only exists on Linux");
            return;
        }

        var directory = TempDirectory();

        try
        {
            var node = Path.Combine(directory, "event0");
            File.WriteAllText(node, string.Empty);

            var listed = EvdevNodes.Enumerate(directory);
            Assert.False(listed.Failed);
            Assert.Single(listed.Nodes);

            using var provider = new EvdevGamepadProvider { DeviceDirectory = directory };
            provider.RegisterServedDevice(ServedDevice(node));
            Assert.Equal([node], Ids(provider.GetConnected()));

            // The node disappears: the next scan is a successful, empty listing.
            File.Delete(node);
            provider.Scan(directory);

            Assert.False(EvdevNodes.Enumerate(directory).Failed);
            Assert.Empty(provider.GetConnected());
            Assert.Null(provider.Poll(node));
            Assert.Null(provider.LastScanError);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_Reader_That_Sees_A_Poll_Error_Removes_Its_Own_Device()
    {
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var broken = system.AddGamepadNode("event1");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));

        var brokenFd = system.FdOf("event1");
        Assert.NotNull(brokenFd);

        // The device reports POLLERR: its reader retires the device from its own thread -
        // the path a vanished pad takes between two scans.
        broken.PollErrorMask = EvdevNative.PollError;

        WaitFor(() => provider.Poll("event1") is null, TimeSpan.FromSeconds(5));
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        // The self-removal closed both descriptors exactly once and left no reader behind.
        // The unlink and the closes are two different moments: the device leaves the list
        // inside Remove, the descriptors close inside the Stop that follows it.
        WaitForClosed(system, brokenFd!.Value);
        Assert.Empty(system.Violations);
        Assert.Equal(1, system.Log.Count(entry => entry == $"close:{brokenFd}"));
        Assert.Equal(2, system.OpenDescriptorCount);

        // A reader that outlived its close would read these.
        for (var i = 0; i < 10; i++)
        {
            broken.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, i);
        }

        Thread.Sleep(50);
        Assert.Empty(system.Violations);
    }

    [Fact]
    public void A_Reader_That_Hits_A_Fatal_Read_Removes_Its_Own_Device()
    {
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var broken = system.AddGamepadNode("event1");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));

        var brokenFd = system.FdOf("event1");
        Assert.NotNull(brokenFd);

        // The device stays readable but every read fails with ENODEV - the shape of a pad
        // that vanished between the poll and the read. EAGAIN would be retried; this is not.
        // The errno is armed before the event is queued: a reader that drained the event
        // first would have nothing left to read and would never hit the fatal read.
        broken.ReadErrno = EvdevNative.ErrnoNoDevice;
        broken.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, 100);

        WaitFor(() => provider.Poll("event1") is null, TimeSpan.FromSeconds(5));
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        // The unlink and the closes are two different moments: the device leaves the list
        // inside Remove, the descriptors close inside the Stop that follows it.
        WaitForClosed(system, brokenFd!.Value);
        Assert.Empty(system.Violations);
        Assert.Equal(1, system.Log.Count(entry => entry == $"close:{brokenFd}"));
        Assert.Equal(2, system.OpenDescriptorCount);
    }

    [Fact]
    public void A_Reader_Self_Removal_Racing_The_Scans_Stop_Keeps_Every_Descriptor_Closed_Once()
    {
        // The race this pins: a device whose reader retires it (POLLHUP) at the same moment
        // the scan's removal pass retires it (the node vanished). Both paths stop the same
        // device from different threads; exactly one may close each descriptor, and no reader
        // may touch a descriptor after it is closed - the freed number is handed to the next
        // open, and a stale reader would apply a different device's events.
        //
        // The interleaving is staged rather than left to chance: the reader is parked inside
        // poll(), the scan is launched on its own thread, and the gate releases both at once.
        // The assertions hold for every interleaving - reader first, scan first, or genuinely
        // overlapped - and the rounds exist to cover all of them.
        for (var round = 0; round < 10; round++)
        {
            var system = new FakeEvdevSystem();
            system.AddGamepadNode("event0");
            var dying = system.AddGamepadNode("event1");
            dying.PollErrorMask = EvdevNative.PollHangUp;
            dying.PollGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var provider = CreateProvider(system);
            Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));

            var dyingFd = system.FdOf("event1");
            Assert.NotNull(dyingFd);

            // Park the dying device's reader inside poll(), with the hang-up already pending.
            WaitFor(() => system.Log.Contains($"poll-gate:{dyingFd}"), TimeSpan.FromSeconds(5));

            // The node vanishes and the scan is due: launch it, then release the reader. From
            // here the reader's self-removal and the scan's Stop run concurrently.
            system.Unplug("event1");
            _clock.AdvanceMilliseconds(300);
            var scanThread = new Thread(() => provider.GetConnected()) { IsBackground = true };
            scanThread.Start();

            // The background scan is provably walking before anything is asserted about its
            // result - GetConnected returns as soon as the scan is launched, so waiting on the
            // scan alone would race the launch itself.
            WaitFor(() => system.EnumerateCount >= 2, TimeSpan.FromSeconds(5));

            dying.PollGate.SetResult();

            Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(10)));
            scanThread.Join(TimeSpan.FromSeconds(10));

            Assert.DoesNotContain(provider.GetConnected(), descriptor => descriptor.DeviceId == "event1");
            Assert.Null(provider.Poll("event1"));

            // Whichever path retired the device, its descriptors closed exactly once. When
            // the reader retired itself, its Stop runs on the reader thread and can finish
            // after the scan's worker has already cleared its slot - so the close is waited
            // for rather than assumed to precede the scan's completion.
            WaitForClosed(system, dyingFd!.Value);
            Assert.Empty(system.Violations);
            Assert.Equal(1, system.Log.Count(entry => entry == $"close:{dyingFd}"));

            // A reader that outlived its close would read these.
            for (var i = 0; i < 10; i++)
            {
                dying.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, i);
            }

            Thread.Sleep(20);
            Assert.Empty(system.Violations);

            // The freed number is handed back out by the next open, and nothing stale reads
            // from it in between.
            system.ConfigureGamepadNode("event2");
            system.PlugIn("event2");
            _clock.AdvanceMilliseconds(300);
            provider.GetConnected();
            Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(10)));

            Assert.Equal(dyingFd, system.FdOf("event2"));
            Assert.Empty(system.Violations);
            Assert.Equal(["event0", "event2"], Ids(provider.GetConnected()));
            Assert.Equal(4, system.OpenDescriptorCount);
        }
    }

    private EvdevGamepadProvider CreateProvider(FakeEvdevSystem system)
        => new(system, _clock.Now, FakeClock.Frequency);

    private static IEnumerable<string> Ids(IReadOnlyList<GamepadDescriptor> descriptors)
        => descriptors.Select(descriptor => descriptor.DeviceId);

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < timeout)
        {
            Thread.Sleep(1);
        }

        Assert.True(condition(), $"condition not reached within {timeout}");
    }

    /// <summary>
    /// Waits until <paramref name="fd"/> shows exactly one close in the fake's log. A device
    /// leaves the connected list inside Remove and closes its descriptors inside the Stop
    /// that follows - on the reader's own thread when the reader retired itself - so the two
    /// are separate moments and only the second one is safe to assert on.
    /// </summary>
    private static void WaitForClosed(FakeEvdevSystem system, int fd)
        => WaitFor(() => system.Log.Count(entry => entry == $"close:{fd}") == 1, TimeSpan.FromSeconds(5));

    private static EvdevGamepadProvider.Device ServedDevice(string path)
        => new()
        {
            Path = path,
            Name = "测试手柄",
            Tracker = new EvdevGamepadStateTracker(),
            UnhandledAxes = [],
            Fd = -1,
        };

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dezirojoy-evdev-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    private static bool IsUnlistable(string path)
    {
        try
        {
            _ = Directory.GetFiles(path, "event*");
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
