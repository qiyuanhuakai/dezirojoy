using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;

namespace DeziroJoy.App.Hosting;

/// <summary>
/// One child process the tray supervises. The abstraction exists so the supervisors can be
/// driven headlessly: tests hand them a fake and no real process is ever spawned.
/// </summary>
internal interface IProcessHandle : IDisposable
{
    bool HasExited { get; }

    /// <summary>Raised when the child ended, so a supervisor can drop its cached handle.</summary>
    event Action? Exited;

    void Kill(bool entireTree);

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The one injectable process-launch seam. Production passes
/// <see cref="ProcessLauncher.Start"/>; every test passes a recorder, which is why no
/// supervisor ever spawns a real process.
/// </summary>
internal delegate IProcessHandle? ProcessLaunch(ProcessStartInfo startInfo);

internal enum DaemonSupervisionState
{
    Stopped,
    Running,
    Stopping,
    StoppedGracefully,
    Killed,
    StartFailed,
    StopFailed,
}

/// <summary>
/// Supervises the resident daemon. Disposing a supervisor only releases its process handle;
/// explicit application exit is the sole path that asks the daemon to stop.
/// </summary>
internal sealed class DaemonSupervisor : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IElevationService _elevation;
    private readonly ProcessLaunch _launcher;
    private readonly Func<bool> _isWindows;
    private readonly Func<bool> _isDaemonReachable;
    private readonly Func<CancellationToken, Task<bool>> _requestShutdown;
    private readonly TimeSpan _stopTimeout;
    private IProcessHandle? _daemon;
    private bool _elevatedRequested;
    private bool _disposed;

    public DaemonSupervisor(
        IElevationService? elevation = null,
        ProcessLaunch? launch = null,
        Func<bool>? isWindows = null,
        Func<bool>? isDaemonReachable = null,
        Func<CancellationToken, Task<bool>>? requestShutdown = null,
        TimeSpan? stopTimeout = null)
    {
        _elevation = elevation ?? new ElevationService();
        _launcher = launch ?? ProcessLauncher.Start;
        _isWindows = isWindows ?? (static () => OperatingSystem.IsWindows());
        _isDaemonReachable = isDaemonReachable ?? (static () => IsDaemonReachable(IpcContract.PipeName));
        _requestShutdown = requestShutdown ?? RequestDaemonShutdownAsync;
        _stopTimeout = stopTimeout ?? StopTimeout;
    }

    public bool IsRunning => _daemon is { HasExited: false };

    public DaemonSupervisionState State { get; private set; } = DaemonSupervisionState.Stopped;

    public string? LastError { get; private set; }

    /// <summary>
    /// Starts the daemon when this supervisor does not already hold a live one and none is
    /// already reachable. A daemon whose pipe never answers is not something the tray acts on:
    /// it stays resident and the next menu action tries again. There is no local-engine
    /// fallback - the daemon is the only mapping authority, and a tray that injected on its
    /// own behalf would be the UIPI-silent-drop failure the architecture exists to prevent.
    /// </summary>
    public void EnsureRunning()
    {
        if (_disposed || IsRunning)
        {
            return;
        }

        if (SafeIsDaemonReachable())
        {
            State = DaemonSupervisionState.Running;
            return;
        }

        if (_isWindows())
        {
            if (_elevatedRequested)
            {
                return;
            }

            _elevatedRequested = true;
            State = _elevation.RelaunchElevated("--daemon")
                ? DaemonSupervisionState.Running
                : DaemonSupervisionState.StartFailed;
            return;
        }

        _daemon = _launcher(ProcessLauncher.DaemonStartInfo());
        State = _daemon is null ? DaemonSupervisionState.StartFailed : DaemonSupervisionState.Running;
        Trace.WriteLine($"[DeziroJoy] daemon supervisor: {State}");
    }

