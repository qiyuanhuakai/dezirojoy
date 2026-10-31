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
        host.TickOnce(Tick);

        Assert.Empty(host.Controllers);
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

    private sealed class InMemoryProfileStore : IProfileStore
    {
        private readonly Dictionary<string, GamepadProfile> _profiles = [];

        public GamepadProfile? Load(string deviceId) => _profiles.GetValueOrDefault(deviceId);

        public void Save(GamepadProfile profile) => _profiles[profile.DeviceId ?? profile.Name] = profile;

        public bool Delete(string deviceId) => _profiles.Remove(deviceId);

        public IReadOnlyList<GamepadProfile> LoadAll() => _profiles.Values.ToList();
    }
}
