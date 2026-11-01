using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            desktop.MainWindow = new MainWindow(viewModel, host, themes);

            // The tray owns hide-on-close, so the engine keeps mapping with no window
            // open. Constructed exactly once here; disposed when the lifetime exits.
            var tray = new TrayService(this, desktop, themes);
            tray.Install(desktop.MainWindow);
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
