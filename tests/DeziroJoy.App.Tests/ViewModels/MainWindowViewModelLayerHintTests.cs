using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The layer hint overlay is driven by the daemon's snapshot: while a device runs a
/// temporary layer, the view model feeds the hint the layer's own mappings; the moment the
/// layer is released - or the daemon is lost - the hint goes empty, which hides the
/// overlay. The daemon is the layer authority, so these tests run the real controller over
/// a real server rather than setting state on the view model directly.
/// </summary>
public sealed class MainWindowViewModelLayerHintTests
{
    private static MappingEntry OnLayer(GamepadButton button, MappingTarget target)
        => new() { Source = new ButtonBinding(button), Target = target, Layer = 2 };

    [Fact]
    public async Task A_Snapshot_With_An_Active_Layer_Fills_The_Hint()
    {
        var context = await CreateLayerViewModel();
        await using var viewModel = context.ViewModel;

        context.Layers.Add(new ActiveLayerSnapshot("pad-1", 2, EffectiveIds(context.Profile, 2)));
        await PushStateAsync(viewModel);
        await WaitForCondition(() => viewModel.LayerHint.IsActive);

        Assert.Equal("临时层 2", viewModel.LayerHint.LayerTitle);
        Assert.Equal(
            [
                new LayerHintRow("X", "复制"),
                new LayerHintRow("Y", "粘贴"),
                new LayerHintRow("A", "撤销"),
                new LayerHintRow("B", "保存"),
            ],
            viewModel.LayerHint.Rows);
    }

    [Fact]
    public async Task Releasing_The_Layer_Hides_The_Hint()
    {
        var context = await CreateLayerViewModel();
        await using var viewModel = context.ViewModel;

        context.Layers.Add(new ActiveLayerSnapshot("pad-1", 2, EffectiveIds(context.Profile, 2)));
        await PushStateAsync(viewModel);
        await WaitForCondition(() => viewModel.LayerHint.IsActive);

        context.Layers.Clear();
        await PushStateAsync(viewModel);
        await WaitForCondition(() => !viewModel.LayerHint.IsActive);

        Assert.Empty(viewModel.LayerHint.Rows);
        Assert.Equal(string.Empty, viewModel.LayerHint.LayerTitle);
    }

    [Fact]
    public async Task Losing_The_Daemon_Hides_The_Hint()
    {
        var context = await CreateLayerViewModel();
        var viewModel = context.ViewModel;

        context.Layers.Add(new ActiveLayerSnapshot("pad-1", 2, EffectiveIds(context.Profile, 2)));
        await PushStateAsync(viewModel);
        await WaitForCondition(() => viewModel.LayerHint.IsActive);

        // The disconnect reaches the view model asynchronously (channel -> receive loop ->
        // Disconnected event), so the wait matches the target state rather than the moment
        // the channel closed.
        await context.Channel.DisposeAsync();
        await WaitForCondition(() => !viewModel.LayerHint.IsActive);

        await viewModel.DisposeAsync();
    }

    /// <summary>
    /// The layer's own mapping ids as the daemon reports them: every layer-2 entry, plus the
    /// base drag mapping the layer inherits, so the hint is built from the same effective
    /// list the engine publishes.
    /// </summary>
    private static List<EffectiveMappingSnapshot> EffectiveIds(GamepadProfile profile, int layerId)
    {
        var effective = profile.Mappings
            .Select(mapping => new EffectiveMappingSnapshot(mapping.Id, mapping.Layer))
            .ToList();
        effective.Insert(0, new EffectiveMappingSnapshot(Guid.NewGuid(), 0));
        return effective;
    }

    /// <summary>
    /// A pause toggle republished as a snapshot: the cheapest mutation that makes the daemon
    /// broadcast its current state, layer state source included.
    /// </summary>
    private static async Task PushStateAsync(MainWindowViewModel viewModel)
        => await viewModel.ToggleMappingCommand.ExecuteAsync(null);

    private static async Task WaitForCondition(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static async Task<(MainWindowViewModel ViewModel, GamepadProfile Profile, List<ActiveLayerSnapshot> Layers, LayerHintServerChannel Channel)>
        CreateLayerViewModel()
    {
        var profile = new GamepadProfile
        {
            Name = "layer",
            DeviceId = "pad-1",
            Mappings =
            [
                new MappingEntry
                {
                    Source = new ButtonBinding(GamepadButton.RightTrigger),
                    Target = new MouseButtonTarget(MouseButton.Left),
                },
                OnLayer(GamepadButton.X, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.C)),
                OnLayer(GamepadButton.Y, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.V)),
                OnLayer(GamepadButton.A, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.Z)),
                OnLayer(GamepadButton.B, new KeyComboTarget(KeyModifiers.Ctrl, GamepadKey.S)),
            ],
        };

        var daemonHost = new FakeDaemonHost { HasController = true };
        daemonHost.AddDevice("pad-1");
        daemonHost.Reload("pad-1", profile);
        var store = new InMemoryProfileStore();
        var layers = new List<ActiveLayerSnapshot>();
        var statePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"dezirojoy-{Guid.NewGuid():N}.json");
        var server = new DaemonServer(daemonHost, store, new DaemonStateStore(statePath));
        server.LayerStateSource = () => layers;
        var channel = new LayerHintServerChannel(server);
        server.StateChanged += channel.Push;
        var controller = new DaemonController(new LayerHintServerEndpoint(channel));
        await controller.ConnectAsync(TestContext.Current.CancellationToken);

        var presentation = new EngineHost(new NoGamepadProvider(), new RecordingOutputSink(), store);
        var viewModel = new MainWindowViewModel(presentation, store, daemon: controller, daemonRequired: true);
        return (viewModel, profile, layers, channel);
    }

    /// <summary>A ready endpoint over one in-memory channel, so no pipe is opened.</summary>
    private sealed class LayerHintServerEndpoint(LayerHintServerChannel channel) : IDaemonEndpoint
    {
        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
            => Task.FromResult<IDaemonChannel>(channel);
    }

    /// <summary>
    /// One client side of the daemon server: answers are produced synchronously on send and
    /// pushed broadcasts queue behind them, exactly like the named-pipe transport delivers
    /// them in order.
    /// </summary>
    private sealed class LayerHintServerChannel(DaemonServer server) : IDaemonChannel
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
