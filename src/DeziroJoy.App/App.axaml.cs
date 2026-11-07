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
/// The render process's window policy: the window IS the process. Closing it shuts the desktop
/// lifetime down, which ends the process immediately - unsaved drafts are discarded by design,
/// because the render process is stateless - while the tray and the daemon stay resident and
/// keep mapping.
/// </summary>
internal static class RenderProcess
{
    public static MainWindow InstallWindow(
        IClassicDesktopStyleApplicationLifetime lifetime,
        MainWindowViewModel viewModel,
        EngineHost host,
        ThemeService themes)
    {
        // The window is the process, so the lifetime ends with it: no explicit shutdown call,
        // because a close must not have to be routed through anything to end the process.
        lifetime.ShutdownMode = ShutdownMode.OnMainWindowClose;

        var window = new MainWindow(viewModel, host, themes);
        lifetime.MainWindow = window;
        window.Show();

        return window;
    }
}
