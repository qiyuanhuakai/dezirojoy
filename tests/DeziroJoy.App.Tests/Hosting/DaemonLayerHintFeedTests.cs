using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Engine;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Platform;
using Xunit;

namespace DeziroJoy.App.Tests.Hosting;

/// <summary>
/// The tray process owns no window and no view model, so <see cref="DaemonLayerHintFeed"/> is
/// the seam that turns daemon snapshots and input samples into the two view models the tray's
/// layer hint overlay binds. It mirrors how the render window drives its own overlay, so these
/// tests run the real controller over a real server: a snapshot with an active layer fills the
/// hint, an input sample lights the pad diagram, and losing the daemon empties the hint.
/// </summary>
public sealed class DaemonLayerHintFeedTests
{
    private static readonly TimeSpan EngineTick = TimeSpan.FromSeconds(1.0 / 64.0);

    private static MappingEntry OnLayer(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target, Layer = 2 };

    [Fact]
    public async Task A_Snapshot_With_An_Active_Layer_Fills_The_Tray_Hint()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        context.Layers.Add(new ActiveLayerSnapshot("pad-1", 2, EffectiveIds(context.Profile, 2)));
        await BroadcastAsync(context.Controller);
        await WaitForCondition(() => context.LayerHint.IsActive);

        Assert.Equal("临时层 2", context.LayerHint.LayerTitle);
        Assert.Contains(new LayerHintRow("X", "复制"), context.LayerHint.Rows);
    }

    [Fact]
    public async Task A_Layer_Activating_On_The_Engine_Reaches_The_Tray_Hint_On_Its_Own()
    {
        var context = await CreateEngineFeed();
        using var feed = context.Feed;

        // The leader goes down: the engine's temporary layer activates and nothing else about
        // the daemon's state changes - no revision, no profile, no device. Before the
        // layer-state publish the overlay only ever learned the layer was active when some
        // unrelated mutation happened to push a snapshot, which is why it appeared only rarely.
        context.Provider.SetState("pad-1", GamepadState.Create(GamepadButtonMask.RightBumper, 0, 0, 0, 0));
        context.Host.TickOnce(EngineTick);

        await WaitForCondition(() => context.LayerHint.IsActive);
        Assert.Equal("临时层 1", context.LayerHint.LayerTitle);
        Assert.Contains(new LayerHintRow("X", "B"), context.LayerHint.Rows);

        // The leader comes up: the layer releases, and the hint follows it down without anyone
        // touching the daemon's configuration.
        context.Provider.SetState("pad-1", GamepadState.Neutral);
        context.Host.TickOnce(EngineTick);

        await WaitForCondition(() => !context.LayerHint.IsActive);
        Assert.Empty(context.LayerHint.Rows);
    }

    [Fact]
    public async Task An_Input_Sample_Feeds_The_Tray_Pad_Diagram()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        await WaitForCondition(() => context.Visualizer.IsAPressed);

        Assert.True(context.Visualizer.IsConnected);
    }

    [Fact]
    public async Task A_Frames_Worth_Of_Samples_Dispatches_Once_To_The_Tray_Pad_Diagram()
    {
        // The tray is resident and the daemon's samples outrun its refresh, so the feed keeps
        // the sample that arrived last - the diagram is one pad's picture - and lets the frame
        // clock post one update for the whole frame instead of one per sample.
        var context = await CreateFeed(clock: new FakeFrameClock());
        using var feed = context.Feed;

        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.B, 0f, 0f, 0f, 0f));
        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.X, 0f, 0f, 0f, 0f));
        await WaitForSample(context.Controller, "pad-1", GamepadButtonMask.X);

        // Nothing dispatched before the frame: the samples a frame coalesced away were never
        // displayed states.
        Assert.False(context.Visualizer.IsAPressed);
        Assert.False(context.Visualizer.IsBPressed);
        Assert.False(context.Visualizer.IsXPressed);

        context.Clock!.RaiseTick();

        // One dispatch, carrying the latest sample.
        Assert.True(context.Visualizer.IsXPressed);
        Assert.False(context.Visualizer.IsAPressed);
        Assert.True(context.Visualizer.IsConnected);
    }

    [Fact]
    public async Task Losing_The_Daemon_Empties_The_Tray_Hint()
    {
        var context = await CreateFeed();
        var feed = context.Feed;

        context.Layers.Add(new ActiveLayerSnapshot("pad-1", 2, EffectiveIds(context.Profile, 2)));
        await BroadcastAsync(context.Controller);
        await WaitForCondition(() => context.LayerHint.IsActive);

        // The disconnect reaches the feed asynchronously (channel -> receive loop -> Disconnected),
        // so the wait matches the target state rather than the moment the channel closed.
        await context.Channel.DisposeAsync();
        await WaitForCondition(() => !context.LayerHint.IsActive);

        feed.Dispose();
    }

    /// <summary>The cheapest mutation that makes the daemon broadcast its current layer state.</summary>
    private static Task<MutationResult> BroadcastAsync(DaemonController controller)
        => controller.SetMappingEnabledAsync(false, TestContext.Current.CancellationToken);
    private static async Task WaitForCondition(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// Waits until the controller has taken the given sample. The controller stores the latest
    /// sample per device before raising the event, so a match also means the feed has already
    /// queued it.
    /// </summary>
    private static async Task WaitForSample(
        DaemonController controller,
        string deviceId,
        GamepadButtonMask pressed)
    {
        await WaitForCondition(() => controller.InputSamples.Any(sample => sample.DeviceId == deviceId
            && sample.PressedButtons == pressed));
    }

    private static List<EffectiveMappingSnapshot> EffectiveIds(GamepadProfile profile, int layerId)
    {
        var effective = profile.Mappings
            .Select(mapping => new EffectiveMappingSnapshot(mapping.Id, mapping.Layer))
            .ToList();
        effective.Insert(0, new EffectiveMappingSnapshot(Guid.NewGuid(), 0));
        return effective;
    }

    private static async Task<FeedContext> CreateFeed(FakeFrameClock? clock = null)
    {
        var profile = new GamepadProfile
        {
            Name = "layer",
            DeviceId = "pad-1",
            Mappings =
            [
                OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)),
                OnLayer(GamepadButton.Y, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.V)),
            ],
        };

        var host = new FakeDaemonHost { HasController = true };
        host.AddDevice("pad-1");
        host.Reload("pad-1", profile);
        var store = new InMemoryProfileStore();
        var layers = new List<ActiveLayerSnapshot>();
        var statePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dezirojoy-tray-feed-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(host, store, new DaemonStateStore(statePath));
        server.LayerStateSource = () => layers;
        var channel = new FeedServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new FeedServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var layerHint = new LayerHintViewModel();
        var visualizer = new GamepadVisualizerViewModel();
        var feed = new TrayHost.DaemonLayerHintFeed(controller, layerHint, visualizer, clock);
        return new FeedContext(controller, feed, host, layers, profile, channel, layerHint, visualizer, clock);
    }

    /// <summary>
    /// The production wiring in miniature: a real engine host behind the daemon's host seam,
    /// with the layer state source and the layer-change publish the daemon wires up in
    /// <see cref="DaemonHost.RunOwned"/>. Driving a real tick is what proves the whole chain -
    /// engine, daemon, controller, feed, hint - rather than each seam on its own.
    /// </summary>
    private static async Task<EngineFeedContext> CreateEngineFeed()
    {
        var profile = new GamepadProfile
        {
            Name = "layer",
            DeviceId = "pad-1",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.X),
                    Target = new KeyPressTarget(GamepadKey.B),
                    Layer = 1,
                },
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.RightBumper),
                    Target = new SpecialKeyTarget(
                        SpecialKeyKind.Leader, SwitchMode: LeaderSwitchMode.Hold, Action: LeaderAction.Layer),
                },
            ],
        };

        var store = new InMemoryProfileStore();
        store.Save(profile);
        var provider = new FakeProvider();
        provider.Add("pad-1");
        provider.SetState("pad-1", GamepadState.Neutral);
        var host = new EngineHost(provider, new NullOutputSink(), store);

        var statePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dezirojoy-tray-engine-feed-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(new EngineHostAdapter(host), store, new DaemonStateStore(statePath));
        server.LayerStateSource = () => host.ActiveLayers;
        host.ActiveLayersChanged += () => server.PublishLayerState();

        var channel = new FeedServerChannel(server);
        server.StateChanged += channel.Push;
        server.InputSampled += channel.Push;
        var controller = new DaemonController(new FeedServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var layerHint = new LayerHintViewModel();
        var visualizer = new GamepadVisualizerViewModel();
        var feed = new TrayHost.DaemonLayerHintFeed(controller, layerHint, visualizer);
        return new EngineFeedContext(controller, feed, host, provider, layerHint);
    }

    private sealed record EngineFeedContext(
        DaemonController Controller,
        TrayHost.DaemonLayerHintFeed Feed,
        EngineHost Host,
        FakeProvider Provider,
        LayerHintViewModel LayerHint);

    private sealed class FakeProvider : IGamepadProvider
    {
        private readonly Dictionary<string, IGamepadState> _states = [];

        public void Add(string deviceId) => _states.TryAdd(deviceId, GamepadState.Neutral);

        public void SetState(string deviceId, IGamepadState state) => _states[deviceId] = state;

        public IReadOnlyList<GamepadDescriptor> GetConnected()
            => _states.Keys.Select(deviceId => new GamepadDescriptor(deviceId, "Test Pad")).ToList();

        public IGamepadState? Poll(string deviceId) => _states.GetValueOrDefault(deviceId);

        public void Dispose()
        {
        }
    }

    /// <summary>A real engine host behind the daemon's host seam, as <see cref="DaemonHost"/> builds it.</summary>
    private sealed class EngineHostAdapter(EngineHost host) : IDaemonHost
    {
        public bool IsRunning => host.IsRunning;

        public bool IsMappingEnabled => host.IsMappingEnabled;

        public bool IsInjectionSuspended => host.IsInjectionSuspended;

        public bool IsNativeGamepadPaused => host.IsNativeGamepadPaused;

        public bool HasController => host.Controllers.Count > 0;

        public int InjectorPid => 4242;

        public string? InjectorIntegrity => "high";

        public long InjectedEvents => 0;

        public long BlockedEvents => 0;

        public IReadOnlyList<string> ConnectedDeviceIds
            => host.Controllers.Select(runtime => runtime.Descriptor.DeviceId).ToList();

        public IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices
            => host.Controllers.Select(runtime => new ConnectedDeviceSnapshot(
                runtime.Descriptor.DeviceId,
                runtime.Descriptor.Name,
                runtime.Descriptor.Layout)).ToList();

        public event Action? DevicesChanged
        {
            add => host.ControllersChanged += value;
            remove => host.ControllersChanged -= value;
        }

        public event Action<string, IGamepadState>? InputSampled
        {
            add => _inputSampled += value;
            remove => _inputSampled -= value;
        }

        private event Action<string, IGamepadState>? _inputSampled;

        private void OnSampled(object? sender, ControllerSampledEventArgs args)
            => _inputSampled?.Invoke(args.Runtime.Descriptor.DeviceId, args.State);

        public GamepadProfile? GetProfile(string deviceId)
            => host.Controllers.SingleOrDefault(
                runtime => runtime.Descriptor.DeviceId == deviceId)?.Profile;

        public void SamplePresence() => host.SamplePresence();

        public void Start() => host.StartManual();

        public void StopMapping() => host.Stop();

        public void SetMappingEnabled(bool enabled) => host.SetMappingEnabled(enabled);

        public void SetInjectionSuspended(bool suspended) => host.SetInjectionSuspended(suspended);

        public void SetNativeGamepadPaused(bool paused) => host.SetNativeGamepadPaused(paused);

        public void Reload(string deviceId, GamepadProfile profile) => host.Reload(deviceId, profile);

        public void SwitchPresetSlot(int slot, IReadOnlyDictionary<string, GamepadProfile> profiles)
            => host.SwitchPresetSlot(slot, profiles);

        public void InjectText(string text) => host.InjectText(text);

        public void TapKey(GamepadKey key, KeyModifiers modifiers) => host.TapKey(key, modifiers);
    }

    private sealed record FeedContext(
        DaemonController Controller,
        TrayHost.DaemonLayerHintFeed Feed,
        FakeDaemonHost Host,
        List<ActiveLayerSnapshot> Layers,
        GamepadProfile Profile,
        FeedServerChannel Channel,
        LayerHintViewModel LayerHint,
        GamepadVisualizerViewModel Visualizer,
        FakeFrameClock? Clock);

    /// <summary>A ready endpoint over one in-memory channel, so no pipe is opened.</summary>
    private sealed class FeedServerEndpoint(FeedServerChannel channel) : IDaemonEndpoint
    {
        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChannel>(channel);
    }

    /// <summary>
    /// One client side of the daemon server: answers are produced synchronously on send and
    /// pushed broadcasts queue behind them, exactly like the named-pipe transport delivers them
    /// in order.
    /// </summary>
    private sealed class FeedServerChannel(DaemonServer server) : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            var answer = await server.ProcessAsync(message);
            Push(answer);
        }

        public Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    return Task.FromResult(_incoming.Dequeue());
                }

                if (_closed)
                {
                    return Task.FromException<IpcMessage>(new EndOfStreamException());
                }

                _waiting ??= new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                return AwaitReceiveAsync(_waiting.Task, cancellationToken);
            }
        }

        public void Push(IpcMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue(message);
                _waiting?.TrySetResult(message);
                _waiting = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }

            return ValueTask.CompletedTask;
        }

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
