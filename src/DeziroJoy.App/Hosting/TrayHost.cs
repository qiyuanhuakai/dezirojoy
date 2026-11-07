using System.ComponentModel;
using System.Diagnostics;
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
    private IProcessHandle? _daemon;
    private bool _elevatedRequested;
    private bool _disposed;

    public DaemonSupervisor(
        IElevationService? elevation = null,
        ProcessLaunch? launch = null,
        Func<bool>? isWindows = null)
    {
        _elevation = elevation ?? new ElevationService();
        _launcher = launch ?? ProcessLauncher.Start;
        _isWindows = isWindows ?? (static () => OperatingSystem.IsWindows());
    }

    public bool IsRunning => _daemon is { HasExited: false };

    /// <summary>
    /// Starts the daemon when this supervisor does not already hold a live one. A daemon whose
    /// pipe never answers is not something the tray acts on: it stays resident and the next
    /// menu action tries again. There is no local-engine fallback - the daemon is the only
    /// mapping authority, and a tray that injected on its own behalf would be the
    /// UIPI-silent-drop failure the architecture exists to prevent.
    /// </summary>
    public void EnsureRunning()
    {
        if (_disposed || IsRunning)
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
        // to show a window.
        using var lease = arbitration.Lease!;
        using var activation = GuiActivationServer.StartActive(identity);

        Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
        return 0;
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
            GuiActivationServer.ActiveInstance);

    internal static TrayService InstallTray(
        Application application,
        IClassicDesktopStyleApplicationLifetime lifetime,
        ThemeService themes,
        DaemonSupervisor daemon,
        RenderSupervisor render,
        GuiActivationServer? activation = null,
        Action? shutdown = null)
    {
        themes.ApplyPersisted();

        var tray = new TrayService(
            application,
            lifetime,
            themes,
            showMainWindow: () =>
            {
                // The daemon is started by the tray, never by the GUI, and a menu action is
                // also the retry point for a daemon whose pipe never answered.
                daemon.EnsureRunning();
                render.Show();
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
