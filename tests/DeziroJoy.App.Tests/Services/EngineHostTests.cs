using DeziroJoy.App.Services;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

public class EngineHostTests
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

    private static (EngineHost Host, FakeProvider Provider, RecordingSink Sink) CreateHost(IProfileStore? store = null)
    {
        var provider = new FakeProvider();
        var sink = new RecordingSink();
        var host = new EngineHost(provider, sink, store ?? new InMemoryProfileStore());
        return (host, provider, sink);
    }

    [Fact]
    public void Connected_Xbox_Pad_Applies_The_Default_Profile()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);

        Assert.Single(host.Controllers);
        Assert.Equal("Xbox Wireless Controller", host.Controllers.Single().Descriptor.Name);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void No_Devices_Means_No_Controllers_And_No_Output()
    {
        var (host, _, sink) = CreateHost();

        host.TickOnce(Tick);

        Assert.Empty(host.Controllers);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Saved_Profile_Takes_Precedence_Over_The_Built_In_Default()
    {
        var store = new InMemoryProfileStore();
        store.Save(new GamepadProfile
        {
            Name = "自定义",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        });

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);

        Assert.Contains("down:Space", sink.Calls);
        Assert.DoesNotContain("down:Enter", sink.Calls);
    }

    [Fact]
    public void Right_Stick_Moves_The_Mouse()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0));

        host.TickOnce(Tick);
        host.TickOnce(Tick);

        Assert.Contains(sink.Calls, call => call.StartsWith("move:", StringComparison.Ordinal));
    }

    [Fact]
    public void Disabling_Mapping_Releases_Held_Keys_And_Stops_Output()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);

        host.SetMappingEnabled(false);
        Assert.Contains("up:Enter", sink.Calls);

        sink.Calls.Clear();
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Re_Enabling_Mapping_Resumes_Output()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        host.SetMappingEnabled(false);
        host.SetMappingEnabled(true);
        host.TickOnce(Tick);

        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void Disconnected_Device_Is_Removed()
    {
        var (host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");

        host.TickOnce(Tick);
        Assert.Single(host.Controllers);

        provider.Remove("xinput:0");

        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Empty(host.Controllers);
    }

    [Fact]
    public void A_Transiently_Missing_Device_Keeps_Its_Runtime_And_Its_Held_Keys()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);

        // One XInputGetState failure on a Bluetooth pad drops the device from the connected
        // list for a tick. Removing it then would ReleaseAll and lift the held Enter, closing
        // the Alt+Tab switcher so the next tick reopens it at MRU position 1.
        provider.Remove("xinput:0");
        host.TickOnce(Tick);

        Assert.Single(host.Controllers);
        Assert.Contains("down:Enter", sink.Calls);
        Assert.DoesNotContain("up:Enter", sink.Calls);
    }

    [Fact]
    public void A_Device_Missing_For_Longer_Than_The_Grace_Period_Is_Removed_And_Releases_Its_Keys()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        provider.Remove("xinput:0");

        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Empty(host.Controllers);
        Assert.Contains("up:Enter", sink.Calls);
    }

    [Fact]
    public void Device_Removal_Clears_A_Latched_Precision_Toggle()
    {
        var store = new InMemoryProfileStore();
        store.Save(new GamepadProfile
        {
            Name = "toggle",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new StickBinding(GamepadStick.Right),
                    Target = new MouseMoveTarget(new MouseStickSettings
                    {
                        DeadZone = 0f,
                        MaxSpeed = 640f,
                        Curve = ResponseCurve.Linear,
                        CurveStrength = 0f,
                        ResponseTime = 0f,
                        InvertY = false,
                        PrecisionScale = 0.5f,
                        PrecisionButton = GamepadButton.LeftStick,
                        PrecisionMode = PrecisionMode.Toggle,
                    }),
                },
            ],
        });

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.LeftStick, 0, 0, 1, 0));
        host.TickOnce(Tick);
        Assert.Equal("move:5,0", Assert.Single(sink.Calls));

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0));
        sink.Calls.Clear();
        host.TickOnce(Tick);
        Assert.Equal("move:5,0", Assert.Single(sink.Calls));

        provider.Remove("xinput:0");
        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(Tick);
        }

        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.None, 0, 0, 1, 0));
        sink.Calls.Clear();
        host.TickOnce(Tick);
        Assert.Equal("move:10,0", Assert.Single(sink.Calls));
    }

    [Fact]
    public void Refresh_Presence_Tracks_Devices_Without_Ticking_Or_Sampling()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        var sampled = 0;
        host.Sampled += (_, _) => sampled++;

        host.RefreshPresence();

        Assert.Single(host.Controllers);
        Assert.Empty(sink.Calls);
        Assert.Equal(0, sampled);

        provider.Remove("xinput:0");
        host.RefreshPresence();

        Assert.Empty(host.Controllers);
    }

    [Fact]
    public void StartManual_Enters_The_Running_State_Without_A_Background_Loop()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.StartManual();

        Assert.True(host.IsRunning);

        var sampled = 0;
        host.Sampled += (_, _) => sampled++;

        host.TickOnce(Tick);

        Assert.Equal(1, sampled);
        Assert.Contains("down:Enter", sink.Calls);

        host.Stop();

        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Reload_Swaps_In_An_Edited_Profile_Immediately()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);

        host.Reload("xinput:0", new GamepadProfile
        {
            Name = "改过的配置",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.B),
                    Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
                },
            ],
        });

        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        sink.Calls.Clear();
        host.TickOnce(Tick);

        Assert.Contains("down:LeftCtrl", sink.Calls);
        Assert.Contains("down:C", sink.Calls);
    }

    [Fact]
    public void Sampled_Event_Reports_The_Latest_State()
    {
        var (host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        ControllerSampledEventArgs? sampled = null;
        host.Sampled += (_, args) => sampled = args;

        host.TickOnce(Tick);

        Assert.NotNull(sampled);
        Assert.True(sampled.State.PressedButtons.Has(GamepadButton.B));
    }

    [Fact]
    public void SamplePresence_Raises_PresenceSampled_Without_Ticking_Or_Injecting()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        var sampled = 0;
        var presence = 0;
        host.Sampled += (_, _) => sampled++;
        host.PresenceSampled += (_, _) => presence++;

        host.SamplePresence();

        Assert.Single(host.Controllers);
        Assert.Empty(sink.Calls);
        Assert.Equal(0, sampled);
        Assert.Equal(1, presence);

        provider.Remove("xinput:0");
        host.SamplePresence();

        Assert.Empty(host.Controllers);
    }

    [Fact]
    public void Suspending_Injection_Stops_Dispatch_But_Keeps_Sampling()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);

        host.SetInjectionSuspended(true);
        Assert.True(host.IsInjectionSuspended);
        Assert.True(sink.Calls.Contains("up:Enter"), "suspending must release whatever is held");

        sink.Calls.Clear();
        var sampled = 0;
        host.Sampled += (_, _) => sampled++;
        host.TickOnce(Tick);

        Assert.Empty(sink.Calls);
        Assert.Equal(1, sampled);
    }

    [Fact]
    public void Resuming_Injection_After_Suspend_Fires_Again()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        host.SetInjectionSuspended(true);
        host.SetInjectionSuspended(false);
        Assert.False(host.IsInjectionSuspended);

        sink.Calls.Clear();
        host.TickOnce(Tick);

        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void First_Connect_Initializes_All_Four_Preset_Slots()
    {
        var store = new InMemoryProfileStore();
        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");

        host.RefreshPresence();

        var presets = new PresetStore(store);
        Assert.Equal([1, 2, 3, 4], presets.SlotsInUse("xinput:0"));
        Assert.Equal("xinput:0", host.Controllers.Single().Profile.DeviceId);
    }

    [Fact]
    public void Preset_Switch_Rolls_Back_Every_Device_When_One_Reload_Fails()
    {
        var (host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.Add("xinput:1", "DualShock 4");
        host.RefreshPresence();
        var before = host.Controllers.ToDictionary(
            controller => controller.Descriptor.DeviceId,
            controller => controller.Profile,
            StringComparer.Ordinal);
        var replacements = before.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { Name = $"Replacement {pair.Key}" },
            StringComparer.Ordinal);
        replacements["xinput:1"] = null!;

        Assert.Throws<ArgumentNullException>(() => host.SwitchPresetSlot(2, replacements));

        Assert.Equal(1, host.ActivePresetSlot);
        foreach (var controller in host.Controllers)
        {
            Assert.Equal(before[controller.Descriptor.DeviceId].Name, controller.Profile.Name);
        }
    }

    [Fact]
    public void ControllersChanged_Raises_For_Connect_And_Disconnect()
    {
        var (host, provider, _) = CreateHost();
        var changed = 0;
        host.ControllersChanged += () => changed++;
        provider.Add("xinput:0", "Xbox Wireless Controller");

        host.RefreshPresence();
        provider.Remove("xinput:0");
        host.RefreshPresence();

        Assert.Equal(2, changed);
    }

    private static GamepadProfile LeaderProfile(LeaderSwitchMode switchMode, params GamepadButton[] buttons)
        => new()
        {
            Name = "leader",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                .. buttons.Select(button => new MappingEntry
                {
                    Source = new ButtonBinding(button),
                    Target = new SpecialKeyTarget(
                        SpecialKeyKind.Leader,
                        PrecisionMode.Toggle,
                        switchMode,
                        SlotA: 1,
                        SlotB: 2),
                }),
            ],
        };

    [Fact]
    public void A_Leader_Press_Edge_Raises_Preset_Switch_Requested_Once_Per_Tick()
    {
        var store = new InMemoryProfileStore();
        store.Save(LeaderProfile(LeaderSwitchMode.Press, GamepadButton.Back));
        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        var requested = new List<int>();
        host.PresetSwitchRequested += (_, slot) => requested.Add(slot);

        host.TickOnce(Tick);
        Assert.Empty(requested);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);

        Assert.Equal([2], requested);
        Assert.Empty(sink.Calls);

        // The button is still held: no new edge, so no second request.
        host.TickOnce(Tick);
        Assert.Equal([2], requested);
    }

    [Fact]
    public void A_Hold_Leader_Raises_Preset_Switch_Requested_On_Both_Edges()
    {
        var store = new InMemoryProfileStore();
        store.Save(LeaderProfile(LeaderSwitchMode.Hold, GamepadButton.Back));
        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        var requested = new List<int>();
        host.PresetSwitchRequested += (_, slot) => requested.Add(slot);

        host.TickOnce(Tick);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2], requested);

        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Equal([2, 1], requested);
    }

    [Fact]
    public void Repeated_Switch_Requests_In_One_Tick_Raise_The_Event_Once()
    {
        var store = new InMemoryProfileStore();
        store.Save(LeaderProfile(LeaderSwitchMode.Press, GamepadButton.Back, GamepadButton.Start));
        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        var requested = new List<int>();
        host.PresetSwitchRequested += (_, slot) => requested.Add(slot);

        host.TickOnce(Tick);

        provider.SetState(
            "xinput:0",
            GamepadState.Create(GamepadButtonMask.Back | GamepadButtonMask.Start, 0, 0, 0, 0));
        host.TickOnce(Tick);

        Assert.Equal([2], requested);
    }

    [Fact]
    public async Task Mapping_Mutations_Wait_For_An_In_Flight_Tick()
    {
        var changed = new GamepadProfile { Name = "changed", DeviceId = "xinput:0", Mappings = [] };
        var mutations = new Action<EngineHost>[]
        {
            host => host.Reload("xinput:0", changed),
            host => host.SetMappingEnabled(false),
            host => host.SetInjectionSuspended(true),
        };

        foreach (var mutation in mutations)
        {
            var provider = new BlockingProvider();
            using var host = new EngineHost(provider, new RecordingOutputSink(), new InMemoryProfileStore());
            host.StartManual();
            var tick = Task.Run(() => host.TickOnce(Tick), TestContext.Current.CancellationToken);
            await provider.PollEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var mutationTask = Task.Run(() =>
            {
                mutationStarted.SetResult();
                mutation(host);
            }, TestContext.Current.CancellationToken);
            await mutationStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Task.Yield();

            try
            {
                Assert.False(mutationTask.IsCompleted, "a mutation ran inside the in-flight tick");
            }
            finally
            {
                provider.ReleasePoll();
            }

            await Task.WhenAll(tick, mutationTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private sealed class BlockingProvider : IGamepadProvider
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _pollCount;

        public TaskCompletionSource PollEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<GamepadDescriptor> GetConnected()
            => [new GamepadDescriptor("xinput:0", "Xbox Wireless Controller")];

        public IGamepadState? Poll(string deviceId)
        {
            PollEntered.TrySetResult();
            if (Interlocked.Exchange(ref _pollCount, 1) == 0)
            {
                _release.Wait(TestContext.Current.CancellationToken);
            }

            return GamepadState.Neutral;
        }

        public void ReleasePoll() => _release.Set();

        public void Dispose() => _release.Dispose();
    }

    private sealed class InMemoryProfileStore : IProfileStore
    {
        private readonly Dictionary<string, GamepadProfile> _profiles = [];

        public GamepadProfile? Load(string deviceId) => _profiles.GetValueOrDefault(deviceId);

        public void Save(GamepadProfile profile) => _profiles[profile.DeviceId ?? profile.Name] = profile;

        public bool Delete(string deviceId) => _profiles.Remove(deviceId);

        public IReadOnlyList<GamepadProfile> LoadAll() => _profiles.Values.ToList();
    }
}
