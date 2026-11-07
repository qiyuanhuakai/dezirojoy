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
    /// The render process: a stateless window over the daemon's authoritative state. It owns no
    /// tray, no instance lock and no activation channel - all three belong to the tray - and
    /// closing its window ends the process immediately.
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

        // The settings service is built before the view model so the persisted preferences
        // reach it: the view model reads and writes the 允许手柄操作 choice through this
        // instance, and one built without it silently ignores the setting.
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

        RenderProcess.InstallWindow(desktop, viewModel, host, themes);
    }
}

/// <summary>
/// The render process's window policy: the window IS the process. Closing it wakes the tray
/// process first - the tray takes over the instance lock, the activation channel and the daemon
/// supervision - and only then ends the lifetime. Unsaved drafts are discarded by design,
/// because the render process is stateless, and the daemon keeps mapping across the handover.
/// </summary>
internal static class RenderProcess
{
    public static MainWindow InstallWindow(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindowViewModel viewModel,
        EngineHost host,
        ThemeService themes,
        Func<ProcessStartInfo, bool>? spawnTray = null,
        Action? shutdown = null)
    {
        spawnTray ??= static info =>
        {
            using var process = Process.Start(info);
            return process is not null;
        };

        // The window owns the process. Closing it first wakes the tray - which takes over the
        // instance lock, the activation channel and the daemon supervision - and only then
        // ends the lifetime, so the daemon keeps mapping across the handover and no second
        // GUI is ever constructed by the render process itself.
        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new MainWindow(viewModel, host, themes);
        lifetime.MainWindow = window;
        window.Closed += (_, _) =>
        {
            spawnTray(ProcessLauncher.TrayStartInfo());
            (shutdown ?? (() => lifetime.Shutdown()))();
        };
        window.Show();
        DesktopLauncher.NotifyWindowShown();

        return window;
    }
}
