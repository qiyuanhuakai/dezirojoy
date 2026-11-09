using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;

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

    /// <summary>Pause between render-answer polls while the show route waits for the window.</summary>
    private static readonly TimeSpan ShowPollPause = TimeSpan.FromMilliseconds(100);

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
            reacquireInstance: () => _instance?.TryReacquireLease() ?? false);

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
        Func<bool>? reacquireInstance = null)
    {
        themes.ApplyPersisted();

        showPoll ??= static (identity, options, cancellationToken)
            => GuiActivationClient.TryProbeAsync(identity, GuiInstanceRole.Render, options, cancellationToken);
        pollDelay ??= static () => Task.Delay(ShowPollPause);
        var budget = pollBudget ?? ShowPollBudget;
        var handoverGate = new object();
        Task<bool>? handover = null;

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

        var tray = new TrayService(
            application,
            lifetime,
            themes,
            showMainWindow: async void () => await ShowAsync(),
            exit: () =>
            {
                if (!daemon.Stop())
                {
                    return;
                }

                if (render.Stop())
                {
                    Retire(shutdown, lifetime);
                }
            });

        tray.Install();

        if (activation is not null)
        {
            activation.ShowRequested += ShowAsync;
            activation.BeginServing();
        }

        lifetime.Exit += (_, _) =>
        {
            tray.Dispose();
            daemon.Dispose();
            render.Dispose();
        };

        daemon.EnsureRunning();

        return tray;
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
