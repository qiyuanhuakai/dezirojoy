using System.Diagnostics;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Platform;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The responsiveness contract the segmented-joystick report was about: the daemon ticks
/// every 2ms, and a tick must never wait behind the 250ms rescan's directory walk and device
/// I/O. The provider used to run the rescan inside <c>GetConnected</c> under the one lock
/// that also guarded <c>Poll</c> and every reader's event application, so a slow walk stalled
/// sampling and event merging for its whole duration - the stick then advanced in visible
/// segments. Measured on the pre-fix code with a 10,000-node fixture tree: a 47ms scan made
/// the worst <c>Poll</c> wait 46ms.
/// <para>
/// These tests pin the fixed shape with the injectable native seam
/// (<see cref="FakeEvdevSystem"/>) and a hand-advanced clock (<see cref="FakeClock"/>), so
/// every scenario is deterministic: a scan stalled mid-open by a gate, events queued into a
/// node, descriptor numbers handed back out after a close exactly the way the kernel reuses
/// them. No sleeps on the 250ms cadence, no timing thresholds that only hold on a fast
/// machine.
/// </para>
/// </summary>
public class EvdevProviderResponsivenessTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void The_First_Scan_Completes_Before_GetConnected_Returns()
    {
        // The daemon's startup summary reads LastScanDiagnostic right after its first
        // GetConnected, so the first scan stays inline and synchronous - it is the only one
        // that does. Everything the periodic path must not do is pinned by the tests below.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");

        using var provider = CreateProvider(system);

        var connected = provider.GetConnected();

        Assert.Equal(["event0"], Ids(connected));
        Assert.NotNull(provider.LastScanDiagnostic);
        Assert.Equal(EvdevScanOutcome.GamepadsFound, provider.LastScanDiagnostic.Outcome);
        Assert.Equal(1, provider.LastScanDiagnostic.GamepadCount);
    }

    [Fact]
    public void GetConnected_Serves_The_Cached_List_Until_The_Interval_Elapses()
    {
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");

        using var provider = CreateProvider(system);

        var first = provider.GetConnected();
        Assert.Equal(1, system.EnumerateCount);

        // Inside the interval: the cached list, served without touching the directory again.
        var second = provider.GetConnected();
        Assert.Same(first, second);
        Assert.Equal(1, system.EnumerateCount);

        // Past the interval: the next call launches a background scan instead of walking the
        // directory on the caller's thread, and the list stays correct across it.
        _clock.AdvanceMilliseconds(300);
        var third = provider.GetConnected();
        Assert.Equal(["event0"], Ids(third));

        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, system.EnumerateCount);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));
    }

    [Fact]
    public async Task Poll_Is_Not_Blocked_While_A_Scan_Is_Stalled_Mid_Open()
    {
        // The regression test for the reported bug: a scan that has stalled inside a device
        // open must not hold up sampling. The gate makes the stall deterministic and
        // unbounded, so any version that serializes Poll behind the scan fails here instead
        // of passing on a fast machine.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var slow = system.ConfigureGamepadNode("event1");
        slow.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        // Hot-plug the gated node in and let the interval elapse: GetConnected now launches
        // the background scan, which blocks inside event1's open.
        system.PlugIn("event1");
        _clock.AdvanceMilliseconds(300);
        var launched = new Thread(() => provider.GetConnected()) { IsBackground = true };
        launched.Start();
        WaitFor(() => system.ReachedOpen("event1"), TimeSpan.FromSeconds(5));

        // The scan is provably stalled here, holding no lock the sampling path needs.
        Assert.False(slow.OpenGate.Task.IsCompleted);

        var worst = TimeSpan.Zero;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(500))
        {
            var watch = Stopwatch.StartNew();
            var state = await Task.Run(() => provider.Poll("event0"))
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            watch.Stop();

            Assert.NotNull(state);
            if (watch.Elapsed > worst)
            {
                worst = watch.Elapsed;
            }

            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(
            worst < TimeSpan.FromMilliseconds(250),
            $"Poll waited {worst.TotalMilliseconds:0.##}ms behind a scan stalled in open()");

        // Releasing the gate lets the scan finish and serve the node it stalled on.
        slow.OpenGate.SetResult();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));
        launched.Join(TimeSpan.FromSeconds(5));

        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));
    }

    [Fact]
    public void Reader_Events_Keep_Merging_While_A_Scan_Is_Stalled()
    {
        // The other half of the segmentation: the reader threads applied events under the
        // same lock the scan held, so events queued in the kernel for the scan's duration
        // and landed in one burst. The per-device lock keeps them merging throughout.
        var system = new FakeEvdevSystem();
        var pad = system.AddGamepadNode("event0");
        var slow = system.ConfigureGamepadNode("event1");
        slow.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        system.PlugIn("event1");
        _clock.AdvanceMilliseconds(300);
        var launched = new Thread(() => provider.GetConnected()) { IsBackground = true };
        launched.Start();
        WaitFor(() => system.ReachedOpen("event1"), TimeSpan.FromSeconds(5));

        // The reader for event0 is blocked in poll() on a node with no events. Queue one
        // while the scan is still stalled inside event1's open.
        pad.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, 32767);

        IGamepadState? state = null;
        WaitFor(() => (state = provider.Poll("event0")) is { LeftStickX: >= 0.999f }, TimeSpan.FromSeconds(5));

        // The event reached the tracker while the scan was still stalled: reader application
        // was never queued behind the scan.
        Assert.False(slow.OpenGate.Task.IsCompleted);
        Assert.NotNull(state);
        Assert.Equal(1f, state.LeftStickX);

        slow.OpenGate.SetResult();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));
        launched.Join(TimeSpan.FromSeconds(5));
        Assert.Empty(system.Violations);
    }

    [Fact]
    public void Overlapping_GetConnected_Calls_Run_A_Single_Scan()
    {
        // A scan slower than the interval must not pile up behind itself: the daemon's tick
        // thread and its 250ms presence monitor can both find the scan due at once.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var slow = system.ConfigureGamepadNode("event1");
        slow.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        system.PlugIn("event1");
        _clock.AdvanceMilliseconds(300);

        var threads = Enumerable.Range(0, 8)
            .Select(_ => new Thread(() => provider.GetConnected()) { IsBackground = true })
            .ToList();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(5));
        }

        WaitFor(() => system.ReachedOpen("event1"), TimeSpan.FromSeconds(5));

        // One inline first scan plus exactly one background scan, no matter how many callers
        // found it due.
        Assert.Equal(2, system.EnumerateCount);

        slow.OpenGate.SetResult();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));
        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));
        Assert.Empty(system.Violations);
    }

    [Fact]
    public void A_Node_That_Appears_Is_Served_By_The_Next_Scan()
    {
        // Hot-plug in, end to end on the background path: the scan picks the node up, starts
        // its reader, and the device answers Poll.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var extra = system.ConfigureGamepadNode("event1");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        system.PlugIn("event1");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));

        extra.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768);
        WaitFor(() => provider.Poll("event1") is { LeftStickX: <= -0.999f }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_Node_That_Vanishes_Is_Stopped_And_Unlisted()
    {
        // Hot-plug out: the scan retires the device, its reader exits, its descriptors close
        // exactly once, and Poll stops answering for it.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        system.AddGamepadNode("event1");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));

        var vanishedFd = system.FdOf("event1");
        Assert.NotNull(vanishedFd);

        system.Unplug("event1");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        Assert.Equal(["event0"], Ids(provider.GetConnected()));
        Assert.Null(provider.Poll("event1"));
        Assert.Empty(system.Violations);
        Assert.Equal(1, system.Log.Count(entry => entry == $"close:{vanishedFd}"));

        // Exactly the survivor's two descriptors are still open: its device fd and its stop
        // eventfd. A leaked stop eventfd would show up here as a third.
        Assert.Equal(2, system.OpenDescriptorCount);
    }

    [Fact]
    public void Two_Identical_Pads_Stay_Two_Devices_With_Independent_State()
    {
        // Same model, same capabilities, two nodes: two devices, two readers, two trackers.
        var system = new FakeEvdevSystem();
        var first = system.AddGamepadNode("event0", "Fake Pad");
        var second = system.AddGamepadNode("event1", "Fake Pad");

        using var provider = CreateProvider(system);

        var connected = provider.GetConnected();
        Assert.Equal(2, connected.Count);
        Assert.All(connected, descriptor => Assert.Equal("Fake Pad", descriptor.Name));

        first.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, 32767);
        second.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, -32768);

        WaitFor(() => provider.Poll("event0") is { LeftStickX: >= 0.999f }, TimeSpan.FromSeconds(5));
        WaitFor(() => provider.Poll("event1") is { LeftStickX: <= -0.999f }, TimeSpan.FromSeconds(5));

        Assert.Equal(1f, provider.Poll("event0")!.LeftStickX);
        Assert.Equal(-1f, provider.Poll("event1")!.LeftStickX);
        Assert.Empty(system.Violations);
    }

    [Fact]
    public void Dispose_Stops_Readers_And_Aborts_An_In_Flight_Scan()
    {
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var slow = system.ConfigureGamepadNode("event1");
        slow.OpenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        // A scan is stalled inside event1's open when Dispose lands.
        system.PlugIn("event1");
        _clock.AdvanceMilliseconds(300);
        var launched = new Thread(() => provider.GetConnected()) { IsBackground = true };
        launched.Start();
        WaitFor(() => system.ReachedOpen("event1"), TimeSpan.FromSeconds(5));

        provider.Dispose();

        // The aborted scan must release what it opened and serve nothing.
        slow.OpenGate.SetResult();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));
        launched.Join(TimeSpan.FromSeconds(5));

        Assert.Empty(provider.GetConnected());
        Assert.Null(provider.Poll("event0"));
        Assert.Empty(system.Violations);

        // Nothing is left open: the served pad's device fd and stop eventfd, plus the
        // descriptor the aborted scan had opened before it saw the generation bump.
        Assert.Equal(0, system.OpenDescriptorCount);

        // A second Dispose changes nothing.
        provider.Dispose();
        Assert.Empty(system.Violations);
    }

    [Fact]
    public void A_Removed_Device_Is_Closed_Only_After_Its_Reader_Exits()
    {
        // The fd-reuse race: close() frees the descriptor number, the kernel hands the same
        // number to the next open, and a reader still inside read() on the old number would
        // apply a different device's events. Retiring a device therefore waits for its
        // reader to exit before closing anything - and the fake, which reuses the lowest
        // free number exactly like the kernel, records any read or close that touches a
        // descriptor that is not open.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var gone = system.AddGamepadNode("event1");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0", "event1"], Ids(provider.GetConnected()));

        // Keep the reader busy while the device is retired underneath it.
        for (var i = 0; i < 50; i++)
        {
            gone.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, i);
        }

        var goneFd = system.FdOf("event1");
        Assert.NotNull(goneFd);

        system.Unplug("event1");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        // A reader that outlived its close would read these: the fake records a read on a
        // closed descriptor as a violation instead of letting it pass.
        for (var i = 0; i < 50; i++)
        {
            gone.EnqueueEvent(EvdevCodes.EvAbs, EvdevCodes.AbsX, i);
        }

        Thread.Sleep(50);

        Assert.Null(provider.Poll("event1"));
        Assert.Empty(system.Violations);
        Assert.Equal(1, system.Log.Count(entry => entry == $"close:{goneFd}"));

        // The retired device's two descriptors are gone and only the survivor's remain.
        Assert.Equal(2, system.OpenDescriptorCount);

        // The freed number really is handed back out: the next open reuses it, and nothing
        // stale reads from it in between.
        system.ConfigureGamepadNode("event2");
        system.PlugIn("event2");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        Assert.Equal(goneFd, system.FdOf("event2"));
        Assert.Empty(system.Violations);
        Assert.Equal(["event0", "event2"], Ids(provider.GetConnected()));

        // event2's reader owns a fresh stop eventfd beside the reused device number, and
        // nothing from the retired device's reader touched either.
        Assert.Equal(4, system.OpenDescriptorCount);
    }

    [Fact]
    public void A_Permission_Wall_Is_Still_Reported_By_The_Scan()
    {
        // The permission diagnostics the daemon and the GUI quote must survive the move to
        // the background scan: a node this user may not open still says EACCES with the
        // usermod guidance, not "no gamepad".
        var system = new FakeEvdevSystem();
        var denied = system.AddGamepadNode("event0");
        denied.OpenErrno = EvdevNative.ErrnoAccessDenied;

        using var provider = CreateProvider(system);

        provider.GetConnected();

        var diagnostic = provider.LastScanDiagnostic;
        Assert.NotNull(diagnostic);
        Assert.Equal(EvdevScanOutcome.AccessDenied, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.DeniedNodeCount);
        Assert.Equal("event0", diagnostic.FirstDeniedPath);
        Assert.Equal(13, diagnostic.Errno);
        Assert.Contains("usermod -aG input", diagnostic.Guidance);
    }

    [Fact]
    public void A_Denied_Node_Beside_A_Gamepad_Keeps_Both_Facts()
    {
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");
        var denied = system.ConfigureGamepadNode("event1");
        denied.OpenErrno = EvdevNative.ErrnoAccessDenied;

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        // The denied node appears after the first scan, so the background scan is the one
        // that has to classify it.
        system.PlugIn("event1");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        var diagnostic = provider.LastScanDiagnostic;
        Assert.NotNull(diagnostic);
        Assert.Equal(EvdevScanOutcome.GamepadsFound, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.DeniedNodeCount);
        Assert.Equal("event1", diagnostic.FirstDeniedPath);
        Assert.Equal(13, diagnostic.Errno);
    }

    [Fact]
    public void A_Scan_That_Throws_Does_Not_Break_The_Next_One()
    {
        // A background scan must never take the daemon down: the readers already serving the
        // connected pads keep running untouched, and the next due scan retries the walk.
        var system = new FakeEvdevSystem();
        system.AddGamepadNode("event0");

        using var provider = CreateProvider(system);
        Assert.Equal(["event0"], Ids(provider.GetConnected()));

        system.ThrowOnNextEnumerate = new IOException("device directory vanished");
        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        // The failed scan is diagnosable and changed nothing.
        Assert.NotNull(provider.LastScanError);
        Assert.Contains("IOException", provider.LastScanError);

        Assert.Equal(["event0"], Ids(provider.GetConnected()));
        Assert.NotNull(provider.Poll("event0"));

        _clock.AdvanceMilliseconds(300);
        provider.GetConnected();
        Assert.True(provider.WaitForScanCompletion(TimeSpan.FromSeconds(5)));

        Assert.Equal(["event0"], Ids(provider.GetConnected()));
        Assert.Equal(3, system.EnumerateCount);
        Assert.Null(provider.LastScanError);
        Assert.Empty(system.Violations);
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
}