    /// <summary>
    /// Asks the daemon to exit over IPC before waiting and, for a child we own, killing only
    /// what survived the bounded graceful wait. A remote daemon that cannot confirm its exit
    /// reports failure so the caller can keep its UI alive instead of pretending to have exited.
    /// </summary>
    public bool Stop()
    {
        if (_daemon is { HasExited: true } exited)
        {
            _daemon = null;
            exited.Dispose();
        }

        var daemon = _daemon;
        if (daemon is null && !_elevatedRequested && !SafeIsDaemonReachable())
        {
            State = DaemonSupervisionState.Stopped;
            LastError = null;
            return true;
        }

        State = DaemonSupervisionState.Stopping;
        LastError = null;
        var acknowledged = false;

        try
        {
            using var timeout = new CancellationTokenSource(_stopTimeout);
            acknowledged = _requestShutdown(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }

        var stopped = false;
        var killed = false;
        if (daemon is not null)
        {
            if (acknowledged)
            {
                TryWait(daemon, _stopTimeout);
            }

            if (!daemon.HasExited)
            {
                killed = true;
                TryKill(daemon);
                TryWait(daemon, _stopTimeout);
            }

            stopped = daemon.HasExited;
            if (stopped)
            {
                _daemon = null;
                daemon.Dispose();
            }
        }
        else if (acknowledged)
        {
            stopped = WaitUntilUnreachable(_stopTimeout);
        }

        State = stopped
            ? killed
                ? DaemonSupervisionState.Killed
                : acknowledged ? DaemonSupervisionState.StoppedGracefully : DaemonSupervisionState.Stopped
            : DaemonSupervisionState.StopFailed;
        if (stopped)
        {
            _elevatedRequested = false;
            LastError = null;
        }
        else
        {
            LastError ??= "the daemon did not confirm its exit within the shutdown budget";
        }

        Trace.WriteLine($"[DeziroJoy] daemon supervisor: {State}; {LastError}");
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _daemon?.Dispose();
        _daemon = null;
    }

    /// <summary>
    /// How long one connect attempt waits for the daemon's mapping pipe to answer - the same
    /// budget the daemon itself uses to detect a peer. The daemon holds the pipe open for its
    /// whole life, so a connect that cannot complete in this window means no daemon is there.
    /// </summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Pause between probe attempts. The daemon-mid-startup race is exactly this wide: the
    /// process exists but has not created its pipe yet, so one failed attempt proves nothing.
    /// </summary>
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Attempts before the probe concludes that no daemon is reachable.</summary>
    private const int ProbeAttempts = 3;

    /// <summary>
    /// True when something answers on <paramref name="pipeName"/>. The pipe, not a process or
    /// a mutex, is the liveness signal: the daemon listens for its whole life, so a connect
    /// that succeeds means the mapping authority is already up.
    /// </summary>
    internal static bool IsDaemonReachable(string pipeName)
    {
        for (var attempt = 0; attempt < ProbeAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(ProbeRetryDelay);
            }

            try
            {
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(ProbeTimeout);
                return client.IsConnected;
            }
            catch (Exception exception) when (exception
                is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Nothing answered within the budget: retry, then conclude unreachable.
            }
        }

        return false;
    }

    private bool SafeIsDaemonReachable()
    {
        try
        {
            return _isDaemonReachable();
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    private bool WaitUntilUnreachable(TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (!SafeIsDaemonReachable())
            {
                return true;
            }

            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }
        while (true);
    }

    private static async Task<bool> RequestDaemonShutdownAsync(CancellationToken cancellationToken)
    {
        await using var controller = new DaemonController(new NamedPipeDaemonEndpoint());
        await controller.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await controller.RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryKill(IProcessHandle daemon)
    {
        try
        {
            daemon.Kill(entireTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already gone, or not ours to terminate.
        }
    }

    private static void TryWait(IProcessHandle daemon, TimeSpan timeout)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(timeout);
            daemon.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception
            is OperationCanceledException or AggregateException or InvalidOperationException)
        {
            // The fallback path inspects HasExited after this bounded wait.
        }
    }
}

/// <summary>
/// Supervises the render process: the stateless Avalonia window the tray starts on demand.
/// The tray never hosts a window itself, so this is the only thing that can put one on screen.
/// </summary>
internal enum RenderSupervisionState
{
    Stopped,
    Running,
    StartFailed,
    StopFailed,
}

internal sealed class RenderSupervisor : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);
    private readonly ProcessLaunch _launcher;
    private readonly Action _showLive;
    private IProcessHandle? _render;
    private bool _disposed;

    public RenderSupervisor(ProcessLaunch? launch = null, Action? showLive = null)
    {
        _launcher = launch ?? ProcessLauncher.Start;
        _showLive = showLive ?? (static () => { });
    }

    public bool IsAlive => _render is { HasExited: false };

    public RenderSupervisionState State { get; private set; } = RenderSupervisionState.Stopped;

    public void Show()
    {
        if (_disposed)
        {
            return;
        }

        if (IsAlive)
        {
            _showLive();
            return;
        }

        Spawn();
    }

    public bool Stop()
    {
        if (_render is not { } render)
        {
            State = RenderSupervisionState.Stopped;
            return true;
        }

        if (!render.HasExited)
        {
            try
            {
                render.Kill(entireTree: true);
                using var timeout = new CancellationTokenSource(StopTimeout);
                render.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception
                is InvalidOperationException or Win32Exception or OperationCanceledException)
            {
                Trace.WriteLine($"[DeziroJoy] render stop: {exception.Message}");
            }
        }

        var stopped = render.HasExited;
        State = stopped ? RenderSupervisionState.Stopped : RenderSupervisionState.StopFailed;
        if (stopped)
        {
            render.Exited -= OnRenderExited;
            _render = null;
            render.Dispose();
        }

        Trace.WriteLine($"[DeziroJoy] render supervisor: {State}");
        return stopped;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_render is { } render)
        {
            render.Exited -= OnRenderExited;
            _render = null;
            render.Dispose();
        }
    }

    private void Spawn()
    {
        var handle = _launcher(ProcessLauncher.RenderStartInfo());

        if (handle is null)
        {
            State = RenderSupervisionState.StartFailed;
            return;
        }

        _render = handle;
        State = RenderSupervisionState.Running;
        handle.Exited += OnRenderExited;
    }

    private void OnRenderExited()
    {
        if (_render is not { } render)
        {
            return;
        }

        render.Exited -= OnRenderExited;

        if (ReferenceEquals(_render, render))
        {
            _render = null;
            State = RenderSupervisionState.Stopped;
        }
    }
}

