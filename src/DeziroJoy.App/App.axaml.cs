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

            serviceProvider.GetRequiredService<ThemeService>().ApplyPersisted();

            var viewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
            var host = serviceProvider.GetRequiredService<EngineHost>();

            desktop.MainWindow = new MainWindow(viewModel, host);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
