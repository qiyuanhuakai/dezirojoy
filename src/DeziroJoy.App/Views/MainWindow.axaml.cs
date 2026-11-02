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
using DeziroJoy.App.Views;
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

    private EngineHost? _host;

    /// <summary>
    /// Bumped when this window detaches. Host callbacks carry the generation they were posted
    /// under and drop themselves when it no longer matches, so a sample that was in flight
    /// when the window closed never reaches the destroyed visual tree.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Whether the platform currently considers this window active. Seeded from
    /// <see cref="Window.IsActive"/> when the window opens and kept in step by the
    /// Activated/Deactivated events, so a window shown without focus starts out of scope.
    /// </summary>
    private bool _windowActive;

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
    /// Drops the theme toggle into the caption button row, left of the settings button. The
    /// title bar is where the app's chrome preferences live and it has free space
    /// between the app name and the caption buttons, so the toggle costs no room in
    /// the already dense left sidebar.
    /// </summary>
    private void InstallThemeToggle(ThemeService themes)
    {
        var anchor = this.FindControl<Button>("SettingsButton") ?? this.FindControl<Button>("MinimizeButton");

        if (anchor?.Parent is not Panel captionButtons)
        {
            return;
        }

        captionButtons.Children.Insert(captionButtons.Children.IndexOf(anchor), new ThemeToggleButton(themes));
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
        _host = host;

        _previewTimer.Tick += OnPreviewTick;
        _syncTimer.Tick += OnSyncTick;
        _previewClock.Start();
        _lastPreviewTime = _previewClock.Elapsed;
        _previewTimer.Start();
        _syncTimer.Start();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        host.Sampled += OnHostSampled;
        host.PresenceSampled += OnHostPresenceSampled;

        if (this.FindControl<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked += OnGamepadElementClicked;
        }

        // Esc/Enter are the confirm/cancel pair for the mapping settings. They are handled on
        // the window rather than on a button so they work no matter which element of the
        // settings surface has focus, and so a gamepad-only user never needs the mouse.
        AddHandler(KeyDownEvent, OnSettingsKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        // Physical pad input is scoped to a visible, active mapping tab, and the window is
        // the only thing that knows about all three.
        if (this.FindControl<TabControl>("MainTabs") is { } tabs)
        {
            tabs.SelectionChanged += (_, _) => UpdatePhysicalInputScope();
        }

        Opened += (_, _) =>
        {
            _windowActive = IsActive;
            UpdatePhysicalInputScope();
        };

        Activated += (_, _) =>
        {
            _windowActive = true;
            UpdatePhysicalInputScope();
        };

        Deactivated += (_, _) =>
        {
            _windowActive = false;
            UpdatePhysicalInputScope();
        };

        Closed += OnClosed;
    }

    private void OnHostSampled(object? sender, ControllerSampledEventArgs args)
    {
        var generation = Volatile.Read(ref _generation);

        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            ViewModel.ApplySample(args);
        });
    }

    private void OnHostPresenceSampled(object? sender, ControllerSampledEventArgs args)
    {
        var generation = Volatile.Read(ref _generation);

        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            ViewModel.ApplyPresenceSample(args);
        });
    }

    /// <summary>
    /// Releases everything this window attached: both timers, both host sampling
    /// subscriptions, the view-model listener, the visualizer and the key handler. The
    /// runtime is deliberately NOT stopped here - the window is a presentation resource, and
    /// the engine (the Windows daemon or the Linux local one) keeps mapping with no window
    /// open. Shutdown belongs to the application lifetime and the tray's 退出 item.
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        // Invalidate anything already queued before detaching, so an in-flight sample cannot
        // reach the destroyed window.
        Interlocked.Increment(ref _generation);

        _previewTimer.Stop();
        _syncTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        _syncTimer.Tick -= OnSyncTick;

        if (_host is { } host)
        {
            host.Sampled -= OnHostSampled;
            host.PresenceSampled -= OnHostPresenceSampled;
            _host = null;
        }

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (this.FindControl<GamepadVisualizer>("GamepadVisualizer") is { } visualizer)
        {
            visualizer.ElementClicked -= OnGamepadElementClicked;
        }

        RemoveHandler(KeyDownEvent, OnSettingsKeyDown);

        // The window is gone: physical input is out of scope until a new window shows.
        ViewModel.SetPhysicalInputScope(false);
    }

    /// <summary>
    /// Physical pad input is in scope only while this window is visible and active and the
    /// mapping tab is selected. Recomputed on every signal that can change one of the three,
    /// so a hide, a deactivation or a tab leave takes effect immediately.
    /// </summary>
    private void UpdatePhysicalInputScope()
    {
        var mappingTabSelected = this.FindControl<TabControl>("MainTabs") is not { } tabs
            || tabs.SelectedIndex == 0;

        ViewModel.SetPhysicalInputScope(IsVisible && _windowActive && mappingTabSelected);
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

        var generation = Volatile.Read(ref _generation);

        Dispatcher.UIThread.Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

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
        var action = WindowShortcutRouter.Route(
            e.Key,
            e.KeyModifiers,
            viewModel.IsSettingsOpen,
            viewModel.Editor.IsCapturingKey);

        // The settings surface is the topmost keyboard layer and an armed key capture consumes
        // every key, so both are arbitrated before any consumed-event guard: the surface can
        // always be dismissed and the capture always sees its key.
        if (action is WindowShortcutAction.CloseSettings)
        {
            e.Handled = true;
            viewModel.CloseSettingsCommand.Execute(null);
            return;
        }

        if (viewModel.IsSettingsOpen || viewModel.Editor.IsCapturingKey)
        {
            return;
        }

        // A key an input control already consumed, or one typed into a text field or pressed
        // while a dropdown is open, belongs to that control: the global shortcuts must not
        // override it.
        if (e.Handled || ShouldYieldToInputSurface())
        {
            return;
        }

        switch (action)
        {
            case WindowShortcutAction.OpenSettings:
                e.Handled = true;
                viewModel.OpenSettingsCommand.Execute(null);
                return;
            case WindowShortcutAction.SelectMappingTab:
            case WindowShortcutAction.SelectMouseTab:
            case WindowShortcutAction.SelectScrollTab:
                e.Handled = true;
                this.FindControl<TabControl>("MainTabs")!.SelectedIndex = action switch
                {
                    WindowShortcutAction.SelectMappingTab => 0,
                    WindowShortcutAction.SelectMouseTab => 1,
                    _ => 2,
                };
                return;
        }

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
            case Key.Enter:
                e.Handled = true;
                viewModel.CommitEditorCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// True when the key belongs to an input surface rather than to the window: a text field
    /// holds focus, or a dropdown is open. The dropdown is scanned window-wide because its
    /// popup is a separate visual tree, so a walk up from the focused element cannot reach
    /// the ComboBox from inside the popup.
    /// </summary>
    private bool ShouldYieldToInputSurface()
    {
        if (this.GetVisualDescendants().OfType<ComboBox>().Any(combo => combo.IsDropDownOpen))
        {
            return true;
        }

        for (var current = FocusManager?.GetFocusedElement() as Visual;
            current is not null;
            current = current.GetVisualParent())
        {
            if (current is TextBox)
            {
                return true;
            }
        }

        return false;
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeGlyph();
        }
    }

    private void MaximizeClick(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (this.FindControl<Control>("MaximizeGlyph") is { } maximize)
        {
            maximize.IsVisible = !maximized;
        }

        if (this.FindControl<Control>("RestoreGlyph") is { } restore)
        {
            restore.IsVisible = maximized;
        }

        if (this.FindControl<Button>("MaximizeButton") is { } button)
        {
            ToolTip.SetTip(button, maximized ? "还原" : "最大化");
        }
    }

    internal void SetSidebarCollapsed(bool left, bool collapsed)
    {
        var shell = this.FindControl<Grid>("WorkspaceGrid")!;
        var column = shell.ColumnDefinitions[left ? 0 : 4];
        column.Width = new GridLength(collapsed ? 0 : left ? 230 : 300);
        this.FindControl<Button>(left ? "ShowLeftPanelButton" : "ShowRightPanelButton")!.IsVisible = collapsed;
    }

    private void SidebarDragCompleted(object? sender, VectorEventArgs e)
    {
        var left = ReferenceEquals(sender, this.FindControl<GridSplitter>("LeftSplitter"));
        var shell = this.FindControl<Grid>("WorkspaceGrid")!;
        var column = shell.ColumnDefinitions[left ? 0 : 4];

        if (column.ActualWidth < 64)
        {
            SetSidebarCollapsed(left, true);
        }
        else
        {
            this.FindControl<Button>(left ? "ShowLeftPanelButton" : "ShowRightPanelButton")!.IsVisible = false;
        }
    }

    private void ShowLeftPanelClick(object? sender, RoutedEventArgs e) => SetSidebarCollapsed(true, false);

    private void ShowRightPanelClick(object? sender, RoutedEventArgs e) => SetSidebarCollapsed(false, false);

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
