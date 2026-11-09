using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Core.Platform;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

namespace DeziroJoy.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (TrayHost.IsTrayProcess)
            {
                TrayHost.InstallTray(this, desktop);
            }
            else
            {
                InstallRenderProcess(desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The render process: a stateless window over the daemon's authoritative state. While the
    /// window is alive it owns the instance lease and activation channel but no tray; its close
    /// and minimize policies are completed before the process exits.
    /// </summary>
    private static void InstallRenderProcess(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // The EngineHost below is a presentation seam, never a mapping engine: a provider that
        // reports no pads and a sink that injects nothing, and it is never started. The daemon
        // is the sole mapping authority, so a render process that cannot reach it opens in the
        // connection-error state and offers reconnect - there is no local-engine fallback,
        // because a GUI injecting on its own behalf is exactly the UIPI-silent-drop failure.
        var store = new FileProfileStore();
        var provider = new NoGamepadProvider();
        var sink = new NullOutputSink();
        var presentationHost = new EngineHost(provider, sink, store);

        // The settings service is built before the view model so gamepad ownership and both
        // tray policies use the same persisted instance as the render lifetime coordinator.
        var themes = new ThemeService();
        var controller = new DaemonController(new NamedPipeDaemonEndpoint());

        var viewModel = new MainWindowViewModel(
            presentationHost,
            store,
            daemon: controller,
            settings: themes);

        var services = new ServiceCollection();
        services.AddSingleton<IGamepadProvider>(provider);
        services.AddSingleton<IOutputSink>(sink);
        services.AddSingleton<IProfileStore>(store);
        services.AddSingleton(presentationHost);
        services.AddSingleton(viewModel);
        services.AddSingleton(themes);
        services.AddSingleton<IElevationService, ElevationService>();

        var serviceProvider = services.BuildServiceProvider();

        themes.ApplyPersisted();

        var host = serviceProvider.GetRequiredService<EngineHost>();

        try
        {
            controller.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception
            is DaemonUnavailableException or DaemonHandshakeException or IOException or TimeoutException
            || exception is OperationCanceledException)
        {
            // The daemon is the authority and there is no local fallback: the window opens in
            // the connection-error state and offers reconnect.
            viewModel.ReportDaemonUnavailable(exception.Message);
        }

        RenderProcess.InstallWindow(desktop, viewModel, host, themes, daemon: DesktopLauncher.Daemon);
    }
}

internal sealed class RenderWindowCoordinator
{
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly ThemeService _themes;
    private readonly Func<ProcessStartInfo, IProcessHandle?> _spawnTray;
    private readonly Action _releaseInstance;
    private readonly Func<bool> _reacquireInstance;
    private readonly Func<GuiInstanceIdentity, GuiInstanceRole, GuiActivationOptions, CancellationToken, Task<bool>> _probe;
    private readonly Func<Task> _pollDelay;
    private readonly TimeSpan _handoverBudget;
    private readonly Func<Task<bool>> _stopDaemon;
    private Task<bool>? _handover;
    private bool _closeApproved;
    private bool _exitInProgress;

    internal RenderWindowCoordinator(
        MainWindow window,
        MainWindowViewModel viewModel,
        ThemeService themes,
        Func<ProcessStartInfo, IProcessHandle?> spawnTray,
        Action releaseInstance,
        Func<bool> reacquireInstance,
        Func<GuiInstanceIdentity, GuiInstanceRole, GuiActivationOptions, CancellationToken, Task<bool>> probe,
        Func<Task> pollDelay,
        TimeSpan handoverBudget,
        Func<Task<bool>> stopDaemon)
    {
        _window = window;
        _viewModel = viewModel;
        _themes = themes;
        _spawnTray = spawnTray;
        _releaseInstance = releaseInstance;
        _reacquireInstance = reacquireInstance;
        _probe = probe;
        _pollDelay = pollDelay;
        _handoverBudget = handoverBudget;
        _stopDaemon = stopDaemon;
    }

    internal async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved)
        {
            return;
        }

        e.Cancel = true;
        if (_exitInProgress)
        {
            return;
        }

        _exitInProgress = true;
        var approved = _themes.CloseToTrayEnabled
            ? await HandOverToTrayAsync()
            : await StopDaemonAsync();
        if (!approved)
        {
            if (_themes.CloseToTrayEnabled)
            {
                _viewModel.StatusMessage = "无法切换到托盘：托盘进程未就绪，窗口已保留。";
            }

            _exitInProgress = false;
            return;
        }

        ApproveClose();
    }

    internal async Task<bool> MinimizeAsync()
    {
        if (_exitInProgress)
        {
            if (!_themes.CloseToTrayEnabled)
            {
                _window.WindowState = WindowState.Normal;
            }

            return true;
        }

        if (!_themes.MinimizeToTrayEnabled)
        {
            return false;
        }

        _exitInProgress = true;
        if (!await HandOverToTrayAsync())
        {
            _window.WindowState = WindowState.Normal;
            _viewModel.StatusMessage = "无法切换到托盘：托盘进程未就绪，窗口已保留。";
            _exitInProgress = false;
            return true;
        }

        ApproveClose();
        return true;
    }

    private async Task<bool> StopDaemonAsync()
    {
        try
        {
            if (await _stopDaemon())
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] full exit failed: {exception.Message}");
        }

        _viewModel.StatusMessage = "无法完全退出：守护进程未确认停止。请重试。";
        return false;
    }

    private async Task<bool> HandOverToTrayAsync()
    {
        var current = _handover ??= PerformHandOverAsync();
        var handedOver = false;
        try
        {
            handedOver = await current;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] tray handover failed: {exception.Message}");
        }

        if (!handedOver && ReferenceEquals(_handover, current))
        {
            _handover = null;
        }

        return handedOver;
    }

    private async Task<bool> PerformHandOverAsync()
    {
        IProcessHandle? tray = null;
        try
        {
            tray = _spawnTray(ProcessLauncher.TrayStartInfo());
            if (tray is null)
            {
                return false;
            }

            _releaseInstance();
            var identity = GuiInstanceIdentity.ForCurrentProcess();
            var elapsed = Stopwatch.StartNew();

            while (!tray.HasExited)
            {
                var remaining = _handoverBudget - elapsed.Elapsed;
                var probeBudget = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
                var options = new GuiActivationOptions(
                    probeBudget < TimeSpan.FromMilliseconds(250) ? probeBudget : TimeSpan.FromMilliseconds(250),
                    probeBudget < TimeSpan.FromSeconds(1) ? probeBudget : TimeSpan.FromSeconds(1));

                if (await _probe(identity, GuiInstanceRole.Tray, options, CancellationToken.None))
                {
                    tray.Dispose();
                    return true;
                }

                if (elapsed.Elapsed >= _handoverBudget)
                {
                    break;
                }

                await _pollDelay();
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] tray handover failed: {exception.Message}");
        }

        if (tray is not null)
        {
            try
            {
                if (!tray.HasExited)
                {
                    tray.Kill(entireTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await tray.WaitForExitAsync(timeout.Token);
                }
            }
            catch (Exception exception) when (exception
                is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                Trace.WriteLine($"[DeziroJoy] incomplete tray cleanup failed: {exception.Message}");
            }
            finally
            {
                tray.Dispose();
            }
        }

        var reclaimed = false;
        try
        {
            reclaimed = _reacquireInstance();
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] tray handover lease rollback failed: {exception.Message}");
        }

        Trace.WriteLine($"[DeziroJoy] tray handover rolled back; instance lease reclaimed: {reclaimed}");
        return false;
    }

    private void ApproveClose()
    {
        _closeApproved = true;
        _window.Close();
    }
}