/// <summary>
/// The tray process: a resident Avalonia application with no window. It owns the instance lease,
/// activation channel, persisted theme, tray icon and process supervisors while no render is up.
/// </summary>
internal static class TrayHost
{
    /// <summary>True once this process is the tray rather than a render process.</summary>
    internal static bool IsTrayProcess { get; private set; }

    /// <summary>
    /// Pause between render-answer polls while the show route waits for the window. Kept short
    /// (well under the daemon probe's own cadence) so a tray wake feels immediate without adding
    /// load: the per-probe connect still honours the shrinking handover budget.
    /// </summary>
    private static readonly TimeSpan ShowPollPause = TimeSpan.FromMilliseconds(40);

    private static readonly TimeSpan ShowPollBudget = TimeSpan.FromSeconds(10);

    private static GuiInstanceOwner? _instance;

    /// <summary>
    /// The default entry point. The tray wins the instance lock, so a second launch reaches it
    /// over the activation pipe instead of starting a second tray.
    /// </summary>
    internal static GuiArbitration ArbitrateWithRetry(
        GuiInstanceIdentity identity,
        Func<GuiInstanceIdentity, CancellationToken, Task<GuiArbitration>>? arbitrate = null,
        Func<CancellationToken, Task>? retryDelay = null,
        int maxAttempts = 300)
    {
        arbitrate ??= static (id, ct) => GuiInstanceGuard.ArbitrateAsync(id, null, ct);
        retryDelay ??= static ct => Task.Delay(100, ct);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var arbitration = arbitrate(identity, CancellationToken.None).GetAwaiter().GetResult();
            if (arbitration.IsWinner)
            {
                return arbitration;
            }

            if (attempt == maxAttempts - 1)
            {
                return arbitration;
            }

            retryDelay(CancellationToken.None).GetAwaiter().GetResult();
        }

