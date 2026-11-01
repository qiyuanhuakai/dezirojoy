using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.App.Views.Controls;
using System.ComponentModel;

namespace DeziroJoy.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(16);

    // A busy UI thread delays the tick; the sweep follows real elapsed time so it stays in
    // sync, but a multi-second stall (modal dialog, blocking I/O) is clamped to one generous
    // frame so the dot does not teleport across the pad when the thread finally frees up.
    private static readonly TimeSpan MaxPreviewDelta = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _previewTimer = new() { Interval = PreviewInterval };

    private readonly DispatcherTimer _syncTimer = new() { Interval = SyncInterval };

    private readonly System.Diagnostics.Stopwatch _previewClock = new();

    private TimeSpan _lastPreviewTime;

    public MainWindow()
        : this(CreateRuntime())
    {
    }

    public MainWindow((MainWindowViewModel ViewModel, EngineHost Host) runtime)
        : this(runtime.ViewModel, runtime.Host)
    {
    }

    public MainWindow(MainWindowViewModel viewModel, EngineHost? host = null, ThemeService? themes = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        LoadWindowIcon();
        ConfigureResizeGrips();

        // One ThemeService is shared by the sidebar appearance panel and the title-bar
        // crescent, so the two entry points always agree on the current variant.
        var themeService = themes ?? new ThemeService();
        viewModel.Appearance.Attach(themeService);
        InstallThemeToggle(themeService);
        Wire(host ?? viewModel.Host);
    }

    /// <summary>
    /// Drops the theme toggle into the caption button row, left of minimize. The
    /// title bar is where the app's chrome preferences live and it has free space
    /// between the app name and the caption buttons, so the toggle costs no room in
    /// the already dense left sidebar.
    /// </summary>
    private void InstallThemeToggle(ThemeService themes)
    {
        var minimize = this.FindControl<Button>("MinimizeButton");

        if (minimize?.Parent is not Panel captionButtons)
        {
            return;
        }

        captionButtons.Children.Insert(captionButtons.Children.IndexOf(minimize), new ThemeToggleButton(themes));
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
        _previewClock.Start();
        _lastPreviewTime = _previewClock.Elapsed;
        _previewTimer.Start();
        _syncTimer.Start();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        host.Sampled += (_, args) => Dispatcher.UIThread.Post(() => ViewModel.ApplySample(args));

        // The engine-stopped counterpart: the host polls without ticking or injecting, so
        // highlighting and pad-driven navigation keep working while the mapping loop is off.
        host.PresenceSampled += (_, args) => Dispatcher.UIThread.Post(() => ViewModel.ApplyPresenceSample(args));

        if (this.FindControl<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked += OnGamepadElementClicked;
        }

        // Esc/Enter are the confirm/cancel pair for the mapping settings. They are handled on
        // the window rather than on a button so they work no matter which element of the
        // settings surface has focus, and so a gamepad-only user never needs the mouse.
        AddHandler(KeyDownEvent, OnSettingsKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            _syncTimer.Stop();
            host.Stop();
        };
    }

    /// <summary>
    /// When the inline editor opens it appears below the model in the mapping tab, so it is
    /// scrolled into view. The mapping list now lives in the always-visible right column, so a
    /// row clicked while another tab is selected would open the editor in a hidden tab - the
    /// mapping tab is selected to bring it into view. The model stays pinned above the card, so
    /// the element being edited remains the visible spatial reference.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.IsEditing) || !ViewModel.IsEditing)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<Border>("EditorPanel") is { IsVisible: true } panel)
            {
                panel.BringIntoView();
            }

            if (this.FindControl<TabControl>("MainTabs") is { } tabs)
            {
                tabs.SelectedIndex = 0;
            }
        });
    }

    /// <summary>
    /// Opens the mapping settings for the element that was clicked on the model. The view model
    /// owns the flow so a physical press on the same element takes exactly this path.
    /// </summary>
    private void OnGamepadElementClicked(object? sender, GamepadElementClickedEventArgs e)
        => ViewModel.OpenEditorForSource(e.Source);

    private void OnSettingsKeyDown(object? sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (!viewModel.IsEditing)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                viewModel.CancelEditorCommand.Execute(null);
                break;
            case Key.Enter when !viewModel.Editor.IsCapturingKey:
                e.Handled = true;
                viewModel.CommitEditorCommand.Execute(null);
                break;
        }
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var now = _previewClock.Elapsed;
        var delta = now - _lastPreviewTime;
        _lastPreviewTime = now;

        if (delta < TimeSpan.Zero || delta > MaxPreviewDelta)
        {
            delta = PreviewInterval;
        }

        UpdatePreviewLiveness();

        ViewModel.MouseSensitivity.Advance(delta);
        ViewModel.ScrollSensitivity.Advance(delta);

        // With the mapping loop stopped nothing polls the pad, so live highlighting and
        // pad-driven navigation would freeze. Presence sampling is the stopped-state
        // counterpart of the engine's Sampled event: it polls without ticking or injecting.
        if (!ViewModel.Host.IsRunning)
        {
            ViewModel.Host.SamplePresence();
        }
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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && ShouldBeginMoveDrag(e.Source))
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>
    /// A press that lands on an interactive caption control must not start a window drag:
    /// BeginMoveDrag hands the pointer to the native move loop, which swallows the control's
    /// Click. The walk starts at the pressed element so a press on a glyph inside a button is
    /// attributed to that button; presses on the bare drag surface (title text, icon, the
    /// title-bar Border) still drag.
    /// </summary>
    internal static bool ShouldBeginMoveDrag(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is Button)
            {
                return false;
            }
        }

        return true;
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
