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

/// <summary>
/// Supervises the resident daemon. The tray starts it and never hosts the mapping engine
/// itself: on Windows the start goes through <see cref="IElevationService"/> so the daemon runs
/// elevated, and on Linux the same entry assembly is started as the current user, because
/// uinput is not gated by UIPI and needs no elevation.
/// </summary>
internal sealed class DaemonSupervisor : IDisposable
{
    /// <summary>
    /// How long the exit waits for the daemon before killing whatever survived. Bounded so a
    /// wedged daemon can never hold the tray's exit open.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IElevationService _elevation;
    private readonly ProcessLaunch _launcher;
    private readonly Func<bool> _isWindows;
    private readonly Func<bool> _isDaemonReachable;
    private IProcessHandle? _daemon;
    private bool _elevatedRequested;
    private bool _disposed;

    public DaemonSupervisor(
        IElevationService? elevation = null,
        ProcessLaunch? launch = null,
        Func<bool>? isWindows = null,
        Func<bool>? isDaemonReachable = null)
    {
        _elevation = elevation ?? new ElevationService();
        _launcher = launch ?? ProcessLauncher.Start;
        _isWindows = isWindows ?? (static () => OperatingSystem.IsWindows());
        _isDaemonReachable = isDaemonReachable ?? (static () => IsDaemonReachable(IpcContract.PipeName));
    }

    public bool IsRunning => _daemon is { HasExited: false };

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

        // A daemon the render process already started answers on the mapping pipe, so there is
        // nothing to start: relaunching would show a UAC prompt for a copy the instance mutex
        // makes exit immediately. Probed on every call, never cached, so a daemon that
        // genuinely died is still restarted on the next menu action.
        if (_isDaemonReachable())
        {
            return;
        }

        if (_isWindows())
        {
            // The elevated relaunch shows a UAC prompt, so it is asked for once per tray
            // process: the daemon's own single-instance mutex makes a duplicate launch a clean
            // no-op, and re-prompting on every menu action would be unusable.
            if (_elevatedRequested)
            {
                return;
            }

            _elevatedRequested = true;
            _elevation.RelaunchElevated("--daemon");
            return;
        }

        _daemon = _launcher(ProcessLauncher.DaemonStartInfo());
    }

    /// <summary>
    /// Stops the daemon this supervisor started: stop, wait, then kill whatever survived the
    /// wait. The wait is bounded, so a daemon that refuses to die cannot hold the tray's exit
    /// open.
    /// </summary>
    public void Stop()
    {
        if (_daemon is not { } daemon)
        {
            return;
        }

        _daemon = null;

        TryKill(daemon);
        TryWait(daemon);

        if (!daemon.HasExited)
        {
            TryKill(daemon);
        }

        daemon.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();
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

    private static void TryKill(IProcessHandle daemon)
    {
        try
        {
            daemon.Kill(entireTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The daemon may already be gone, or refuse the terminate; neither is worth
            // failing the tray's exit over.
        }
    }

    private static void TryWait(IProcessHandle daemon)
    {
        try
        {
            using var timeout = new CancellationTokenSource(StopTimeout);
            daemon.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception
            is OperationCanceledException or AggregateException or InvalidOperationException)
        {
            // A daemon that outlives the wait is killed as residue instead.
        }
    }
}

/// <summary>
/// Supervises the render process: the stateless Avalonia window the tray starts on demand.
/// The tray never hosts a window itself, so this is the only thing that can put one on screen.
/// </summary>
internal sealed class RenderSupervisor : IDisposable
{
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

    /// <summary>
    /// The tray menu's 显示主窗口. A render process that is not alive is started; one that is
    /// alive already has its window on screen - closing it would have ended the process - so
    /// the show is sent over the activation pipe instead of spawning a second render process.
    /// </summary>
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

    /// <summary>
    /// What the activation channel answers a Show with: start a render process when none is
    /// alive, and nothing when one already is. Deliberately not <see cref="Show"/> - routing a
    /// pipe Show straight back into the pipe would loop.
    /// </summary>
    public void EnsureVisible()
    {
            if (_disposed || IsAlive)
            {
                return;
            }

            Spawn();
        }

        /// <summary>
        /// Ends the render process this supervisor spawned, as the tray's exit item does: the
        /// window is the process, so killing the child is the whole cleanup. A render that
        /// already exited on its own is a no-op.
        /// </summary>
        public void Stop()
        {
            if (_disposed || _render is not { } render)
            {
                return;
            }

            render.Kill(entireTree: true);
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
        }
    }

    private void Spawn()
    {
        var handle = _launcher(ProcessLauncher.RenderStartInfo());

        if (handle is null)
        {
            return;
        }

        _render = handle;
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
        }
    }
}

