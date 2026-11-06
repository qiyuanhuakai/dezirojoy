using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
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
            var runtime = GuiRuntimeFactory.CreateDefault()
                .CreateAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var composition = runtime.Local ?? new EngineComposition(presentationOnly: true);

            // The settings service is built before the view model so the persisted
            // preferences reach it: the view model reads and writes the 允许手柄操作 choice
            // through this instance, and one built without it silently ignores the setting.
            var themes = new ThemeService();
            var viewModel = new MainWindowViewModel(
                composition.Host,
                composition.ProfileStore,
                daemon: runtime.Controller,
                daemonRequired: OperatingSystem.IsWindows(),
                settings: themes);

            var services = new ServiceCollection();
            services.AddSingleton(composition.Provider);
            services.AddSingleton(composition.Sink);
            services.AddSingleton<IProfileStore>(composition.ProfileStore);
            services.AddSingleton(composition.Host);
            services.AddSingleton(viewModel);
            services.AddSingleton(themes);
            services.AddSingleton<IElevationService, ElevationService>();

            var serviceProvider = services.BuildServiceProvider();

            themes.ApplyPersisted();

            var host = serviceProvider.GetRequiredService<EngineHost>();
            if (runtime.UnavailableReason is { } unavailableReason)
            {
                viewModel.ReportDaemonUnavailable(unavailableReason);
            }

            // The tray owns the window lifecycle: the view model, the runtime and the tray
            // outlive every window generation, and the window itself is a releasable
            // presentation resource built from them on demand. Closing or minimizing
            // destroys the window and frees its render resources, while the app stays
            // resident in the tray - the icon and its menu are the way back - and the
            // engine, hosted here or by the daemon, keeps mapping with no window open.
            var tray = new TrayService(this, desktop, themes, () => new MainWindow(viewModel, host, themes));

            // A second launch asked this GUI to show itself: answer by restoring the window.
            // The activation channel is Avalonia-free, so the restore is marshalled onto the
            // UI thread here.
            if (GuiActivationServer.ActiveInstance is { } activation)
            {
                activation.ShowRequested += ()
                    => Dispatcher.UIThread.InvokeAsync(tray.ShowMainWindow).GetTask();

                activation.BeginServing();
            }

            tray.Install();

            // The tray asks; the application - which owns the runtime - answers by shutting
            // the lifetime down. Every window is force-closed first, so each detaches its
            // timers and subscriptions, and Exit then disposes the runtime.
            tray.ExitRequested += () => desktop.Shutdown();

            desktop.Exit += (_, _) =>
            {
                tray.Dispose();

                // The runtime teardown awaits the daemon pipe closing down, and this handler
                // runs on the UI thread. Waiting for it here deadlocks: the UI thread is the
                // very thread the await needs, so the process never reaches its exit. Hand the
                // teardown to the thread pool instead and let the UI thread finish shutting
                // down. The daemon is a separate process, so nothing of the mapping is lost by
                // not waiting for the disconnect.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await viewModel.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        composition.Dispose();
                    }
                });
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
