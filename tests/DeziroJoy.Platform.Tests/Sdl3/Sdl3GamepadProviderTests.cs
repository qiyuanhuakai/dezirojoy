using DeziroJoy.Core.Input;
using DeziroJoy.Platform.Sdl3;
using SDL3;
using Xunit;

namespace DeziroJoy.Platform.Tests.Sdl3;

/// <summary>
/// Provider lifecycle over the scripted <see cref="FakeSdl3Api"/>: enumeration, hot-plug
/// through the event queue, the enumeration safety net, remap, same-model independence,
/// failure paths and idempotent cleanup. Deterministic by construction - the only code that
/// could talk to a real SDL runtime lives behind the seam these tests replace.
/// </summary>
public sealed class Sdl3GamepadProviderTests : IDisposable
{
    private readonly FakeSdl3Api _api = new();
    private readonly Sdl3GamepadProvider _provider;

    public Sdl3GamepadProviderTests()
    {
        // Zero interval: every GetConnected scans, so a test can observe the effect of each
        // scripted change without racing the rate limiter. The one test that pins the rate
        // limiter creates its own provider with a real interval.
        _provider = new Sdl3GamepadProvider(_api, TimeSpan.Zero);
    }

    public void Dispose() => _provider.Dispose();

    /// <summary>Poll through the provider and keep the concrete state for its helpers.</summary>
    private GamepadState PollState(string deviceId) => (GamepadState)_provider.Poll(deviceId)!;

