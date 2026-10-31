using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
        LoadWindowIcon();
        ConfigureResizeGrips();
        Wire(host ?? viewModel.Host);
    }

    private void ConfigureResizeGrips()
    {
        // Avalonia has no Cursor type converter for XAML, so the resize directions are
        // assigned here. The ElementRole attributes on the grips drive the actual
        // hit-testing; these cursors only give the user the visual affordance.
        SetGripCursor("ResizeTop", StandardCursorType.SizeNorthSouth);
        SetGripCursor("ResizeBottom", StandardCursorType.SizeNorthSouth);
        SetGripCursor("ResizeLeft", StandardCursorType.SizeWestEast);
        SetGripCursor("ResizeRight", StandardCursorType.SizeWestEast);
        SetGripCursor("ResizeTopLeft", StandardCursorType.TopLeftCorner);
        SetGripCursor("ResizeTopRight", StandardCursorType.TopRightCorner);
        SetGripCursor("ResizeBottomLeft", StandardCursorType.BottomLeftCorner);
        SetGripCursor("ResizeBottomRight", StandardCursorType.BottomRightCorner);
    }

    private void SetGripCursor(string name, StandardCursorType type)
    {
        if (this.FindControl<Border>(name) is { } grip)
        {
            grip.Cursor = new Cursor(type);
        }
    }

    private void LoadWindowIcon()
    {
        // Avalonia's build targets register <ApplicationIcon> under this key
        // (see AvaloniaBuildTasks.targets: AvaloniaResource Link="!__AvaloniaDefaultWindowIcon"),
        // not under its source-relative path "Assets/dezirojoy.ico".
        var iconUri = new Uri(
            $"avares://{GetType().Assembly.GetName().Name}/!__AvaloniaDefaultWindowIcon");

        using (var stream = AssetLoader.Open(iconUri))
        {
            Icon = new WindowIcon(stream);
        }

        var titleBarIcon = this.FindControl<Image>("TitleBarIcon")!;
        using (var stream = AssetLoader.Open(iconUri))
        {
            titleBarIcon.Source = new Bitmap(stream);
        }
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
        UpdatePreviewLiveness();

        ViewModel.MouseSensitivity.Advance(PreviewInterval);
        ViewModel.ScrollSensitivity.Advance(PreviewInterval);
    }

    /// <summary>
    /// The scripted preview sweep stands in for live stick input, so it only runs while the
    /// engine is actually mapping a connected controller. Stopping the engine or losing the
    /// controller drives both panels to rest instead of letting them sweep until the window
    /// closes; reconnecting and restarting resumes them.
    /// </summary>
    internal void UpdatePreviewLiveness()
    {
        var live = ViewModel.Host.IsRunning && ViewModel.HasController;

        ViewModel.MouseSensitivity.IsLive = live;
        ViewModel.ScrollSensitivity.IsLive = live;
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
