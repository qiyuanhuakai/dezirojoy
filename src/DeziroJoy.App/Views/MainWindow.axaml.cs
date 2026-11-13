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
using System.Buffers.Binary;
using System.ComponentModel;

namespace DeziroJoy.App;

public partial class MainWindow : Window
{
    /// <summary>The PNG signature every icon frame in a Vista-and-later .ico starts with.</summary>
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(16);

    // A busy UI thread delays the tick; the sweep follows real elapsed time so it stays in
    // sync, but a multi-second stall (modal dialog, blocking I/O) is clamped to one generous
    // frame so the dot does not teleport across the pad when the thread finally frees up.
    private static readonly TimeSpan MaxPreviewDelta = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan SyncInterval = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _previewTimer = new() { Interval = PreviewInterval };

    private readonly DispatcherTimer _syncTimer = new() { Interval = SyncInterval };

    /// <summary>
    /// The foreground rules' own poll. The engine ticks at 2 ms; the program in front changes on
    /// the user's timescale, so the rules are read at the watcher's own cadence and never from
    /// the mapping loop. The timer belongs to the window because the watcher it drives belongs to
    /// the view model, which outlives the window in daemon mode.
    /// <para>
    /// What the poll drives depends on who owns mapping: the engine host in local mode, and in
    /// daemon mode the display only - the resident daemon runs its own watcher over its own
    /// engine host, so this one keeps the panel current without applying anything. Closing the
    /// window to the tray therefore stops the display, never the rules.
    /// </para>
    /// </summary>
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = ForegroundWatcher.DefaultInterval };

    /// <summary>
    /// The tab the 前台规则 panel was hosted in, or null before the window is wired and after it
    /// is closed. Held as the tab rather than as the panel's view model so the window's disposal
    /// stays a question of layout: the view model it carries is released with the tab.
    /// </summary>
    private TabItem? _foregroundRulesTab;

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

    private bool _minimizeRequestInProgress;

    internal Func<Task<bool>>? MinimizeToTrayRequested { get; set; }

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

        // The X11 backend decodes the .ico through Skia, which reads only the first
        // frame - 16x16 here - and X11IconData then caps the _NET_WM_ICON payload at
        // 128px, so panels and taskbars would only ever be handed a 16px icon. Hand
        // the window the largest frame instead (256x256, capped to 128 by Avalonia);
        // the tray keeps its own path through the same resource. Windows keeps the
        // whole .ico, where CreateIconFromResourceEx picks the best frame per size.
        if (OperatingSystem.IsLinux() && LoadLargestIconFrame(iconUri) is { } frame)
        {
            Icon = new WindowIcon(frame);
        }
        else
        {
            using var stream = AssetLoader.Open(iconUri);
            Icon = new WindowIcon(stream);
        }

        var titleBarIcon = this.FindControl<Image>("TitleBarIcon")!;
        using (var stream = AssetLoader.Open(iconUri))
        {
            titleBarIcon.Source = new Bitmap(stream);
        }
    }

    /// <summary>
    /// Reads the largest PNG-compressed frame out of the <c>.ico</c> window icon
    /// resource as a bitmap. Returns null when the resource is not a multi-frame
    /// .ico this understands, and the caller keeps the whole-resource icon.
    /// </summary>
    private static Bitmap? LoadLargestIconFrame(Uri iconUri)
    {
        byte[] icon;
        using (var stream = AssetLoader.Open(iconUri))
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            icon = buffer.ToArray();
        }

        // Layout: a 6-byte header (reserved u16, type u16, count u16) followed by
        // `count` 16-byte directory entries - width u8, height u8, palette u8,
        // reserved u8, planes u16, bits-per-pixel u16, bytes-in-resource u32 LE,
        // image-offset u32 LE. A dimension byte of 0 encodes 256.
        const int headerSize = 6;
        const int entrySize = 16;

        if (icon.Length < headerSize)
        {
            return null;
        }

        var entries = BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4));
        var bestOffset = 0;
        var bestLength = 0;
        var bestPixels = -1;

        for (var i = 0; i < entries; i++)
        {
            var start = headerSize + (i * entrySize);
            if (start + entrySize > icon.Length)
            {
                return null;
            }

            var entry = icon.AsSpan(start, entrySize);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            var length = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(8, 4));
            var offset = BinaryPrimitives.ReadInt32LittleEndian(entry.Slice(12, 4));

            // length > 0 is already established, so icon.Length - length cannot
            // overflow and offset > icon.Length - length is the wrap-safe form of
            // offset + length > icon.Length.
            if (width <= 0 || height <= 0 || length <= 0 || offset < 0
                || offset > icon.Length - length)
            {
                continue;
            }

            // Only PNG-compressed entries decode on their own; a BMP entry would need
            // its DIB header stripped first, so it is skipped.
            if (length < PngMagic.Length
                || !icon.AsSpan(offset, PngMagic.Length).SequenceEqual(PngMagic))
            {
                continue;
            }

            var pixels = width * height;
            if (pixels > bestPixels)
            {
                bestPixels = pixels;
                bestOffset = offset;
                bestLength = length;
            }
        }

        if (bestPixels < 0)
        {
            return null;
        }

        using var frame = new MemoryStream(icon, bestOffset, bestLength, writable: false);
        return new Bitmap(frame);
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

        HostForegroundRules();

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

    /// <summary>
    /// Hosts the 前台规则 surface as a tab of its own and starts the poll that drives it. The
    /// panel is built here rather than in XAML because its view model needs the live watcher the
    /// window's view model owns, and because the tab's place is a decision about the window's
    /// layout rather than about the rules. Everything the panel shows reads the watcher through
    /// that view model - the active-rule explanation above all - so there is no second copy of
    /// the state to drift.
    /// </summary>
    private void HostForegroundRules()
    {
        var panel = new ForegroundRulesPanel { DataContext = new ForegroundRulesViewModel(ViewModel.Foreground) };
        var tab = new TabItem
        {
            Header = "前台规则",
            Content = panel,
        };

        if (this.FindControl<TabControl>("MainTabs") is { } tabs)
        {
            tabs.Items.Add(tab);
        }

        _foregroundRulesTab = tab;
        _foregroundTimer.Tick += OnForegroundTick;
        _foregroundTimer.Start();
    }

    private void OnForegroundTick(object? sender, EventArgs e) => ViewModel.Foreground.Refresh();

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
    /// subscriptions, the view-model listener, the visualizer and the key handler. Daemon
    /// shutdown is deliberately decided before this event by the render lifetime coordinator:
    /// a tray handover keeps it running, while a full exit has already stopped it.
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

        // The rules panel and its poll go with the window: a window built afterwards starts from
        // a clean slate, so a destroy/recreate cycle never stacks a second timer or a second
        // subscription to the same watcher. The watcher itself belongs to the view model.
        _foregroundTimer.Stop();
        _foregroundTimer.Tick -= OnForegroundTick;

        if (_foregroundRulesTab is { } tab)
        {
            _foregroundRulesTab = null;

            if (tab.Content is ForegroundRulesPanel { DataContext: IDisposable rules })
            {
                rules.Dispose();
            }

            if (tab.Parent is TabControl tabs)
            {
                tabs.Items.Remove(tab);
            }
        }

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
        var live = ViewModel.IsEngineRunning && ViewModel.HasController;

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
            if (WindowState == WindowState.Minimized
                && !_minimizeRequestInProgress
                && MinimizeToTrayRequested is not null)
            {
                RequestMinimize();
            }
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

    private void MinimizeClick(object? sender, RoutedEventArgs e) => RequestMinimize();

    internal async void RequestMinimize()
    {
        if (_minimizeRequestInProgress)
        {
            return;
        }

        _minimizeRequestInProgress = true;
        try
        {
            if (MinimizeToTrayRequested is null || !await MinimizeToTrayRequested())
            {
                WindowState = WindowState.Minimized;
            }
        }
        finally
        {
            _minimizeRequestInProgress = false;
        }
    }

    private void CloseClick(object? sender, RoutedEventArgs e)
        => Close();

    private static (MainWindowViewModel ViewModel, EngineHost Host) CreateRuntime()
    {
        var store = new FileProfileStore();
        var provider = PlatformFactory.TryCreateProvider(out var gamepadProvider)
            ? gamepadProvider
            : new NoGamepadProvider();
        var sinkCreated = PlatformFactory.TryCreateSink(out var outputSink, out var sinkError);
        var sink = sinkCreated
            ? outputSink
            : new NullOutputSink();
        var host = new EngineHost(provider, sink, store);

        // The design-time factory feeds the view model a settings service too, so the
        // 劫持手柄按键 preference behaves here exactly as it does in the running app.
        // The sink failure travels with it: a NullOutputSink degradation used to be
        // invisible here, which is how "mapping does nothing" reached users.
        return (
            new MainWindowViewModel(host, store, settings: new ThemeService(), sinkError: sinkCreated ? null : sinkError),
            host);
    }
}
