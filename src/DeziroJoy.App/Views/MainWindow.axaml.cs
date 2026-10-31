using System.Reflection;
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
    {
        var store = new FileProfileStore();
        var host = new EngineHost(new NoGamepadProvider(), new NullOutputSink(), store);

        AvaloniaXamlLoader.Load(this);
        DataContext = new MainWindowViewModel(host, store);
        Wire(host);
    }

    public MainWindow(MainWindowViewModel viewModel, EngineHost? host = null)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        Wire(host ?? HostOf(viewModel));
    }

    // The view model owns the host without exposing it; this keeps live sampling working
    // for callers that only have the view model.
    private static EngineHost? HostOf(MainWindowViewModel viewModel)
    {
        foreach (var field in viewModel.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.GetValue(viewModel) is EngineHost host)
            {
                return host;
            }
        }

        return null;
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private void Wire(EngineHost? host)
    {
        _previewTimer.Tick += OnPreviewTick;
        _syncTimer.Tick += OnSyncTick;
        _previewTimer.Start();
        _syncTimer.Start();

        if (host is not null)
        {
            host.Sampled += (_, args) => Dispatcher.UIThread.Post(() => ViewModel.ApplySample(args));
        }

        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _syncTimer.Stop();
            host?.Stop();
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
}
