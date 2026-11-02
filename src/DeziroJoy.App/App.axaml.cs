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
            var viewModel = new MainWindowViewModel(
                composition.Host,
                composition.ProfileStore,
                daemon: runtime.Controller,
                daemonRequired: OperatingSystem.IsWindows());

            var services = new ServiceCollection();
            services.AddSingleton(composition.Provider);
            services.AddSingleton(composition.Sink);
            services.AddSingleton<IProfileStore>(composition.ProfileStore);
            services.AddSingleton(composition.Host);
            services.AddSingleton(viewModel);
            services.AddSingleton<ThemeService>();
            services.AddSingleton<IElevationService, ElevationService>();

            var serviceProvider = services.BuildServiceProvider();

            var themes = serviceProvider.GetRequiredService<ThemeService>();
            themes.ApplyPersisted();

            var host = serviceProvider.GetRequiredService<EngineHost>();
            if (runtime.UnavailableReason is { } unavailableReason)
            {
                viewModel.ReportDaemonUnavailable(unavailableReason);
            }

            // The tray owns the window lifecycle: the view model, the runtime and the tray
            // outlive every window generation, and the window itself is a releasable
            // presentation resource built from them on demand.
            var tray = new TrayService(this, desktop, themes, () => new MainWindow(viewModel, host, themes));

            // A second launch asked this GUI to show itself: answer by restoring the window.
            // The activation channel is Avalonia-free, so the restore is marshalled onto the
            // UI thread here.
            if (GuiActivationServer.ActiveInstance is { } activation)
            {
                activation.ShowRequested += ()
                    => Dispatcher.UIThread.InvokeAsync(tray.ShowMainWindow).GetTask();
            }

            tray.Install();

            // The tray asks; the application - which owns the runtime - answers by shutting
            // the lifetime down. Every window is force-closed first, so each detaches its
            // timers and subscriptions, and Exit then disposes the runtime.
            tray.ExitRequested += () => desktop.Shutdown();

            desktop.Exit += (_, _) =>
            {
                tray.Dispose();
                viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
                composition.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
