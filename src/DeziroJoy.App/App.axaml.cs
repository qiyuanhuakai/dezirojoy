using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views;
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

        RenderProcess.InstallWindow(
            desktop,
            viewModel,
            host,
            themes,
            daemon: DesktopLauncher.Daemon,
            ensureTray: DesktopLauncher.EnsureTrayRunning,
            renderStopped: DesktopLauncher.NotifyRenderStopped,
            exitTray: DesktopLauncher.RequestTrayExitAsync);

    }
}

internal sealed class RenderWindowCoordinator
{
    private readonly MainWindow _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly ThemeService _themes;
    private readonly Func<bool> _ensureTray;
    private readonly Func<Task<bool>> _stopDaemon;
    private readonly Action _renderStopped;
    private readonly Func<Task<bool>> _exitTray;
    private bool _closeApproved;
    private bool _exitInProgress;

    internal RenderWindowCoordinator(
        MainWindow window,
        MainWindowViewModel viewModel,
        ThemeService themes,
        Func<bool> ensureTray,
        Func<Task<bool>> stopDaemon,
        Action renderStopped,
        Func<Task<bool>> exitTray)
    {
        _window = window;
        _viewModel = viewModel;
        _themes = themes;
        _ensureTray = ensureTray;
        _stopDaemon = stopDaemon;
        _renderStopped = renderStopped;
        _exitTray = exitTray;
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
            ? EnsureTray()
            : await StopDaemonAsync() && await ExitTrayAsync();
        if (approved)
        {
            ApproveClose();
        }
        else
        {
            _exitInProgress = false;
        }
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
        if (!EnsureTray())
        {
            _window.WindowState = WindowState.Normal;
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

    private bool EnsureTray()
    {
        try
        {
            if (_ensureTray())
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] presentation host check failed: {exception.Message}");
        }

        _viewModel.StatusMessage = "无法切换到托盘：呈现宿主未启动，窗口已保留。";
        return false;
    }

    private async Task<bool> ExitTrayAsync()
    {
        try
        {
            if (await _exitTray())
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] tray exit failed: {exception.Message}");
        }

        _viewModel.StatusMessage = "无法完全退出：托盘未确认退出。请重试。";
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
    internal static MainWindow InstallWindow(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindowViewModel viewModel,
        EngineHost host,
        ThemeService themes,
        Action? shutdown = null,
        DaemonSupervisor? daemon = null,
        Func<Task<bool>>? stopDaemon = null,
        Func<bool>? ensureTray = null,
        Action? renderStopped = null,
        Func<Task<bool>>? exitTray = null)
    {
        stopDaemon ??= () => Task.Run(() => daemon?.Stop() ?? true);
        ensureTray ??= static () => true;
        renderStopped ??= static () => { };
        exitTray ??= static () => Task.FromResult(true);

        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow(viewModel, host, themes);
        var coordinator = new RenderWindowCoordinator(
            window,
            viewModel,
            themes,
            ensureTray,
            stopDaemon,
            renderStopped,
            exitTray);
        window.Closing += coordinator.OnClosing;
        window.MinimizeToTrayRequested = coordinator.MinimizeAsync;
        lifetime.MainWindow = window;
        window.Closed += (_, _) =>
        {
            renderStopped();
            (shutdown ?? (() => lifetime.Shutdown()))();
        };
        window.Show();
        DesktopLauncher.NotifyWindowShown();

        return window;
    }
}
