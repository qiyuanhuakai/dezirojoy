using DeziroJoy.App.Services;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using DeziroJoy.Core.Profiles;
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
    public void A_Reconnect_After_Deleting_A_Default_Slot_Does_Not_Resurrect_It()
    {
        var store = new InMemoryProfileStore();
        var (host, provider, _) = CreateHost(store);
        var presets = new PresetStore(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        host.RefreshPresence();
        Assert.Equal([1, 2, 3, 4], presets.SlotsInUse("xinput:0"));

        // Delete a default slot while the device is connected.
        presets.DeleteSlot("xinput:0", 2);

        // Reconnect: the runtime is torn down and recreated through the real EnsureRuntime path,
        // which re-runs EnsureDefaults. The deleted default slot must stay deleted - the initial
        // set is materialized once, on the device's first use, and never again.
        provider.Remove("xinput:0");
        host.RefreshPresence();
        Assert.Empty(host.Controllers);

        provider.Add("xinput:0", "Xbox Wireless Controller");
        host.RefreshPresence();

        Assert.Single(host.Controllers);
        Assert.Equal([1, 3, 4], presets.SlotsInUse("xinput:0"));
        Assert.Null(presets.LoadSlot("xinput:0", 2));
    }

    [Fact]
    public void Reconnecting_A_Device_That_Lacks_The_Active_Slot_Falls_Back_To_A_Survivor()
    {
        var store = new InMemoryProfileStore();
        var (host, provider, _) = CreateHost(store);
        var presets = new PresetStore(store);
        provider.Add("pad-A", "Controller A");
        provider.Add("pad-B", "Controller B");
        host.RefreshPresence();

        // A deletes its non-active slot 2.
        presets.DeleteSlot("pad-A", 2);
        Assert.Equal([1, 3, 4], presets.SlotsInUse("pad-A"));

        // B selects the app-wide slot 2 - A lacks it, so A keeps running its own profile (slot 1)
        // while the app-wide scalar moves to 2.
        host.SwitchPresetSlot(2, new Dictionary<string, GamepadProfile>
        {
            ["pad-B"] = presets.LoadSlot("pad-B", 2)!,
        });
        Assert.Equal(2, host.ActivePresetSlot);
        Assert.Equal(1, host.Controllers.Single(c => c.Descriptor.DeviceId == "pad-A").Profile.PresetSlot);

        // Reconnect A: EnsureDefaults(A, 2) must fall back to a survivor (the primary), not throw,
        // not resurrect the deleted slot 2, and not move the app-wide scalar.
        provider.Remove("pad-A");
        host.RefreshPresence();
        provider.Add("pad-A", "Controller A");
        host.RefreshPresence();

        var reconnected = host.Controllers.Single(c => c.Descriptor.DeviceId == "pad-A");
        Assert.Equal(1, reconnected.Profile.PresetSlot);
        Assert.Equal([1, 3, 4], presets.SlotsInUse("pad-A"));
        Assert.Null(presets.LoadSlot("pad-A", 2));
        Assert.Equal(2, host.ActivePresetSlot);
    }

    [Fact]
    public void SwitchPresetSlot_Accepts_A_Slot_Beyond_The_Initial_Four()
    {
        var (host, provider, _) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        host.RefreshPresence();

        host.SwitchPresetSlot(5, new Dictionary<string, GamepadProfile>
        {
            ["xinput:0"] = new GamepadProfile
            {
                Name = "第五套",
                DeviceId = "xinput:0",
                Layout = GamepadLayout.Xbox,
                Mappings = [],
            },
        });

        Assert.Equal(5, host.ActivePresetSlot);
        Assert.Equal("第五套", host.Controllers.Single().Profile.Name);
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

    [Fact]
    public void A_Reconnect_Converges_Divergent_Special_Keys_Across_The_Devices_Slots()
    {
        var store = new InMemoryProfileStore();
        var (host, provider, _) = CreateHost(store);
        var presets = new PresetStore(store);
        var leader = new SpecialKeyTarget(SpecialKeyKind.Leader, PrecisionMode.Toggle, LeaderSwitchMode.Press, SlotA: 1, SlotB: 2);

        // A device whose slots predate the device-global special-key rule: only some slots
        // carry the leader, and slot 3's copy names a different pair.
        presets.SaveSlot("xinput:0", 1, new GamepadProfile
        {
            Name = "主配置",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings = [LeaderEntry(leader)],
        });
        presets.SaveSlot("xinput:0", 2, new GamepadProfile
        {
            Name = "第二套",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings = [],
        });
        presets.SaveSlot("xinput:0", 3, new GamepadProfile
        {
            Name = "第三套",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings = [LeaderEntry(leader with { SlotB = 4 })],
        });

        provider.Add("xinput:0", "Xbox Wireless Controller");
        host.RefreshPresence();

        // The runtime was handed the converged profile, and every stored slot agrees.
        var runtime = host.Controllers.Single();
        Assert.Equal(
            leader,
            runtime.Profile.Mappings.Single(m => m.Target is SpecialKeyTarget).Target);
        foreach (var slot in new[] { 1, 2, 3 })
        {
            Assert.Equal(
                leader,
                presets.LoadSlot("xinput:0", slot)!.Mappings.Single(m => m.Target is SpecialKeyTarget).Target);
        }
    }

    private static MappingEntry LeaderEntry(SpecialKeyTarget leader) => new()
    {
        Source = new ButtonBinding(GamepadButton.Back),
        Target = leader,
        Enabled = true,
    };

    private static GamepadProfile LeaderProfile(LeaderSwitchMode switchMode, params GamepadButton[] buttons)
        => LeaderProfile(switchMode, slotA: 1, slotB: 2, buttons);

    private static GamepadProfile LeaderProfile(LeaderSwitchMode switchMode, int slotA, int slotB, params GamepadButton[] buttons)
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
                        SlotA: slotA,
                        SlotB: slotB),
                }),
            ],
        };

    /// <summary>
    /// Wires the host's preset-switch event to apply the switch the way the GUI's local path
    /// and the daemon do - the slot's profile is reloaded into the device - and records every
    /// requested slot. This is the end-to-end shape of the reported bug: the switch lands on
    /// the tick thread, at the end of the very tick that asked for it, while the leader key is
    /// still held.
    /// </summary>
    private static List<int> ApplySwitchesLikeTheGui(EngineHost host, Dictionary<int, GamepadProfile> slotProfiles)
    {
        var requested = new List<int>();
        host.PresetSwitchRequested += (_, slot) =>
        {
            requested.Add(slot);
            if (slotProfiles.TryGetValue(slot, out var profile))
            {
                host.SwitchPresetSlot(slot, new Dictionary<string, GamepadProfile>(StringComparer.Ordinal)
                {
                    ["xinput:0"] = profile,
                });
            }
        };

        return requested;
    }

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
    public void A_Leader_Press_Switches_Exactly_Once_While_The_Button_Stays_Held()
    {
        var slots = new Dictionary<int, GamepadProfile>
        {
            [1] = LeaderProfile(LeaderSwitchMode.Press, 1, 2, GamepadButton.Back),
            [2] = LeaderProfile(LeaderSwitchMode.Press, 1, 2, GamepadButton.Back),
        };
        var store = new InMemoryProfileStore();
        store.Save(slots[1]);
        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        var requested = ApplySwitchesLikeTheGui(host, slots);

        host.TickOnce(Tick);
        Assert.Empty(requested);

        // One press asks for slot 2, and the switch lands on the same tick.
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2], requested);
        Assert.Equal(2, host.ActivePresetSlot);
        Assert.Equal(2, host.Controllers.Single().Profile.PresetSlot);

        // The button is still held across the reload: the fresh engine adopts the last polled
        // snapshot, so no second press edge is fabricated and no second switch is requested -
        // the parity-dependent double-toggle the user reported (切不过去 / 切两次).
        for (var i = 0; i < 5; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Equal([2], requested);

        // A Press leader emits nothing on the release edge.
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Equal([2], requested);

        // The second press round-trips to slot 1: one switch per press, two presses to return.
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2, 1], requested);
        Assert.Equal(1, host.Controllers.Single().Profile.PresetSlot);
    }

    [Fact]
    public void A_Hold_Leader_Enters_Once_And_Returns_Once_After_The_Reload()
    {
        var slots = new Dictionary<int, GamepadProfile>
        {
            [1] = LeaderProfile(LeaderSwitchMode.Hold, 1, 2, GamepadButton.Back),
            [2] = LeaderProfile(LeaderSwitchMode.Hold, 1, 2, GamepadButton.Back),
        };
        var store = new InMemoryProfileStore();
        store.Save(slots[1]);
        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        var requested = ApplySwitchesLikeTheGui(host, slots);

        host.TickOnce(Tick);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2], requested);

        // Held across the reload: the fresh engine sees no new press edge, so the hold does
        // not re-engage on every tick.
        for (var i = 0; i < 5; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Equal([2], requested);

        // The release edge survived the reload, so the hold returns to slot 1 exactly once -
        // the lost release that used to strand the engine on slot 2.
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Equal([2, 1], requested);
        Assert.Equal(1, host.Controllers.Single().Profile.PresetSlot);
    }

    [Fact]
    public void A_Chained_Leader_Moves_One_Slot_Per_Press()
    {
        // Slot 1 flips 1<->2 and slot 2 flips 2<->3: each press must move exactly one step
        // along the chain, never two in a single hold.
        var slots = new Dictionary<int, GamepadProfile>
        {
            [1] = LeaderProfile(LeaderSwitchMode.Press, 1, 2, GamepadButton.Back),
            [2] = LeaderProfile(LeaderSwitchMode.Press, 2, 3, GamepadButton.Back),
            [3] = LeaderProfile(LeaderSwitchMode.Press, 2, 3, GamepadButton.Back),
        };
        var store = new InMemoryProfileStore();
        store.Save(slots[1]);
        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);

        var requested = ApplySwitchesLikeTheGui(host, slots);

        host.TickOnce(Tick);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        for (var i = 0; i < 3; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Equal([2], requested);

        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2, 3], requested);
        Assert.Equal(3, host.Controllers.Single().Profile.PresetSlot);
    }

    [Fact]
    public void A_Device_Missing_The_Target_Slot_Keeps_Its_Configuration_And_Its_Leader_Latch()
    {
        var store = new InMemoryProfileStore();
        var presets = new PresetStore(store);
        // pad-A holds slots 1 and 2; pad-B holds only its primary, so a switch to slot 2 can
        // never move it.
        presets.SaveSlot("pad-A", 1, LeaderProfile(LeaderSwitchMode.Press, GamepadButton.Back));
        presets.SaveSlot("pad-A", 2, LeaderProfile(LeaderSwitchMode.Press, GamepadButton.Back));
        presets.SaveSlot("pad-B", 1, LeaderProfile(LeaderSwitchMode.Press, GamepadButton.Back));

        var (host, provider, _) = CreateHost(store);
        provider.Add("pad-A", "Controller A");
        provider.Add("pad-B", "Controller B");
        provider.SetState("pad-A", GamepadState.Neutral);
        provider.SetState("pad-B", GamepadState.Neutral);
        host.RefreshPresence();

        var requested = new List<int>();
        host.PresetSwitchRequested += (_, slot) => requested.Add(slot);

        // B presses its leader: it asks for slot 2, but nothing answers - B holds no slot 2 -
        // so its latch is left flipped, exactly the stale state a refusing handler produces.
        provider.SetState("pad-B", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2], requested);

        provider.SetState("pad-B", GamepadState.Neutral);
        host.TickOnce(Tick);

        // The app moves to slot 2: A is reloaded onto it, B keeps running its own profile.
        host.SwitchPresetSlot(2, new Dictionary<string, GamepadProfile>(StringComparer.Ordinal)
        {
            ["pad-A"] = presets.LoadSlot("pad-A", 2)!,
        });
        Assert.Equal(2, host.ActivePresetSlot);
        Assert.Equal(1, host.Controllers.Single(c => c.Descriptor.DeviceId == "pad-B").Profile.PresetSlot);

        // The switch re-anchored B's latch to the slot it truly runs, so its next press asks
        // for slot 2 again rather than a no-op switch back to the slot it is already on.
        provider.SetState("pad-B", GamepadState.Create(GamepadButtonMask.Back, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal([2, 2], requested);
    }

    [Fact]
    public void A_Reload_Does_Not_Re_Press_Keys_Held_Across_It()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);

        // An edit lands while the key is still held. The reload releases everything it held
        // and the fresh engine adopts the last polled snapshot as its baseline, so the held
        // key does not fabricate a new press edge and is not silently re-pressed.
        host.Reload("xinput:0", DefaultProfileLibrary.CreateForLayout(GamepadLayout.Xbox) with
        {
            DeviceId = "xinput:0",
        });
        sink.Calls.Clear();

        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        // The release ends the hold the old engine left open, but the fresh dispatcher never
        // pressed the key, so its held-key tracking drops the redundant up: nothing reaches
        // the sink, and no key is left stuck.
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Empty(sink.Calls);

        // A fresh press fires the reloaded mapping again.
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
    }

    [Fact]
    public void A_Switch_To_The_Slot_A_Device_Already_Runs_Leaves_Its_Runtime_Alone()
    {
        var (host, provider, sink) = CreateHost();
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));

        host.TickOnce(Tick);
        Assert.Contains("down:Enter", sink.Calls);
        var runtime = host.Controllers.Single();

        // The device runs slot 1 and is asked to switch to slot 1: reloading it would recreate
        // its engine and release the key it is holding to change nothing at all.
        host.SwitchPresetSlot(1, new Dictionary<string, GamepadProfile>(StringComparer.Ordinal)
        {
            ["xinput:0"] = DefaultProfileLibrary.CreateForLayout(GamepadLayout.Xbox) with { DeviceId = "xinput:0" },
        });

        Assert.Same(runtime, host.Controllers.Single());
        Assert.DoesNotContain("up:Enter", sink.Calls);
        Assert.Equal(1, host.ActivePresetSlot);
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

    [Fact]
    public void Two_Pads_Outputting_One_Key_Do_Not_Steal_It_From_Each_Other()
    {
        var store = new InMemoryProfileStore();
        store.Save(SharedCtrlProfile("xinput:0"));
        store.Save(ComboProfile("xinput:1"));

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.Add("xinput:1", "DualShock 4");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));
        provider.SetState("xinput:1", GamepadState.Neutral);

        host.TickOnce(Tick);
        Assert.Equal(["down:LeftCtrl"], sink.Calls);

        // Pad B taps a Ctrl+C combo while pad A still holds Ctrl: the combo's end used to
        // send Ctrl Up, dropping the modifier the other pad was holding.
        provider.SetState("xinput:1", GamepadState.Create(GamepadButtonMask.X, 0, 0, 0, 0));
        host.TickOnce(Tick);
        provider.SetState("xinput:1", GamepadState.Neutral);
        host.TickOnce(Tick);

        Assert.Equal(["down:LeftCtrl", "down:C", "up:C"], sink.Calls);

        // Pad A releasing its bumper is what finally lifts Ctrl.
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);

        Assert.Equal(["down:LeftCtrl", "down:C", "up:C", "up:LeftCtrl"], sink.Calls);
    }

    private static GamepadProfile SharedCtrlProfile(string deviceId) => new()
    {
        Name = "shared-ctrl",
        DeviceId = deviceId,
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.LeftBumper),
                Target = new KeyPressTarget(GamepadKey.LeftCtrl),
            },
        ],
    };

    private static GamepadProfile ComboProfile(string deviceId) => new()
    {
        Name = "combo",
        DeviceId = deviceId,
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.X),
                Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
            },
        ],
    };

    [Fact]
    public void A_Disconnect_Releases_Only_That_Devices_Holds()
    {
        var store = new InMemoryProfileStore();
        store.Save(SharedCtrlProfile("xinput:0"));
        store.Save(new GamepadProfile
        {
            Name = "space",
            DeviceId = "xinput:1",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.LeftBumper),
                    Target = new KeyPressTarget(GamepadKey.Space),
                },
            ],
        });

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.Add("xinput:1", "DualShock 4");
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));
        provider.SetState("xinput:1", GamepadState.Create(GamepadButtonMask.LeftBumper, 0, 0, 0, 0));

        host.TickOnce(Tick);
        Assert.Equal(["down:LeftCtrl", "down:Space"], sink.Calls);

        // Pad A goes away: its hold is lifted, and pad B's is not - the two devices share one
        // session-wide ownership table, so a revoked owner never lifts a key it does not own.
        provider.Remove("xinput:0");
        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Equal(["down:LeftCtrl", "down:Space", "up:LeftCtrl"], sink.Calls);
    }

    [Fact]
    public void A_Gesture_Mouse_Move_Survives_A_Pause_And_A_Gui_Takeover()
    {
        var store = new InMemoryProfileStore();
        store.Save(GestureMouseProfile());

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);

        DoubleClick(provider, host);
        Assert.Contains("move:10,0", sink.Calls);

        // The processors a gesture action ramps through are registered when the engine is
        // built, so a pause (or a GUI takeover) must not unregister them: the gesture's mouse
        // move behaves identically on every cycle.
        host.SetMappingEnabled(false);
        host.SetMappingEnabled(true);
        sink.Calls.Clear();

        DoubleClick(provider, host);
        Assert.Equal(["move:10,0"], sink.Calls);

        host.SetInjectionSuspended(true);
        host.SetInjectionSuspended(false);
        sink.Calls.Clear();

        DoubleClick(provider, host);
        Assert.Equal(["move:10,0"], sink.Calls);
    }

    private static GamepadProfile GestureMouseProfile() => new()
    {
        Name = "gesture-mouse",
        DeviceId = "xinput:0",
        Layout = GamepadLayout.Xbox,
        Mappings =
        [
            new MappingEntry
            {
                Source = new ButtonBinding(GamepadButton.B)
                {
                    Direction = InputDirection.Right,
                    Gesture = new ButtonGesture
                    {
                        DoubleClickWindow = 0.25f,
                        DoubleClickAction = new MouseMoveTarget(new MouseStickSettings
                        {
                            DeadZone = 0f,
                            MaxSpeed = 640f,
                            Curve = ResponseCurve.Linear,
                            CurveStrength = 0f,
                            ResponseTime = 0f,
                            InvertY = false,
                        }),
                    },
                },
                Target = new NoneTarget(),
            },
        ],
    };

    /// <summary>One double-click driven through the provider, at the host's own tick rate.</summary>
    private static void DoubleClick(FakeProvider provider, EngineHost host)
    {
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        host.TickOnce(Tick);
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.B, 0, 0, 0, 0));
        host.TickOnce(Tick);
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
    }

    [Fact]
    public void A_Temporary_Layer_Keeps_A_Drag_Continuous_And_Leaves_No_Residue()
    {
        var store = new InMemoryProfileStore();
        store.Save(LayerProfile());

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);

        // RT starts a drag on the base layer.
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.RightTrigger, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal(["mdown:Left"], sink.Calls);

        // Hold the leader to run other commands while the window is being dragged: the left
        // mouse button must not come up, and the layer's own X mapping runs beside the drag.
        provider.SetState(
            "xinput:0",
            GamepadState.Create(GamepadButtonMask.RightTrigger | GamepadButtonMask.RightBumper, 0, 0, 0, 0));
        host.TickOnce(Tick);
        provider.SetState(
            "xinput:0",
            GamepadState.Create(
                GamepadButtonMask.RightTrigger | GamepadButtonMask.RightBumper | GamepadButtonMask.X,
                0,
                0,
                0,
                0));
        host.TickOnce(Tick);
        Assert.Equal(["mdown:Left", "down:LeftAlt"], sink.Calls);

        // Releasing the leader lifts the layer's hold and nothing else: the drag is still in
        // progress, so no Ctrl/Alt/mouse residue is left behind.
        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.RightTrigger, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal(["mdown:Left", "down:LeftAlt", "up:LeftAlt"], sink.Calls);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.RightTrigger, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal(["mdown:Left", "down:LeftAlt", "up:LeftAlt"], sink.Calls);

        // Only releasing RT ends the drag.
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);
        Assert.Equal(["mdown:Left", "down:LeftAlt", "up:LeftAlt", "mup:Left"], sink.Calls);
    }

    [Fact]
    public void A_Disconnect_Or_An_Abnormal_Exit_Releases_What_The_Layer_Held()
    {
        var store = new InMemoryProfileStore();
        store.Save(LayerProfile());

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);

        // The layer is active and holds LeftAlt.
        provider.SetState(
            "xinput:0",
            GamepadState.Create(GamepadButtonMask.RightBumper | GamepadButtonMask.X, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal(["down:LeftAlt"], sink.Calls);

        // The pad goes away: the layer's hold is lifted, so no modifier is left stuck down.
        provider.Remove("xinput:0");
        for (var i = 0; i < EngineHost.DeviceAbsenceGraceTicks + 1; i++)
        {
            host.TickOnce(Tick);
        }

        Assert.Equal(["down:LeftAlt", "up:LeftAlt"], sink.Calls);
    }

    [Fact]
    public void Stopping_The_Host_Releases_What_The_Layer_Held()
    {
        var store = new InMemoryProfileStore();
        store.Save(LayerProfile());

        var (host, provider, sink) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.StartManual();
        host.TickOnce(Tick);

        provider.SetState(
            "xinput:0",
            GamepadState.Create(GamepadButtonMask.RightBumper | GamepadButtonMask.X, 0, 0, 0, 0));
        host.TickOnce(Tick);
        Assert.Equal(["down:LeftAlt"], sink.Calls);

        // An abnormal exit - the process is going away - is the ReleaseAll safety net.
        host.Stop();

        Assert.Equal(["down:LeftAlt", "up:LeftAlt"], sink.Calls);
        Assert.False(host.IsRunning);
    }

    private static (GamepadProfile Profile, Guid DragId, Guid BaseXId, Guid LayerXId) LayerProfileWithIds()
    {
        var dragId = Guid.NewGuid();
        var baseXId = Guid.NewGuid();
        var layerXId = Guid.NewGuid();

        var profile = new GamepadProfile
        {
            Name = "layer",
            DeviceId = "xinput:0",
            Layout = GamepadLayout.Xbox,
            Mappings =
            [
                new MappingEntry
                {
                    Id = dragId,
                    Source = new ButtonBinding(GamepadButton.RightTrigger),
                    Target = new MouseButtonTarget(MouseButton.Left),
                },
                new MappingEntry
                {
                    Id = baseXId,
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C),
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.RightBumper),
                    Target = new SpecialKeyTarget(SpecialKeyKind.Layer, SwitchMode: LeaderSwitchMode.Hold, LayerId: 2),
                },
                new MappingEntry
                {
                    Id = layerXId,
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyPressTarget(GamepadKey.LeftAlt),
                    Layer = 2,
                },
            ],
        };

        return (profile, dragId, baseXId, layerXId);
    }

    private static GamepadProfile LayerProfile() => LayerProfileWithIds().Profile;

    [Fact]
    public void The_Host_Reports_The_Active_Layer_And_Its_Effective_Mappings()
    {
        var (profile, dragId, baseXId, layerXId) = LayerProfileWithIds();
        var store = new InMemoryProfileStore();
        store.Save(profile);

        var (host, provider, _) = CreateHost(store);
        provider.Add("xinput:0", "Xbox Wireless Controller");
        provider.SetState("xinput:0", GamepadState.Neutral);
        host.TickOnce(Tick);

        var layer = Assert.Single(host.ActiveLayers);
        Assert.Equal("xinput:0", layer.DeviceId);
        Assert.Equal(0, layer.LayerId);
        Assert.Contains(layer.Mappings, m => m.MappingId == baseXId && m.Layer == 0);
        Assert.DoesNotContain(layer.Mappings, m => m.MappingId == layerXId);

        provider.SetState("xinput:0", GamepadState.Create(GamepadButtonMask.RightBumper, 0, 0, 0, 0));
        host.TickOnce(Tick);

        layer = Assert.Single(host.ActiveLayers);
        Assert.Equal(2, layer.LayerId);

        // The layer's own X mapping is in effect, the base one is not, and the drag mapping
        // the layer never overrode is still the base layer's own.
        Assert.Contains(layer.Mappings, m => m.MappingId == layerXId && m.Layer == 2);
        Assert.DoesNotContain(layer.Mappings, m => m.MappingId == baseXId);
        Assert.Contains(layer.Mappings, m => m.MappingId == dragId && m.Layer == 0);
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