    [Fact]
    public void First_Scan_Opens_Devices_And_Reports_Descriptors()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, name: "Xbox 手柄", serial: "unit-1", path: "/dev/input/event5"));
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 2, name: "PS5 手柄", serial: "unit-2", path: "/dev/input/event9"));

        var connected = _provider.GetConnected();

        Assert.Equal(2, connected.Count);
        Assert.Contains(connected, d => d.DeviceId == "sdl:03000000000000000000000000000000:unit-1:/dev/input/event5");
        Assert.Contains(connected, d => d.DeviceId == "sdl:03000000000000000000000000000000:unit-2:/dev/input/event9");
        Assert.Equal(15, connected.First(d => d.Name == "Xbox 手柄").ButtonCount);
    }

    [Fact]
    public void Poll_Normalizes_Buttons_Sticks_And_Triggers()
    {
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var deviceId = _provider.GetConnected().Single().DeviceId;

        device.Pressed.Add(SDL.GamepadButton.South);
        device.Pressed.Add(SDL.GamepadButton.LeftShoulder);
        device.Pressed.Add(SDL.GamepadButton.Misc1);
        device.Axes[(int)SDL.GamepadAxis.LeftX] = 16384;
        device.Axes[(int)SDL.GamepadAxis.LeftY] = 32767;
        device.Axes[(int)SDL.GamepadAxis.RightTrigger] = 32767;

        var state = PollState(deviceId);

        Assert.True(state.IsPressed(GamepadButton.A));
        Assert.True(state.IsPressed(GamepadButton.LeftBumper));
        Assert.Contains((int)SDL.GamepadButton.Misc1, state.ExtraButtons);
        Assert.Equal(0.5f, state.LeftStickX, 5);
        Assert.True(state.LeftStickY < -0.9f, "SDL 的正值 Y 表示向下推");
        Assert.True(state.IsPressed(GamepadButton.RightTrigger));
    }

    [Fact]
    public void A_Removal_Event_Closes_The_Handle_And_Stops_Polling()
    {
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var deviceId = _provider.GetConnected().Single().DeviceId;
        var handle = _api.OpenedHandles.Single();

        _api.Queue(Sdl3GamepadEventType.Removed, 1);
        device.Enumerated = false;

        Assert.Empty(_provider.GetConnected());
        Assert.Null(_provider.Poll(deviceId));
        Assert.Equal(1, _api.CloseCount(handle));
    }

    [Fact]
    public void A_Device_Vanishing_Without_An_Event_Is_Still_Closed()
    {
        // SDL drops the oldest events when its queue overflows, so a missed Removed must not
        // leave a handle open forever: the enumeration pass is the safety net.
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var deviceId = _provider.GetConnected().Single().DeviceId;
        var handle = _api.OpenedHandles.Single();

        device.Enumerated = false;

        Assert.Empty(_provider.GetConnected());
        Assert.Null(_provider.Poll(deviceId));
        Assert.Equal(1, _api.CloseCount(handle));
    }

    [Fact]
    public void Same_Model_Units_Are_Tracked_Independently()
    {
        var first = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var second = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 2, serial: "unit-2"));

        var connected = _provider.GetConnected();
        Assert.Equal(2, connected.Count);

        var firstId = connected.First(d => d.DeviceId.EndsWith("unit-1", StringComparison.Ordinal)).DeviceId;
        var secondId = connected.First(d => d.DeviceId.EndsWith("unit-2", StringComparison.Ordinal)).DeviceId;

        // Pad A holds its trigger deep inside the band while pad B rests: B's polls must not
        // reset A's held state, and A's band value must not press B's trigger.
        first.Axes[(int)SDL.GamepadAxis.LeftTrigger] = 32767;
        _provider.Poll(firstId);

        for (var i = 0; i < 10; i++)
        {
            Assert.False(_provider.Poll(secondId)!.PressedButtons.Has(GamepadButton.LeftTrigger));
            Assert.True(_provider.Poll(firstId)!.PressedButtons.Has(GamepadButton.LeftTrigger));
        }

        first.Axes[(int)SDL.GamepadAxis.LeftTrigger] = 16000;
        Assert.True(_provider.Poll(firstId)!.PressedButtons.Has(GamepadButton.LeftTrigger), "band value holds A's trigger");
        Assert.False(_provider.Poll(secondId)!.PressedButtons.Has(GamepadButton.LeftTrigger));

        // Buttons stay per-device too.
        second.Pressed.Add(SDL.GamepadButton.South);
        Assert.True(_provider.Poll(secondId)!.PressedButtons.Has(GamepadButton.A));
        Assert.False(_provider.Poll(firstId)!.PressedButtons.Has(GamepadButton.A));
    }

    [Fact]
    public void A_Remap_Refreshes_The_Mapping_And_Cannot_Stick_A_Released_Button()
    {
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var deviceId = _provider.GetConnected().Single().DeviceId;

        device.Pressed.Add(SDL.GamepadButton.South);
        Assert.True(PollState(deviceId).IsPressed(GamepadButton.A));

        device.Info = device.Info with { Mapping = "user-mapping" };
        _api.Queue(Sdl3GamepadEventType.Remapped, 1);

        _provider.GetConnected();
        var diagnostic = _provider.LastScanDiagnostic!;

        Assert.Equal("user-mapping", diagnostic.Devices.Single().Mapping);

        // A button still held across the remap stays held: reset and full re-apply happen
        // inside one poll, so the engine never sees a spurious release-then-press pair.
        Assert.True(PollState(deviceId).IsPressed(GamepadButton.A));

        // A button released at remap time must not survive from the old layout.
        device.Pressed.Remove(SDL.GamepadButton.South);
        Assert.False(PollState(deviceId).IsPressed(GamepadButton.A));
    }

    [Fact]
    public void An_Init_Failure_Reports_SdlUnavailable_Instead_Of_Throwing()
    {
        _api.InitSucceeds = false;
        _api.InitError = "找不到原生 SDL3 运行时";

        Assert.Empty(_provider.GetConnected());
        Assert.Null(_provider.Poll("sdl:anything"));

        var diagnostic = _provider.LastScanDiagnostic;
        Assert.NotNull(diagnostic);
        Assert.Equal(Sdl3ScanOutcome.SdlUnavailable, diagnostic!.Outcome);
        Assert.Equal("找不到原生 SDL3 运行时", diagnostic.InitError);
        Assert.Contains("SDL3.dll", diagnostic.Guidance);
    }

    [Fact]
    public void An_Open_Failure_Is_Recorded_In_The_Diagnostic()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        _api.OpenFailures[1] = "没有可用映射";

        Assert.Empty(_provider.GetConnected());

        var diagnostic = _provider.LastScanDiagnostic!;
        Assert.Equal(Sdl3ScanOutcome.OpenFailed, diagnostic.Outcome);
        var failure = Assert.Single(diagnostic.OpenFailures);
        Assert.Equal(1u, failure.InstanceId);
        Assert.Equal("没有可用映射", failure.Error);
        Assert.Contains("没有可用映射", diagnostic.Guidance);
    }

    [Fact]
    public void An_Unstable_Identity_Is_Flagged_In_The_Diagnostic()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 4, serial: "", path: ""));

        _provider.GetConnected();

        var diagnostic = _provider.LastScanDiagnostic!;
        Assert.Equal(new uint[] { 4 }, diagnostic.UnstableIdentityInstanceIds);
        Assert.Contains("实例 ID", diagnostic.Guidance);
    }

    [Fact]
    public void Every_Tick_Refreshes_Sdl_State_While_Scans_Stay_Throttled()
    {
        // The 4 Hz input-lag regression: SDL_GetGamepadButton/Axis read what the last
        // SDL_UpdateGamepads pump left behind, so the pump must run once per
        // GetConnected (the engine's tick) - not once per scan. Enumeration and the
        // diagnostic rebuild stay on the scan throttle.
        using var provider = new Sdl3GamepadProvider(_api, TimeSpan.FromMilliseconds(250));
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));

        provider.GetConnected();
        provider.GetConnected();
        provider.GetConnected();

        Assert.Equal(3, _api.UpdateGamepadsCalls);
        Assert.Equal(3, _api.PumpEventsCalls);
        Assert.Equal(1, _api.GetGamepadIdsCalls);
        Assert.Equal(3, provider.StateRefreshCount);
    }

    [Fact]
    public void Polling_Many_Devices_Does_Not_Re_Pump_Sdl_State()
    {
        // The engine polls every connected pad once per tick; pumping inside Poll would
        // multiply identical work by the pad count, so Poll must stay pump-free.
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 2, serial: "unit-2"));
        var connected = _provider.GetConnected();

        var pumpsAfterConnect = _api.UpdateGamepadsCalls;
        var drainsAfterConnect = _api.PumpEventsCalls;

        for (var i = 0; i < 5; i++)
        {
            foreach (var descriptor in connected)
            {
                _provider.Poll(descriptor.DeviceId);
            }
        }

        Assert.Equal(pumpsAfterConnect, _api.UpdateGamepadsCalls);
        Assert.Equal(drainsAfterConnect, _api.PumpEventsCalls);
    }

    [Fact]
    public void A_Removal_Event_Is_Acted_On_Between_Scans()
    {
        // Hotplug must not wait for the enumeration throttle: the per-tick event drain
        // closes the handle within a tick. The enumeration reconcile stays the guaranteed
        // safety net for a dropped event (covered above).
        using var provider = new Sdl3GamepadProvider(_api, TimeSpan.FromMilliseconds(250));
        var device = _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var deviceId = provider.GetConnected().Single().DeviceId;
        var handle = _api.OpenedHandles.Single();
        var enumerationsAfterOpen = _api.GetGamepadIdsCalls;

        _api.Queue(Sdl3GamepadEventType.Removed, 1);
        device.Enumerated = false;

        Assert.Empty(provider.GetConnected());
        Assert.Null(provider.Poll(deviceId));
        Assert.Equal(1, _api.CloseCount(handle));
        Assert.Equal(enumerationsAfterOpen, _api.GetGamepadIdsCalls);
    }

    [Fact]
    public void StateRefreshCount_Tracks_Per_Tick_Refreshes()
    {
        // The observable the probe's watch mode prints: a healthy provider refreshes once
        // per tick; polling devices never moves it.
        using var provider = new Sdl3GamepadProvider(_api, TimeSpan.FromMilliseconds(250));
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));

        var deviceId = provider.GetConnected().Single().DeviceId;
        Assert.Equal(1, provider.StateRefreshCount);

        provider.GetConnected();
        Assert.Equal(2, provider.StateRefreshCount);

        for (var i = 0; i < 4; i++)
        {
            provider.Poll(deviceId);
        }

        Assert.Equal(2, provider.StateRefreshCount);
    }

    [Fact]
    public void An_Init_Failure_Is_Retried_On_The_Scan_Cadence_Not_Every_Tick()
    {
        // A failing SDL_Init must not be retried every tick; the attempt stays on the
        // scan throttle while the per-tick refresh stays off until SDL is up.
        using var provider = new Sdl3GamepadProvider(_api, TimeSpan.FromMilliseconds(250));
        _api.InitSucceeds = false;

        provider.GetConnected();
        provider.GetConnected();
        provider.GetConnected();

        // No state pump at all while SDL is down, and the failed init attempt itself
        // stays on the scan throttle (one attempt per scan, not one per tick).
        Assert.Equal(0, provider.StateRefreshCount);
        Assert.Equal(0, _api.UpdateGamepadsCalls);
        Assert.Equal(0, _api.GetGamepadIdsCalls);
        Assert.Equal(Sdl3ScanOutcome.SdlUnavailable, provider.LastScanDiagnostic!.Outcome);
    }

    [Fact]
    public void Dispose_Is_Idempotent_And_Releases_Everything()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        var deviceId = _provider.GetConnected().Single().DeviceId;
        var handle = _api.OpenedHandles.Single();

        _provider.Dispose();
        _provider.Dispose();

        Assert.Equal(1, _api.ShutdownCalls);
        Assert.Equal(1, _api.CloseCount(handle));
        Assert.Empty(_provider.GetConnected());
        Assert.Null(_provider.Poll(deviceId));
    }

    [Fact]
    public void Dispose_Releases_Devices_Opened_After_The_First_Scan()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        _provider.GetConnected();

        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 2, serial: "unit-2"));
        _api.Queue(Sdl3GamepadEventType.Added, 2);
        Assert.Equal(2, _provider.GetConnected().Count);

        var handles = _api.OpenedHandles.ToArray();
        _provider.Dispose();

        Assert.All(handles, handle => Assert.Equal(1, _api.CloseCount(handle)));
        Assert.Equal(1, _api.ShutdownCalls);
    }

    [Fact]
    public void ProbeOnce_Reports_And_Releases_Without_Keeping_Devices()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));

        var diagnostic = Sdl3GamepadProvider.ProbeOnce(_api);

        Assert.Equal(Sdl3ScanOutcome.GamepadsFound, diagnostic.Outcome);
        Assert.Equal(1, diagnostic.GamepadCount);
        Assert.Single(diagnostic.Devices);
        Assert.Equal(1, _api.ShutdownCalls);
        Assert.Single(_api.ClosedHandles);
    }

    [Fact]
    public void A_Partial_Result_Keeps_The_Working_Device_And_Lists_The_Failure()
    {
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 1, serial: "unit-1"));
        _api.AddDevice(FakeSdl3Api.MakeDevice(instanceId: 2, serial: "unit-2"));
        _api.OpenFailures[2] = "打开失败";

        var connected = _provider.GetConnected();

        Assert.Single(connected);
        var diagnostic = _provider.LastScanDiagnostic!;
        Assert.Equal(Sdl3ScanOutcome.GamepadsFound, diagnostic.Outcome);
        var failure = Assert.Single(diagnostic.OpenFailures);
        Assert.Equal(2u, failure.InstanceId);
        Assert.Contains("打开失败", diagnostic.Guidance);
    }
}
