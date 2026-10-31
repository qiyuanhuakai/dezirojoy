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
            var store = new FileProfileStore();
            var provider = PlatformFactory.TryCreateProvider(out var gamepadProvider)
                ? gamepadProvider
                : new NoGamepadProvider();
            var sink = PlatformFactory.TryCreateSink(out var outputSink, out _)
                ? outputSink
                : new NullOutputSink();

            var services = new ServiceCollection();
            services.AddSingleton(provider);
            services.AddSingleton(sink);
            services.AddSingleton<IProfileStore>(store);
            services.AddSingleton<EngineHost>();
            services.AddSingleton<MainWindowViewModel>();

            var serviceProvider = services.BuildServiceProvider();
            var viewModel = serviceProvider.GetRequiredService<MainWindowViewModel>();
            var host = serviceProvider.GetRequiredService<EngineHost>();

            desktop.MainWindow = new MainWindow(viewModel, host);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