/// <summary>
/// The tray process: a resident Avalonia application with no window. It owns the instance lock,
/// the activation channel, the persisted theme, the tray icon and the two supervisors, and it
/// starts the daemon on its own - the GUI never asks for it.
/// </summary>
internal static class TrayHost
{
    /// <summary>True once this process is the tray rather than a render process.</summary>
    internal static bool IsTrayProcess { get; private set; }

    /// <summary>Pause between render-answer polls while the show route waits for the window.</summary>
    private static readonly TimeSpan ShowPollPause = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long the show route gives the render to answer before the tray retires anyway. The
    /// render has to win the instance lock, build its window and start serving, which is a
    /// startup-scale wait, not a spin.
    /// </summary>
    private static readonly TimeSpan ShowPollBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Releases the instance lease and the activation channel, set by <see cref="Run"/> for the
    /// life of the tray process. The show route calls it before it spawns the render, because
    /// the render arbitrates the lock exactly once with no retry - a tray still holding it makes
    /// the render lose, ask this tray to show itself and exit with no GUI. Null in tests, which
    /// install a tray without a real lease.
    /// </summary>
    private static Action? _releaseInstance;

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

        // The lease and the activation channel are held for the whole life of the tray: the
        // lock is what makes this the one tray, and the channel is how a second launch asks it
        // to show a window. They are lifted out of using statements because the show route
        // releases them before it spawns the render: the render arbitrates the lock exactly
        // once with no retry, so a tray still holding it makes the render lose and exit with
        // no GUI at all.
        var lease = arbitration.Lease!;
        var activation = GuiActivationServer.StartActive(identity);
        _releaseInstance = () =>
        {
            activation.Dispose();
            lease.Dispose();
        };

        try
        {
            Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
            return 0;
        }
        finally
        {
            // Idempotent by construction: the show route may already have released both, and a
            // double dispose is exactly what the guards in each type exist to absorb.
            _releaseInstance?.Invoke();
            _releaseInstance = null;
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
            releaseInstance: _releaseInstance);

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
        TimeSpan? pollBudget = null)
    {
        themes.ApplyPersisted();

        showPoll ??= static (identity, options, cancellationToken)
            => GuiActivationClient.TryShowAsync(identity, options, cancellationToken);
        pollDelay ??= static () => Task.Delay(ShowPollPause);
        var budget = pollBudget ?? ShowPollBudget;

        var tray = new TrayService(
            application,
            lifetime,
            themes,
            showMainWindow: async void () =>
            {
                // The daemon is started by the tray, never by the GUI, and a menu action is
                // also the retry point for a daemon whose pipe never answered.
                daemon.EnsureRunning();

                // The instance lock and the activation channel are released BEFORE the render
                // is spawned: the render arbitrates the lock exactly once with no retry, so a
                // tray still holding it makes the render lose, ask this tray to show itself and
                // exit with no window at all. Released first, the render wins the lock, takes
                // the channel over and answers on the same pipe name.
                releaseInstance?.Invoke();

                render.Show();

                // The render's NotifyWindowShown orders window-show before BeginServing, so an
                // answer on the pipe is proof the window is up. The poll is bounded by the
                // budget and also stops the moment the render dies, so the tray can neither
                // hang on a dead child nor outlive a window that never came.
                var identity = GuiInstanceIdentity.ForCurrentProcess();
                var elapsed = Stopwatch.StartNew();

                while (render.IsAlive)
                {
                    if (await showPoll(identity, GuiActivationOptions.Default, CancellationToken.None))
                    {
                        break;
                    }

                    if (elapsed.Elapsed >= budget)
                    {
                        break;
                    }

                    await pollDelay();
                }

                Retire(shutdown, lifetime);
            },
            exit: () =>
            {
                daemon.Stop();
                render.Stop();
                Retire(shutdown, lifetime);
            });

        tray.Install();

        if (activation is not null)
        {
            activation.ShowRequested += () =>
            {
                render.EnsureVisible();
                Retire(shutdown, lifetime);
                return Task.CompletedTask;
            };

            // Serving starts only once the tray can actually answer, so a launch arriving
            // while the supervisors are still being built waits for the live listener.
            activation.BeginServing();
        }

        lifetime.Exit += (_, _) =>
        {
            tray.Dispose();
            daemon.Dispose();
            render.Dispose();
        };

        // The daemon is resident on its own: it starts with the tray, not with a GUI, so a
        // configuration persisted by an earlier session maps before any window exists.
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