        return GuiArbitration.Exit(1);
    }

    public static int Run()
    {
        IsTrayProcess = true;

        var identity = GuiInstanceIdentity.ForCurrentProcess();
        // This tray is spawned by a render process that is closing its window, so the lock is
        // still held for a moment: waiting for the release is the contract, not an exit.
        var arbitration = ArbitrateWithRetry(identity);
        if (!arbitration.IsWinner)
        {
            return arbitration.ExitCode;
        }

        var activation = GuiActivationServer.StartActive(identity, GuiInstanceRole.Tray);
        var owner = new GuiInstanceOwner(identity, arbitration.Lease!, activation);
        _instance = owner;

        try
        {
            Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        finally
        {
            _instance = null;
            owner.Dispose();
        }
    }

    internal static TrayService InstallTray(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime)
        => InstallTray(
            application,
            lifetime,
            new ThemeService(),
            new DaemonSupervisor(),
            new RenderSupervisor(showLive: ShowLiveRenderProcess),
            GuiActivationServer.ActiveInstance,
            releaseInstance: () => _instance?.ReleaseLease(),
            reacquireInstance: () => _instance?.TryReacquireLease() ?? false,
            // The overlay observes the daemon the supervisor already owns, so it probes the
            // mapping pipe and never starts a daemon of its own - a second elevated relaunch
            // from the tray would be a UAC prompt for a daemon that is already there.
            overlayController: static () => new DaemonController(new ProbeOnlyDaemonEndpoint()));

    internal static TrayService InstallTray(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime,
        ThemeService themes,
        DaemonSupervisor daemon,
        RenderSupervisor render,
        GuiActivationServer? activation = null,
        Action? shutdown = null,
        Action? releaseInstance = null,
        Func<GuiInstanceIdentity, GuiActivationOptions, CancellationToken, Task<bool>>? showPoll = null,
        Func<Task>? pollDelay = null,
        TimeSpan? pollBudget = null,
        Func<bool>? reacquireInstance = null,
        Func<bool>? isDaemonReachable = null,
        Func<DaemonController?>? overlayController = null)
    {
        themes.ApplyPersisted();

        showPoll ??= static (identity, options, cancellationToken)
            => GuiActivationClient.TryProbeAsync(identity, GuiInstanceRole.Render, options, cancellationToken);
        pollDelay ??= static () => Task.Delay(ShowPollPause);
        var budget = pollBudget ?? ShowPollBudget;
        isDaemonReachable ??= static () => DaemonSupervisor.IsDaemonReachable(IpcContract.PipeName);
        var handoverGate = new object();
        Task<bool>? handover = null;

        // The layer hint overlay this tray hosts while no render window is up, or null when the
        // overlay seam is not wired (the supervisor tests pass none). Held so tray exit can close
        // it and release its daemon connection.
        IDisposable? layerHint = null;

        // The floating screen keyboard this tray hosts while no render window is up, or null when
        // the overlay seam is not wired. Held so tray exit can close it and release its daemon
        // connection.
        IDisposable? screenKeyboard = null;

        async Task ShowAsync()
        {
            Task<bool> current;
            lock (handoverGate)
            {
                current = handover ??= HandOverToRenderAsync(
                    daemon,
                    render,
                    releaseInstance,
                    showPoll,
                    pollDelay,
                    budget,
                    shutdown,
                    lifetime,
                    reacquireInstance);
            }

            var handedOver = false;
            try
            {
                handedOver = await current;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"[DeziroJoy] render handover failed: {exception.Message}");
            }

            if (!handedOver)
            {
                lock (handoverGate)
                {
                    if (ReferenceEquals(handover, current))
                    {
                        handover = null;
                    }
                }
            }
        }

        // The menu exit and the update flow's Exit request share one path. The difference is
        // the daemon: an updater has already stopped it over IPC - the only way an elevated
        // daemon can be stopped at all - and a second refusal from a daemon that is already
        // gone must not keep the tray, and with it the old binary, resident. When the mapping
        // pipe still answers, the daemon is alive after all and the normal stop path runs, so
        // the skip is verified rather than assumed.
        bool ExitTray(bool daemonAlreadyStopped)
        {
            if (!daemonAlreadyStopped || isDaemonReachable())
            {
                if (!daemon.Stop())
                {
                    return false;
                }
            }

            if (!render.Stop())
            {
                return false;
            }

            Retire(shutdown, lifetime);
            return true;
        }

        var tray = new TrayService(
            application,
            lifetime,
            themes,
            showMainWindow: async void () => await ShowAsync(),
            exit: () => ExitTray(daemonAlreadyStopped: false));

        tray.Install();

        if (activation is not null)
        {
            activation.ShowRequested += ShowAsync;
            activation.ExitRequested += () =>
            {
                if (!ExitTray(daemonAlreadyStopped: true))
                {
                    // The server turns a faulted handler into Accepted=false, so the peer
                    // learns at once that the tray is not going away instead of waiting out
                    // its whole vanish budget for the same answer.
                    return Task.FromException(
                        new InvalidOperationException("the tray could not retire"));
                }

                return Task.CompletedTask;
            };
            activation.BeginServing();
        }

        lifetime.Exit += (_, _) =>
        {
            tray.Dispose();
            layerHint?.Dispose();
            screenKeyboard?.Dispose();
            daemon.Dispose();
            render.Dispose();
        };

        daemon.EnsureRunning();

        // The tray owns no window, so it hosts the layer hint overlay itself: the render window
        // carries its own while it is up, and this one takes over the moment the window closes
        // to the tray, so a held temporary layer keeps naming its buttons over the target
        // application. The seam is injectable so the existing supervisor tests spawn nothing.
        if (overlayController?.Invoke() is { } controller)
        {
            layerHint = InstallTrayLayerHint(controller, themes);
            screenKeyboard = InstallTrayScreenKeyboard(controller);
        }

        return tray;
    }

    /// <summary>How many times the tray retries the overlay's daemon connect before giving up.</summary>
    private const int TrayLayerHintConnectAttempts = 6;

    /// <summary>Pause between overlay connect retries, covering the daemon child's pipe startup.</summary>
    private static readonly TimeSpan TrayLayerHintConnectRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Builds the tray process's layer hint overlay over its own daemon connection: a layer hint
    /// and a live pad diagram fed from the daemon's snapshots and input samples, bound to a
    /// click-through overlay whose placement rides the persisted preferences. The connection is
    /// retried within a bounded budget because the daemon child the supervisor just launched may
    /// still be opening its pipe; a connection that never lands simply leaves the overlay hidden
    /// rather than taking the tray down. The returned handle owns the overlay, its feed and the
    /// connection, and is disposed when the tray exits.
    /// </summary>
    internal static IDisposable InstallTrayLayerHint(DaemonController controller, ThemeService themes)
    {
        var layerHint = new LayerHintViewModel
        {
            IsSuppressed = !themes.LayerHintEnabled,
        };
        var visualizer = new GamepadVisualizerViewModel();
        var overlay = new LayerHintOverlay(
            layerHint,
            visualizer,
            () => themes.LayerHintPlacement,
            ScreenCursor.TryGetPosition);
        var feed = new DaemonLayerHintFeed(controller, layerHint, visualizer);
        var connect = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= TrayLayerHintConnectAttempts; attempt++)
            {
                if (connect.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await controller.ConnectAsync(connect.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"[DeziroJoy] 托图层提示连接第 {attempt} 次失败：{exception.Message}");
                    if (attempt == TrayLayerHintConnectAttempts)
                    {
                        return;
                    }

                    try
                    {
                        await Task.Delay(TrayLayerHintConnectRetryDelay, connect.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, CancellationToken.None);

        return new TrayLayerHintHandle(overlay, feed, controller, connect);
    }

    /// <summary>Owns everything the tray's layer hint overlay needs, released on tray exit.</summary>
    private sealed class TrayLayerHintHandle(
        LayerHintOverlay overlay,
        DaemonLayerHintFeed feed,
        DaemonController controller,
        CancellationTokenSource connect) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connect.Cancel();
            connect.Dispose();
            overlay.Close();
            feed.Dispose();
            _ = controller.DisposeAsync().AsTask();
        }
    }

    /// <summary>
    /// Feeds a layer hint overlay from the daemon in the tray process, mirroring how the render
    /// window's view model drives its own overlay. The tray owns no window and no view model, so
    /// this is the smallest seam that turns daemon snapshots and input samples into the two view
    /// models the overlay binds: the layer hint (rebuilt from every snapshot, so it appears and
    /// disappears with the temporary layer the daemon reports) and the live pad diagram (fed from
    /// every input sample, so the held leader lights up on it). Daemon events arrive on the
    /// controller's receive loop, so they are marshalled onto the UI thread exactly as the render
    /// path marshals them.
    /// </summary>
    internal sealed class DaemonLayerHintFeed : IDisposable
    {
        private readonly DaemonController _daemon;
        private readonly LayerHintViewModel _layerHint;
        private readonly GamepadVisualizerViewModel _visualizer;
        private readonly SynchronizationContext? _uiContext;

        public DaemonLayerHintFeed(
            DaemonController daemon,
            LayerHintViewModel layerHint,
            GamepadVisualizerViewModel visualizer)
        {
            _daemon = daemon;
            _layerHint = layerHint;
            _visualizer = visualizer;
            _uiContext = SynchronizationContext.Current;

            _daemon.SnapshotChanged += OnSnapshot;
            _daemon.InputSampled += OnInputSample;
            _daemon.Disconnected += OnDisconnected;

            if (_daemon.Snapshot is { } snapshot)
            {
                OnSnapshot(snapshot);
            }

            foreach (var sample in _daemon.InputSamples)
            {
                OnInputSample(sample);
            }
        }

        private void OnSnapshot(StateSnapshot snapshot)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnSnapshot(snapshot), null);
                return;
            }

            _layerHint.Update(ParseProfile(snapshot), snapshot);
            _visualizer.SetConnected(snapshot.HasController);
        }

        private void OnInputSample(InputSampleMessage sample)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnInputSample(sample), null);
                return;
            }

            _visualizer.Update(sample.ToGamepadState());
            _visualizer.SetConnected(true);
        }

        private void OnDisconnected()
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnDisconnected(), null);
                return;
            }

            // No daemon means no active layer: the hint empties and the overlay hides, exactly as
            // the render window clears its own hint on disconnect.
            _layerHint.Update(null, null);
            _visualizer.SetConnected(false);
        }

        /// <summary>
        /// The profile the snapshot carries, or null when it is absent or unreadable. The layer
        /// hint resolves the layer's mapping ids against this body; a body this build cannot parse
        /// leaves the hint empty, which hides the overlay rather than naming a wrong action.
        /// </summary>
        private static GamepadProfile? ParseProfile(StateSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.ProfileJson))
            {
                return null;
            }

            try
            {
                return ProfileSerializer.Load(snapshot.ProfileJson).Profile;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _daemon.SnapshotChanged -= OnSnapshot;
            _daemon.InputSampled -= OnInputSample;
            _daemon.Disconnected -= OnDisconnected;
        }
    }

    /// <summary>
    /// Builds the tray process's floating screen keyboard over its own daemon connection: the
    /// keyboard a 屏幕键盘 special key summons, which types into whatever application has focus.
    /// The tray owns no window, so - exactly as it does for the layer hint overlay - it hosts the
    /// window itself, because the resident mode is where the keyboard matters most: the user is
    /// in the target application with the pad in hand and no editor card in sight.
    /// <para>
    /// Everything the keyboard does still goes through the daemon: the flag the engine's toggle
    /// flips arrives in a snapshot and is mirrored here, every key press is a request the daemon
    /// answers through its own output path, and the input lease the keyboard takes is the same
    /// per-connection hijack lease the render window uses. A window that takes over from the tray
    /// adopts the same flag from the same snapshot, so there is one keyboard and one lease
    /// between the two processes.
    /// </para>
    /// </summary>
    internal static IDisposable InstallTrayScreenKeyboard(DaemonController controller)
    {
        var keyboard = new ScreenKeyboardViewModel();
        var feed = new DaemonScreenKeyboardFeed(controller, keyboard);
        var window = new ScreenKeyboardWindow(keyboard);
        return new TrayScreenKeyboardHandle(window, feed);
    }

    /// <summary>Owns the tray's screen keyboard window and the feed that drives it.</summary>
    private sealed class TrayScreenKeyboardHandle(
        ScreenKeyboardWindow window,
        DaemonScreenKeyboardFeed feed) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            feed.Dispose();
            window.Close();
        }
    }

    /// <summary>
    /// Drives the tray's floating screen keyboard from the daemon: the open flag from the
    /// snapshots, the cursor and the pages from the input samples, and every output back to the
    /// daemon as a request. The counterpart of <see cref="DaemonLayerHintFeed"/> for the one
    /// surface that also talks back - which is why this one owns the input lease as well.
    /// </summary>
    internal sealed class DaemonScreenKeyboardFeed : IDisposable
    {
        private readonly DaemonController _daemon;
        private readonly ScreenKeyboardViewModel _keyboard;
        private readonly SynchronizationContext? _uiContext;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Dictionary<string, GamepadState> _baselines = new(StringComparer.Ordinal);

        /// <summary>The profile the last snapshot carried, for the key that summons the keyboard.</summary>
        private GamepadProfile? _profile;

        public DaemonScreenKeyboardFeed(DaemonController daemon, ScreenKeyboardViewModel keyboard)
        {
            ArgumentNullException.ThrowIfNull(daemon);
            ArgumentNullException.ThrowIfNull(keyboard);

            _daemon = daemon;
            _keyboard = keyboard;
            _uiContext = SynchronizationContext.Current;

            // The daemon is the sole injection authority, so the keyboard asks and the daemon
            // injects - the same two requests the render window's view model makes, over the
            // same connection this feed already owns.
            keyboard.SetOutput(
                text => InjectTextAsync(text),
                (key, modifiers) => InjectKeyAsync(key, modifiers));
            keyboard.CloseRequested += () => _ = CloseAsync();

            _daemon.SnapshotChanged += OnSnapshot;
            _daemon.InputSampled += OnInputSample;
            _daemon.Disconnected += OnDisconnected;

            if (_daemon.Snapshot is { } snapshot)
            {
                OnSnapshot(snapshot);
            }

            foreach (var sample in _daemon.InputSamples)
            {
                OnInputSample(sample);
            }
        }

        private void OnSnapshot(StateSnapshot snapshot)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnSnapshot(snapshot), null);
                return;
            }

            _profile = ParseProfile(snapshot);
            _baselines.Clear();

            // The daemon owns the flag; this only mirrors it, and moves the input lease with it.
            // The lease is the same per-connection hijack lease the render window takes, so while
            // the keyboard is open the mapping stops and the pad drives the caps.
            if (_keyboard.IsOpen != snapshot.ScreenKeyboardOpen)
            {
                _keyboard.IsOpen = snapshot.ScreenKeyboardOpen;
                _ = SetLeaseAsync(snapshot.ScreenKeyboardOpen);
            }
        }

        private void OnInputSample(InputSampleMessage sample)
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnInputSample(sample), null);
                return;
            }

            if (!_keyboard.IsOpen)
            {
                return;
            }

            var state = sample.ToGamepadState();
            if (!_baselines.TryGetValue(sample.DeviceId, out var previous))
            {
                // The first sample of one device establishes its baseline without navigating.
                _baselines[sample.DeviceId] = state;
                return;
            }

            var edges = state.PressedButtons & ~previous.PressedButtons;
            _baselines[sample.DeviceId] = state;

            if (edges == GamepadButtonMask.None)
            {
                return;
            }

            // The engine is suspended while the keyboard is open, so the key that summoned it
            // cannot reach the engine to toggle a second time - the window answers it instead.
            if (edges.Has(GamepadButton.B) || IsScreenKeyboardKey(edges))
            {
                _ = CloseAsync();
                return;
            }

            _keyboard.Navigate(edges);
        }

        private void OnDisconnected()
        {
            if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
            {
                _uiContext.Post(_ => OnDisconnected(), null);
                return;
            }

            // No daemon means no flag and no output channel: the window goes away rather than
            // sitting there typing into nothing. A reconnect that still reports the keyboard open
            // brings it back, because the daemon is the authority.
            _baselines.Clear();
            _profile = null;
            _keyboard.IsOpen = false;
        }

        /// <summary>
        /// True when one of the newly pressed buttons carries the profile's own 屏幕键盘 key, on
        /// the click or on either gesture action.
        /// </summary>
        private bool IsScreenKeyboardKey(GamepadButtonMask edges)
        {
            if (_profile is not { } profile)
            {
                return false;
            }

            foreach (var mapping in profile.Mappings)
            {
                if (mapping.Source is not ButtonBinding binding || !edges.Has(binding.Button))
                {
                    continue;
                }

                if (CarriesScreenKeyboard(mapping.Target)
                    || binding.Gesture is { } gesture
                    && (CarriesScreenKeyboard(gesture.DoubleClickAction) || CarriesScreenKeyboard(gesture.LongPressAction)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CarriesScreenKeyboard(MappingTarget? target)
            => target is SpecialKeyTarget { Kind: SpecialKeyKind.Keyboard };

        /// <summary>
        /// Takes or releases this connection's hijack lease for the keyboard. The request is
        /// fire-and-forget: a lease that could not be taken leaves the mapping running, which is
        /// the safe direction to fail in, and the daemon's own snapshot says what actually holds.
        /// </summary>
        private async Task SetLeaseAsync(bool suspended)
        {
            try
            {
                await _daemon.SetInjectionSuspendedAsync(suspended, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception
                is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is going away; the daemon releases a disconnected client's lease
                // on its own, so there is nothing to retry.
            }
        }

        private async Task InjectTextAsync(string text)
        {
            try
            {
                var result = await _daemon.InjectTextAsync(text, _lifetime.Token).ConfigureAwait(false);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportFailure(result, "屏幕键盘输入");
                }
            }
            catch (Exception exception) when (exception
                is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception);
            }
        }

        private async Task InjectKeyAsync(GamepadKey key, KeyModifiers modifiers)
        {
            try
            {
                var result = await _daemon.InjectKeyAsync(key, modifiers, _lifetime.Token).ConfigureAwait(false);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportFailure(result, "屏幕键盘按键");
                }
            }
            catch (Exception exception) when (exception
                is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception);
            }
        }

        /// <summary>Dismisses the keyboard, which means asking the daemon to clear its flag.</summary>
        private async Task CloseAsync()
        {
            try
            {
                var result = await _daemon.SetScreenKeyboardAsync(false, _lifetime.Token).ConfigureAwait(false);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportFailure(result, "关闭屏幕键盘");
                }
            }
            catch (Exception exception) when (exception
                is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception);
            }
        }

        private void ReportFailure(MutationResult result, string operation)
            => _keyboard.ReportFailure(
                $"{operation}被拒绝：{result.ErrorMessage ?? result.ErrorCode ?? "守护进程拒绝"}");

        private void ReportDaemonUnavailable(Exception exception)
            => _keyboard.ReportFailure($"守护进程不可用：{exception.Message}");

        /// <summary>
        /// The profile the snapshot carries, or null when it is absent or unreadable. The
        /// summoning key has to be found in it; a body this build cannot parse leaves the window
        /// answering B alone, which is the safe subset.
        /// </summary>
        private static GamepadProfile? ParseProfile(StateSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.ProfileJson))
            {
                return null;
            }

            try
            {
                return ProfileSerializer.Load(snapshot.ProfileJson).Profile;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _daemon.SnapshotChanged -= OnSnapshot;
            _daemon.InputSampled -= OnInputSample;
            _daemon.Disconnected -= OnDisconnected;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    /// <summary>
    /// A live window owns the instance lock, so the tray retires itself once it has handed the
    /// window over: the user's ruling is that no tray process exists while a GUI window does.
    /// </summary>
    private static void Retire(Action? shutdown, IClassicDesktopStyleApplicationLifetime lifetime)
        => (shutdown ?? (() => lifetime.Shutdown()))();

    /// <summary>
    /// Releases only the instance lease before spawning the render. The tray's activation server
    /// stays up until a render-role readiness probe proves that the new window is serving. Any
    /// failure kills the incomplete successor and reclaims the lease, leaving this tray resident.
    /// </summary>
    internal static async Task<bool> HandOverToRenderAsync(
        DaemonSupervisor daemon,
        RenderSupervisor render,
        Action? releaseInstance,
        Func<GuiInstanceIdentity, GuiActivationOptions, CancellationToken, Task<bool>> showPoll,
        Func<Task> pollDelay,
        TimeSpan budget,
        Action? shutdown,
        IClassicDesktopStyleApplicationLifetime lifetime,
        Func<bool>? reacquireInstance = null)
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        var elapsed = Stopwatch.StartNew();
        var handedOver = false;

        try
        {
            daemon.EnsureRunning();
            releaseInstance?.Invoke();
            render.Show();

            while (render.IsAlive)
            {
                var remaining = budget - elapsed.Elapsed;
                var probeBudget = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
                var options = new GuiActivationOptions(
                    probeBudget < TimeSpan.FromMilliseconds(250) ? probeBudget : TimeSpan.FromMilliseconds(250),
                    probeBudget < TimeSpan.FromSeconds(1) ? probeBudget : TimeSpan.FromSeconds(1));

                if (await showPoll(identity, options, CancellationToken.None))
                {
                    handedOver = true;
                    break;
                }

                if (elapsed.Elapsed >= budget)
                {
                    break;
                }

                await pollDelay();
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] render handover failed: {exception.Message}");
        }

        if (handedOver)
        {
            Retire(shutdown, lifetime);
            return true;
        }

        try
        {
            render.Stop();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] render handover cleanup failed: {exception.Message}");
        }

        var reclaimed = false;
        try
        {
            reclaimed = reacquireInstance?.Invoke() ?? true;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] render handover lease rollback failed: {exception.Message}");
        }

        if (reclaimed)
        {
            // The tray is the live holder again: re-assert its endpoint in discovery so a second
            // launch reaches the surviving tray, not the render successor that just failed.
            GuiActivationServer.ActiveInstance?.RepublishEndpoint();
        }

        Trace.WriteLine($"[DeziroJoy] render handover rolled back; instance lease reclaimed: {reclaimed}");
        return false;
    }

    /// <summary>
    /// Sends a Show for a render process that is already alive. The tray owns the activation
    /// channel, so the request is written over that pipe exactly as a second launch would
    /// write it, and the channel routes it to the render supervisor.
    /// </summary>
    private static void ShowLiveRenderProcess()
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess();

        _ = GuiActivationClient.TryShowAsync(identity, GuiActivationOptions.Default, CancellationToken.None);
    }
}

