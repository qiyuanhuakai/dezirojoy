using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Core.Profiles;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The editor's input-capture lease (<see cref="EngineHost.SetInputCaptureDevice"/>): the
/// device being bound keeps polling and keeps publishing samples, but dispatches no mapping,
/// so pressing the button under configuration never fires the action it is bound to. Every
/// other device maps untouched, explicit screen-keyboard output still goes through, and the
/// global safe-release semantics - disconnect, Stop, Dispose - still hold.
/// </summary>
public class EngineHostInputCaptureTests
{
    private sealed class FakeProvider : IGamepadProvider
    {
        private readonly Dictionary<string, GamepadDescriptor> _devices = [];

        private readonly Dictionary<string, IGamepadState> _states = [];

        public void Add(string deviceId, string name)
        {
            _devices[deviceId] = new GamepadDescriptor(deviceId, name);
        }

        public void Remove(string deviceId) => _devices.Remove(deviceId);

        public void SetState(string deviceId, IGamepadState state) => _states[deviceId] = state;

        public IReadOnlyList<GamepadDescriptor> GetConnected() => _devices.Values.ToList();

        public IGamepadState? Poll(string deviceId)
            => _states.TryGetValue(deviceId, out var state) ? state : null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSink : IOutputSink
    {
        public List<string> Calls { get; } = [];

        public void KeyDown(GamepadKey key) => Calls.Add($"down:{key}");

        public void KeyUp(GamepadKey key) => Calls.Add($"up:{key}");

        public void SendText(string text) => Calls.Add($"text:{text}");

        public void MouseMove(int dx, int dy) => Calls.Add($"move:{dx},{dy}");

        public void ScrollWheel(int horizontalNotches, int verticalNotches)
            => Calls.Add($"wheel:{horizontalNotches},{verticalNotches}");

        public void MouseButtonDown(MouseButton button) => Calls.Add($"mdown:{button}");

        public void MouseButtonUp(MouseButton button) => Calls.Add($"mup:{button}");

        public void Dispose()
        {
        }
    }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static (EngineHost Host, FakeProvider Provider, RecordingSink Sink) CreateHost()
    {
        var provider = new FakeProvider();
        var sink = new RecordingSink();
        var host = new EngineHost(provider, sink, new InMemoryProfileStore());
        return (host, provider, sink);
    }

    private static GamepadState Holding(GamepadButtonMask buttons) => GamepadState.Create(buttons, 0, 0, 0, 0);

    [Fact]
    public void Capture_Releases_The_Devices_Held_Actions_And_Stops_Dispatching_Them()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);

        // Taking the lease lifts whatever the device's mappings held: the capture exists so
        // the user can press the button they are binding, and a key left down would defeat
        // that on the very first tick.
        host.SetInputCaptureDevice("xinput:0");
        Assert.Equal("xinput:0", host.CapturedDeviceId);
        Assert.Contains("up:Enter", sink.Calls);

        // Still held, still polled, still sampled - but the mapping dispatches nothing.
        sink.Calls.Clear();
        var sampled = 0;
        host.Sampled += (_, _) => sampled++;
        host.TickOnce(Tick);