/// <summary>Owns the close/minimize policy of the one stateless render window.</summary>
internal static class RenderProcess
{
    private static readonly TimeSpan HandoverBudget = TimeSpan.FromSeconds(10);

    internal static MainWindow InstallWindow(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindowViewModel viewModel,
        EngineHost host,
        ThemeService themes,
        Func<ProcessStartInfo, IProcessHandle?>? spawnTray = null,
        Action? shutdown = null,
        DaemonSupervisor? daemon = null,
        Action? releaseInstance = null,
        Func<bool>? reacquireInstance = null,
        Func<GuiInstanceIdentity, GuiInstanceRole, GuiActivationOptions, CancellationToken, Task<bool>>? probe = null,
        Func<Task>? pollDelay = null,
        TimeSpan? handoverBudget = null,
        Func<Task<bool>>? stopDaemon = null)
    {
        spawnTray ??= ProcessLauncher.Start;
        releaseInstance ??= DesktopLauncher.ReleaseInstanceLease;
        reacquireInstance ??= DesktopLauncher.TryReacquireInstanceLease;
        probe ??= static (identity, role, options, cancellationToken)
            => GuiActivationClient.TryProbeAsync(identity, role, options, cancellationToken);
        pollDelay ??= static () => Task.Delay(100);
        stopDaemon ??= () => Task.Run(() => daemon?.Stop() ?? true);

        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow(viewModel, host, themes);
        var coordinator = new RenderWindowCoordinator(
            window,
            viewModel,
            themes,
            spawnTray,
            releaseInstance,
            reacquireInstance,
            probe,
            pollDelay,
            handoverBudget ?? HandoverBudget,
            stopDaemon);
        window.Closing += coordinator.OnClosing;
        window.MinimizeToTrayRequested = coordinator.MinimizeAsync;
        lifetime.MainWindow = window;
        window.Closed += (_, _) => (shutdown ?? (() => lifetime.Shutdown()))();
        window.Show();
        DesktopLauncher.NotifyWindowShown();

        return window;
    }
}