/// <summary>
/// The production side of the process-launch seam: the real OS process table, plus the command
/// that re-enters this product with a given argument.
/// </summary>
internal static class ProcessLauncher
{
    internal static ProcessStartInfo DaemonStartInfo() => EntryStartInfo("--daemon");

    internal static ProcessStartInfo RenderStartInfo() => EntryStartInfo("--gui");

    internal static ProcessStartInfo TrayStartInfo() => EntryStartInfo("--tray");

    internal static IProcessHandle? Start(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);

        if (process is null)
        {
            return null;
        }

        process.EnableRaisingEvents = true;

        var handle = new SystemProcessHandle(process);
        process.Exited += handle.OnProcessExited;

        return handle;
    }

    /// <summary>
    /// The command that re-enters this product with <paramref name="argument"/>. The published
    /// executable is started directly; under "dotnet run" - where the process path is the
    /// muxer - the host is started with the entry assembly beside it, which is the same shape
    /// the current entry uses.
    /// </summary>
    private static ProcessStartInfo EntryStartInfo(string argument)
    {
        var executable = Environment.ProcessPath;
        var fileName = executable ?? "dotnet";
        var arguments = argument;

        if (string.IsNullOrEmpty(executable) || Program.IsDotNetHost(executable))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;

            if (!string.IsNullOrEmpty(assembly))
            {
                arguments = $"\"{assembly}\" {argument}";
            }
        }

        return new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
    }

    private sealed class SystemProcessHandle(Process process) : IProcessHandle
    {
        private bool _disposed;

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    // No process is associated with the handle any more: treat it as ended.
                    return true;
                }
            }
        }

        public event Action? Exited;

        public void Kill(bool entireTree)
        {
            try
            {
                process.Kill(entireTree);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Already gone, or not ours to terminate.
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => process.WaitForExitAsync(cancellationToken);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process.Dispose();
        }

        internal void OnProcessExited(object? sender, EventArgs e) => Exited?.Invoke();
    }
}