        Assert.Empty(sink.Calls);
        Assert.Equal(1, sampled);
    }

    [Fact]
    public void Capture_Of_A_Device_That_Is_Not_Connected_Yet_Suppresses_It_From_Its_First_Tick()
    {
        var (host, provider, sink) = CreateHost();

        // The editor asks before the pad appears; the lease is remembered rather than
        // silently dropped, which is the safe order for a capture that outlives its device.
        host.SetInputCaptureDevice("xinput:0");
        Assert.Equal("xinput:0", host.CapturedDeviceId);

        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));
        host.TickOnce(Tick);

        Assert.Empty(sink.Calls);

        host.SetInputCaptureDevice(null);
        Assert.Null(host.CapturedDeviceId);

        // The release adopts the polled state as the baseline, so the button held through the
        // capture does not fire on the first tick after it - only a real new press does.
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));
        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void Releasing_Capture_Does_Not_Fire_A_Button_Held_Through_The_Whole_Capture()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));

        host.TickOnce(Tick);
        host.SetInputCaptureDevice("xinput:0");
        host.TickOnce(Tick);
        sink.Calls.Clear();

        // The release must not fabricate a press edge for the button the user has held since
        // before the capture: it fires again only on a real release and re-press.
        host.SetInputCaptureDevice(null);
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.DoesNotContain("down:Enter", sink.Calls);

        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));
        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void Capture_Silences_Only_Its_Own_Device_And_Releases_Nothing_Another_Device_Holds()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("pad-a", "Xbox Wireless Controller");
        provider.Add("pad-b", "Xbox Wireless Controller");
        provider.SetState("pad-a", Holding(GamepadButtonMask.B));
        provider.SetState("pad-b", Holding(GamepadButtonMask.B));

        host.TickOnce(Tick);
        // One shared ownership table: the second pad pressing the same key does not press it
        // twice, and one down is all the sink has seen.
        Assert.Equal(["down:Enter"], sink.Calls);

        // Capturing pad-b releases only pad-b's hold on Enter - pad-a still holds it, so the
        // key stays down. The captured device's release must not lift another device's output.
        host.SetInputCaptureDevice("pad-b");
        Assert.DoesNotContain("up:Enter", sink.Calls);

        // pad-a keeps mapping while pad-b is captured.
        sink.Calls.Clear();
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        // pad-b's button released while captured: nothing happens, its dispatch is gated.
        provider.SetState("pad-b", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        // pad-a letting go lifts the key exactly once, at the end.
        provider.SetState("pad-a", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Equal(["up:Enter"], sink.Calls);
    }

    [Fact]
    public void A_Captured_Device_That_Disconnects_Takes_Its_Lease_With_It()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));

        host.TickOnce(Tick);
        host.SetInputCaptureDevice("xinput:0");

        provider.Remove("xinput:0");
        for (var tick = 0; tick < EngineHost.DeviceAbsenceGraceTicks + 1; tick++)
        {
            host.TickOnce(Tick);
        }

        Assert.Empty(host.Controllers);
        Assert.Null(host.CapturedDeviceId);

        // A pad that reconnects maps again from its first tick: the capture existed to silence
        // a pad the user was binding, and that pad is gone.
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));
        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void Stop_Clears_The_Capture_Lease()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));

        host.SetInputCaptureDevice("xinput:0");
        host.Stop();

        Assert.Null(host.CapturedDeviceId);

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void Explicit_Screen_Keyboard_Output_Still_Goes_Through_While_A_Device_Is_Captured()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        host.SetInputCaptureDevice("xinput:0");

        // The floating screen keyboard's output is an explicit act, not a mapping: the capture
        // gates mapping dispatch only, exactly as the hijack lease does.
        host.InjectText("hi");
        host.TapKey(GamepadKey.Backspace, KeyModifiers.None);

        Assert.Contains("text:hi", sink.Calls);
        Assert.Contains("down:Backspace", sink.Calls);
        Assert.Contains("up:Backspace", sink.Calls);
    }

    [Fact]
    public void The_User_Pause_The_Hijack_Lease_And_The_Capture_Are_Independent_Gates()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));

        host.TickOnce(Tick);

        host.SetInputCaptureDevice("xinput:0");
        host.SetInjectionSuspended(true);
        sink.Calls.Clear();

        // Lifting the hijack lease hands the pad back to the mapping - but the capture is a
        // separate gate, so the device stays silenced until the editor releases it.
        host.SetInjectionSuspended(false);
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        host.SetInputCaptureDevice(null);
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        provider.SetState("xinput:0", Holding(GamepadButtonMask.B));
        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
    }
}
