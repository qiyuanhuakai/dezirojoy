using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.Tests.Ipc;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
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
    public async Task An_Input_Sample_Feeds_The_Tray_Pad_Diagram()
    {
        var context = await CreateFeed();
        using var feed = context.Feed;

        context.Host.PublishInputSample("pad-1", GamepadState.Create(GamepadButtonMask.A, 0f, 0f, 0f, 0f));
        await WaitForCondition(() => context.Visualizer.IsAPressed);

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

    private static List<EffectiveMappingSnapshot> EffectiveIds(GamepadProfile profile, int layerId)
    {
        var effective = profile.Mappings
            .Select(mapping => new EffectiveMappingSnapshot(mapping.Id, mapping.Layer))
            .ToList();
        effective.Insert(0, new EffectiveMappingSnapshot(Guid.NewGuid(), 0));
        return effective;
    }

    private static async Task<FeedContext> CreateFeed()
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
        var feed = new TrayHost.DaemonLayerHintFeed(controller, layerHint, visualizer);
        return new FeedContext(controller, feed, host, layers, profile, channel, layerHint, visualizer);
    }

    private sealed record FeedContext(
        DaemonController Controller,
        TrayHost.DaemonLayerHintFeed Feed,
        FakeDaemonHost Host,
        List<ActiveLayerSnapshot> Layers,
        GamepadProfile Profile,
        FeedServerChannel Channel,
        LayerHintViewModel LayerHint,
        GamepadVisualizerViewModel Visualizer);

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
