using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
/// The resident host builds each floating surface only while the daemon calls for it: the
/// connection is the one resident piece, and the layer hint and the screen keyboard are
/// created from the first snapshot that needs them and released when the need clears. These
/// tests run the real controller over a real daemon server - the same wiring the tray process
/// runs, with the transport in memory - so the need is read from snapshots exactly as it is
/// in production, and a dropped connection exercises the controller's own reconnect.
/// </summary>
public sealed class TrayOverlayLifecycleTests : IDisposable
{
    private readonly string _directory;
    private readonly List<OverlayHarness> _harnesses = [];

    public TrayOverlayLifecycleTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-tray-overlay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public async Task The_Tray_Builds_No_Overlay_Surface_Until_The_Daemon_Calls_For_One()
    {
        var harness = await CreateHarness();

        // A connected daemon that reports no layer and no keyboard builds nothing: the two
        // windows, their view models and their feeds are the resident cost this removes.
        Assert.False(harness.Host!.HasLayerHint);
        Assert.False(harness.Host.HasScreenKeyboard);
        Assert.Null(harness.Host.LayerHintWindow);
        Assert.Null(harness.Host.KeyboardWindow);

        // Input on its own is not a need: samples arrive whether or not a surface exists.
        harness.PublishSample(GamepadButtonMask.A);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, harness.Host.LayerHintCreations);
        Assert.Equal(0, harness.Host.ScreenKeyboardCreations);
    }

    [AvaloniaFact]
    public async Task A_Layer_The_Daemon_Reports_Builds_The_Hint_And_The_Release_Takes_It_Away()
    {
        var harness = await CreateHarness();

        harness.ActivateLayer();
        await WaitForCondition(() => harness.Host!.LayerHintWindow is not null);
        Dispatcher.UIThread.RunJobs();

        var first = harness.Host!.LayerHintWindow;
        Assert.True(first!.IsVisible, "the reported layer must put the hint on screen");
        Assert.Equal(1, harness.Host.LayerHintCreations);

        // A further snapshot with the layer still held is not a second need: the surface
        // already exists, so nothing is built beside it.
        harness.PublishLayerState();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(first, harness.Host.LayerHintWindow);
        Assert.Equal(1, harness.Host.LayerHintCreations);

        // The layer lets go: the window closes and the stack is released, so nothing the
        // daemon raises afterwards reaches it.
        harness.ReleaseLayer();
        await WaitForCondition(() => harness.Host.LayerHintWindow is null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(first.IsVisible, "a released hint must not sit parked and hidden");

        // The leader is held again: a fresh window, built from the snapshot that asked for it.
        harness.ActivateLayer();
        await WaitForCondition(() => harness.Host.LayerHintWindow is not null);
        Dispatcher.UIThread.RunJobs();

        var second = harness.Host!.LayerHintWindow;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second.IsVisible);
        Assert.Equal(2, harness.Host.LayerHintCreations);
    }

    [AvaloniaFact]
    public async Task The_Keyboard_Flag_Builds_The_Window_And_Clearing_It_Releases_The_Stack()
    {
        var harness = await CreateHarness();

        harness.ToggleKeyboard();
        await WaitForCondition(() => harness.Host!.KeyboardWindow is not null);
        Dispatcher.UIThread.RunJobs();

        var first = harness.Host!.KeyboardWindow;
        Assert.True(first!.IsVisible, "the daemon's own flag must put the keyboard on screen");
        Assert.Equal(1, harness.Host.ScreenKeyboardCreations);

        // The flag clears - the engine's toggle, the window's 完成 or its 返回 button, all of
        // which land on the daemon first - and the window goes with it.
        harness.ToggleKeyboard();
        await WaitForCondition(() => harness.Host.KeyboardWindow is null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(first.IsVisible, "a dismissed keyboard must not sit parked and hidden");

        harness.ToggleKeyboard();
        await WaitForCondition(() => harness.Host.KeyboardWindow is not null);
        Dispatcher.UIThread.RunJobs();

        var second = harness.Host!.KeyboardWindow;
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True(second.IsVisible);
        Assert.Equal(2, harness.Host.ScreenKeyboardCreations);
    }

    [AvaloniaFact]
    public async Task Dismissing_The_Keyboard_Releases_The_Injection_Lease_It_Took()
    {
        // The lease is what suspends mapping while the keyboard types, and the daemon keeps
        // it until the connection that took it asks for the release: a stack the host tears
        // down from the same snapshot that cleared the flag must still send that request,
        // or the mapping stays suspended after the keyboard is gone.
        var harness = await CreateHarness();

        harness.ToggleKeyboard();
        await WaitForCondition(() => harness.Host!.KeyboardWindow is not null && harness.IsInjectionSuspended);
        Dispatcher.UIThread.RunJobs();

        harness.ToggleKeyboard();
        await WaitForCondition(() => harness.Host!.KeyboardWindow is null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(harness.IsInjectionSuspended, "a dismissed keyboard must give the input lease back");
    }

    [AvaloniaFact]
    public async Task Losing_The_Daemon_Releases_Both_Surfaces_And_The_Reconnect_Rebuilds_Them_Once()
    {
        var harness = await CreateHarness();

        harness.ActivateLayer();
        harness.ToggleKeyboard();
        await WaitForCondition(() => harness.Host!.LayerHintWindow is not null
            && harness.Host!.KeyboardWindow is not null);
        Dispatcher.UIThread.RunJobs();

        var firstHint = harness.Host!.LayerHintWindow;
        var firstKeyboard = harness.Host!.KeyboardWindow;
        Assert.True(firstHint!.IsVisible);
        Assert.True(firstKeyboard!.IsVisible);

        // The connection dies: both surfaces go away now rather than sitting resident for a
        // daemon that may not come back. The controller's own Disconnected state is transient -
        // its reconnect follows it within milliseconds, and a UI-thread poll can miss it - so
        // what is observed is the surfaces themselves going away, which is what the release is
        // for. The release job is queued by the disconnect and the rebuild job only by the
        // reconnect that follows, so the release is what runs first.
        harness.Endpoint.DropConnection();
        await WaitForCondition(() => harness.Host!.LayerHintWindow is null
            && harness.Host.KeyboardWindow is null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(firstHint.IsVisible, "a released hint must not sit parked and hidden");
        Assert.False(firstKeyboard.IsVisible, "a released keyboard must not sit parked and hidden");

        // The controller reconnects on its own and re-sends the authoritative state, which
        // still carries both: each surface is rebuilt exactly once - a reconnect registers
        // nothing a second time, so there is never a second window beside the first.
        await WaitForCondition(() => harness.Host!.LayerHintWindow is not null
            && harness.Host!.KeyboardWindow is not null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, harness.Host!.LayerHintCreations);
        Assert.Equal(2, harness.Host!.ScreenKeyboardCreations);
        Assert.True(harness.Host!.LayerHintWindow!.IsVisible);
        Assert.True(harness.Host!.KeyboardWindow!.IsVisible);
    }
    [AvaloniaFact]
    public async Task The_Layer_Hint_Stays_Unbuilt_While_The_Preference_Is_Off_And_Appears_When_It_Returns()
    {
        var harness = await CreateHarness(layerHintEnabled: false);

        // The preference is the manual dismissal: a held layer must not build the hint.
        harness.ActivateLayer();
        await WaitForCondition(() => harness.Controller.Snapshot is { Layers.Count: > 0 });
        Dispatcher.UIThread.RunJobs();

        Assert.Null(harness.Host!.LayerHintWindow);
        Assert.Equal(0, harness.Host.LayerHintCreations);

        // Turning it back on builds the hint for the layer that is already held - the snapshot
        // the controller holds is what the fresh stack is built from.
        harness.Themes.SetLayerHintEnabled(true);
        harness.Host.ApplyPreferences();
        await WaitForCondition(() => harness.Host.LayerHintWindow is not null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(harness.Host.LayerHintWindow!.IsVisible);
        Assert.Equal(1, harness.Host.LayerHintCreations);

        // And turning it off releases the surface that is on screen.
        harness.Themes.SetLayerHintEnabled(false);
        harness.Host.ApplyPreferences();
        await WaitForCondition(() => harness.Host.LayerHintWindow is null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task A_Released_Hint_Feed_Stops_Answering_The_Daemon()
    {
        // The feed is released with the surface, so the daemon's streams must not reach a view
        // model nothing binds: the subscriptions go when the stack does. The observable
        // direction is a state change a live feed would have carried and the disposed one
        // leaves alone.
        var harness = await CreateHarness(withHost: false);
        var layerHint = new LayerHintViewModel();
        var visualizer = new GamepadVisualizerViewModel();
        var clock = new FakeFrameClock();
        using var feed = new TrayHost.DaemonLayerHintFeed(harness.Controller, layerHint, visualizer, clock);

        harness.ActivateLayer();
        await WaitForCondition(() => layerHint.IsActive);

        // The sample has to reach the controller before the frame drains it: the queueing
        // happens on the daemon's receive path, which is a different thread from this one.
        harness.PublishSample(GamepadButtonMask.A);
        await harness.WaitForSample(GamepadButtonMask.A);
        clock.RaiseTick();
        await WaitForCondition(() => visualizer.IsAPressed);

        feed.Dispose();

        harness.ReleaseLayer();
        harness.PublishSample(GamepadButtonMask.B);
        await harness.WaitForSample(GamepadButtonMask.B);
        clock.RaiseTick();
        Dispatcher.UIThread.RunJobs();

        Assert.True(layerHint.IsActive, "a disposed feed must not rebuild the hint");
        Assert.True(visualizer.IsAPressed, "a disposed feed must not move the diagram off the sample it held");
        Assert.False(visualizer.IsBPressed, "a disposed feed must not dispatch samples");
    }

    [AvaloniaFact]
    public async Task A_Released_Keyboard_Feed_Stops_Answering_The_Daemon()
    {
        var harness = await CreateHarness(withHost: false);
        var keyboard = new ScreenKeyboardViewModel();
        var clock = new FakeFrameClock();
        using var feed = new TrayHost.DaemonScreenKeyboardFeed(harness.Controller, keyboard, clock);

        harness.ToggleKeyboard();
        await WaitForCondition(() => keyboard.IsOpen);

        feed.Dispose();

        harness.ToggleKeyboard();
        harness.PublishSample(GamepadButtonMask.DPadRight);
        await harness.WaitForSample(GamepadButtonMask.DPadRight);
        clock.RaiseTick();
        Dispatcher.UIThread.RunJobs();

        // The daemon cleared its flag and a sample moved the cursor: a live feed would have
        // taken the window away and navigated to the next key.
        Assert.True(keyboard.IsOpen, "a disposed feed must not mirror the daemon's flag");
        Assert.Equal("a", keyboard.Keyboard.FocusedKey?.Label);
    }

    private async Task<OverlayHarness> CreateHarness(bool withHost = true, bool layerHintEnabled = true)
    {
        var harness = await OverlayHarness.CreateAsync(_directory, withHost, layerHintEnabled);
        _harnesses.Add(harness);
        return harness;
    }

    [AvaloniaFact]
    public async Task The_Tray_Wiring_Builds_The_Surfaces_The_Daemon_Calls_For_And_Releases_Them_At_Exit()
    {
        // The same wiring the tray process runs, driven end to end: InstallTray over the
        // in-memory daemon, with fake supervisors so no process is ever spawned. The keyboard
        // taking the daemon's input lease is the observable that the whole chain ran - host,
        // stack, feed, daemon - because a window the tray owns cannot be reached from here.
        var daemon = await OverlayHarness.CreateDaemonAsync(_directory);
        var daemonSupervisor = new DaemonSupervisor(
            new FakeElevation(),
            FakeLaunch,
            () => false,
            () => true,
            requestShutdown: _ => Task.FromResult(true),
            isDaemonUnitInstalled: () => false);
        var renderSupervisor = new RenderSupervisor(FakeLaunch, () => { });
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        var tray = TrayHost.InstallTray(
            Application.Current!,
            lifetime,
            daemon.Themes,
            daemonSupervisor,
            renderSupervisor,
            overlayController: () => daemon.Controller);

        // The host's own connect loop lands the connection; the keyboard flag then builds the
        // keyboard stack, whose feed takes the input lease the daemon reports.
        await WaitForCondition(() => daemon.Controller.State == DaemonConnectionState.Connected);
        daemon.Server.RequestScreenKeyboardToggle();
        await WaitForCondition(() => daemon.DaemonHost.IsInjectionSuspended);

        // The exit path disposes the host with the tray: the connection it owns goes with it,
        // and with it every surface the host built.
        tray.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => daemon.Controller.GetStateAsync(TestContext.Current.CancellationToken));
    }

    private static async Task WaitForCondition(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    /// <summary>
    /// One overlay host over a real daemon server and a real controller: the same wiring the
    /// tray process runs, with the transport in memory. The endpoint hands out a fresh channel
    /// per connect, so a dropped connection and the controller's own reconnect are exercised
    /// without a pipe. Built without a host, the same harness drives the feeds on their own.
    /// </summary>
    private sealed class OverlayHarness : IDisposable
    {
        private readonly DaemonSide _daemon;
        private readonly List<ActiveLayerSnapshot> _layers = [];

        private OverlayHarness(TrayHost.TrayOverlayHost? host, DaemonSide daemon)
        {
            Host = host;
            _daemon = daemon;
            _daemon.Server.LayerStateSource = () => _layers;
        }

        public TrayHost.TrayOverlayHost? Host { get; }

        public DaemonController Controller => _daemon.Controller;

        public DaemonServer Server => _daemon.Server;

        public OverlayTestEndpoint Endpoint => _daemon.Endpoint;

        public ThemeService Themes => _daemon.Themes;

        public static async Task<OverlayHarness> CreateAsync(
            string directory,
            bool withHost = true,
            bool layerHintEnabled = true)
        {
            var daemon = await CreateDaemonAsync(directory, layerHintEnabled);
            var harness = new OverlayHarness(
                withHost ? new TrayHost.TrayOverlayHost(daemon.Controller, daemon.Themes) : null,
                daemon);

            if (withHost)
            {
                // The host owns the connect: its own retry loop is what lands the connection.
                await harness.WaitForConnection();
            }
            else
            {
                await daemon.Controller.ConnectAsync(TestContext.Current.CancellationToken);
            }

            return harness;
        }

        /// <summary>
        /// The daemon side of the harness on its own: a real server, an endpoint that hands out
        /// a fresh channel per connect, and the controller that reads it - unconnected, so the
        /// caller decides who connects: the overlay host's own retry loop, or the test. The
        /// tray wiring test builds the presentation host over the same pieces.
        /// </summary>
        public static Task<DaemonSide> CreateDaemonAsync(string directory, bool layerHintEnabled = true)
        {
            var profile = new GamepadProfile
            {
                Name = "overlay",
                DeviceId = "pad-1",
                Mappings =
                [
                    new MappingEntry
                    {
                        Source = new ButtonBinding(GamepadButton.X),
                        Target = new KeyPressTarget(GamepadKey.C),
                        Layer = 1,
                    },
                ],
            };

            var daemonHost = new FakeDaemonHost { HasController = true };
            daemonHost.AddDevice("pad-1");
            daemonHost.Reload("pad-1", profile);
            var server = new DaemonServer(
                daemonHost,
                new InMemoryProfileStore(),
                new DaemonStateStore(Path.Combine(directory, $"state-{Guid.NewGuid():N}.json")));

            // The controller's own reconnect is what a daemon restart exercises, so its delay
            // is the test's rather than the production second.
            var endpoint = new OverlayTestEndpoint(server);
            var controller = new DaemonController(endpoint, reconnectDelay: TimeSpan.FromMilliseconds(20));
            var themes = new ThemeService(directory);
            if (!layerHintEnabled)
            {
                themes.SetLayerHintEnabled(false);
            }

            return Task.FromResult(
                new DaemonSide(controller, server, endpoint, themes, daemonHost, profile.Mappings[0].Id));
        }

        /// <summary>Puts a temporary layer on the daemon and publishes the snapshot that carries it.</summary>
        public void ActivateLayer()
        {
            _layers.Add(new ActiveLayerSnapshot("pad-1", 1, [new EffectiveMappingSnapshot(_daemon.LayerMappingId, 1)]));
            Server.PublishLayerState();
        }

        /// <summary>Takes the layer off the daemon and publishes the snapshot that reports it gone.</summary>
        public void ReleaseLayer()
        {
            _layers.Clear();
            Server.PublishLayerState();
        }

        /// <summary>Publishes the current layer state again, as any other state change would.</summary>
        public void PublishLayerState() => Server.PublishLayerState();

        /// <summary>Flips the daemon's own screen-keyboard flag, exactly as the engine's key does.</summary>
        public void ToggleKeyboard() => Server.RequestScreenKeyboardToggle();

        /// <summary>Publishes one input sample from the pad, as the engine's loop would.</summary>
        public void PublishSample(GamepadButtonMask pressed)
            => _daemon.DaemonHost.PublishInputSample("pad-1", GamepadState.Create(pressed, 0f, 0f, 0f, 0f));

        /// <summary>True while the keyboard's input lease is held, as the daemon last saw it.</summary>
        public bool IsInjectionSuspended => _daemon.DaemonHost.IsInjectionSuspended;

        /// <summary>
        /// Waits until the controller has taken the given sample. The controller stores the
        /// latest sample per device before raising the event, so a match also means a feed has
        /// already queued it - which is what makes a frame tick after this wait meaningful.
        /// </summary>
        public async Task WaitForSample(GamepadButtonMask pressed)
        {
            await WaitForCondition(() => Controller.InputSamples.Any(sample => sample.PressedButtons == pressed));
        }

        public async Task WaitForConnection()
        {
            await WaitForCondition(() => Controller.State == DaemonConnectionState.Connected);
        }

        private static async Task WaitForCondition(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 200 && !condition(); attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(condition());
        }

        public void Dispose()
        {
            Host?.Dispose();
            Endpoint.Dispose();
        }
    }

    /// <summary>
    /// A ready endpoint over in-memory channels, one fresh channel per connect: the shape a
    /// daemon restart presents to the controller, so the reconnect path runs for real.
    /// </summary>
    private sealed class OverlayTestEndpoint(DaemonServer server) : IDaemonEndpoint, IDisposable
    {
        private readonly DaemonServer _server = server;
        private LoopbackChannel? _channel;

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
        {
            // Wired once, on the first connect: a reconnect opens a new channel but must never
            // add a second push.
            _server.StateChanged -= Push;
            _server.StateChanged += Push;
            _server.InputSampled -= Push;
            _server.InputSampled += Push;
            return Task.FromResult(true);
        }

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            _channel = new LoopbackChannel(_server);
            return Task.FromResult<IDaemonChannel>(_channel);
        }

        /// <summary>Drops the live channel, exactly as a daemon that stopped answering does.</summary>
        public void DropConnection() => _channel?.Drop();

        private void Push(StateChangedMessage message) => _channel?.Push(message);

        private void Push(InputSampleMessage message) => _channel?.Push(message);

        public void Dispose()
        {
            _server.StateChanged -= Push;
            _server.InputSampled -= Push;
            _channel?.Drop();
        }
    }

    /// <summary>
    /// One client side of the daemon server: answers are produced synchronously on send and
    /// pushed broadcasts queue behind them, exactly like the named-pipe transport delivers
    /// them in order. <see cref="Drop"/> ends the stream, which is how the controller learns
    /// the daemon is gone.
    /// </summary>
    private sealed class LoopbackChannel(DaemonServer server) : IDaemonChannel
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

        public void Drop()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Drop();
            return ValueTask.CompletedTask;
        }

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The daemon side of the harness: the controller, the server it reads, the endpoint that
    /// hands it a channel per connect, and the two things a test drives or observes through
    /// them - the preferences and the fake daemon host.
    /// </summary>
    private sealed record DaemonSide(
        DaemonController Controller,
        DaemonServer Server,
        OverlayTestEndpoint Endpoint,
        ThemeService Themes,
        FakeDaemonHost DaemonHost,
        Guid LayerMappingId);

    /// <summary>A launch seam that never spawns a process, for the tray wiring test's supervisors.</summary>
    private static IProcessHandle? FakeLaunch(ProcessStartInfo startInfo) => null;

    /// <summary>An elevation service that stands down, so a Windows-flavoured supervisor spawns nothing.</summary>
    private sealed class FakeElevation : IElevationService
    {
        public bool IsSupported => false;

        public bool IsElevated => false;

        public bool RelaunchElevated(string? arguments) => false;
    }
}
