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
            // Same wiring the headless daemon uses; only the lifetime around it differs.
            var composition = new EngineComposition();

            var services = new ServiceCollection();
            services.AddSingleton(composition.Provider);
            services.AddSingleton(composition.Sink);
            services.AddSingleton<IProfileStore>(composition.ProfileStore);
            services.AddSingleton(composition.Host);
            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<ThemeService>();
            services.AddSingleton<IElevationService, ElevationService>();

            var serviceProvider = services.BuildServiceProvider();

            var themes = serviceProvider.GetRequiredService<ThemeService>();
            themes.ApplyPersisted();

            var viewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
            var host = serviceProvider.GetRequiredService<EngineHost>();

            desktop.MainWindow = new MainWindow(viewModel, host, themes);

            // The tray owns hide-on-close, so the engine keeps mapping with no window
            // open. Constructed exactly once here; disposed when the lifetime exits.
            var tray = new TrayService(this, desktop, themes);
            tray.Install(desktop.MainWindow);
            desktop.Exit += (_, _) => tray.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
