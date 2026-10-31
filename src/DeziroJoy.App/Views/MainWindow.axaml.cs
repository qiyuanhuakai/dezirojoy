using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;

namespace DeziroJoy.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(16);

    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _previewTimer = new() { Interval = PreviewInterval };

    private readonly DispatcherTimer _syncTimer = new() { Interval = SyncInterval };

    public MainWindow()
        : this(CreateRuntime())
    {
    }

    public MainWindow((MainWindowViewModel ViewModel, EngineHost Host) runtime)
        : this(runtime.ViewModel, runtime.Host)
    {
    }

    public MainWindow(MainWindowViewModel viewModel, EngineHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        Wire(host ?? viewModel.Host);
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private void Wire(EngineHost host)
    {
        _previewTimer.Tick += OnPreviewTick;
        _syncTimer.Tick += OnSyncTick;
        _previewTimer.Start();
        _syncTimer.Start();

        host.Sampled += (_, args) => Dispatcher.UIThread.Post(() => ViewModel.ApplySample(args));

        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _syncTimer.Stop();
            host.Stop();
        };
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        ViewModel.MouseSensitivity.Advance(PreviewInterval);
        ViewModel.ScrollSensitivity.Advance(PreviewInterval);
    }

    private void OnSyncTick(object? sender, EventArgs e)
        => ViewModel.SyncControllers();

    private void TitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void MinimizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClick(object? sender, RoutedEventArgs e)
        => Close();

    private static (MainWindowViewModel ViewModel, EngineHost Host) CreateRuntime()
    {
        var store = new FileProfileStore();
        var provider = PlatformFactory.TryCreateProvider(out var gamepadProvider)
            ? gamepadProvider
            : new NoGamepadProvider();
        var sink = PlatformFactory.TryCreateSink(out var outputSink, out _)
            ? outputSink
            : new NullOutputSink();
        var host = new EngineHost(provider, sink, store);

        return (new MainWindowViewModel(host, store), host);
    }
}
