using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;
using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Windows.Gaming;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Integrity level of the process that actually calls SendInput for mappings started from
/// the main window. <see cref="Unknown"/> means the platform has no integrity-level concept
/// - Linux injects through uinput, which UIPI does not gate - not that the query failed.
/// </summary>
public enum InjectorIntegrityLevel
{
    Unknown,

    Medium,

    Elevated,
}

/// <summary>
/// Which process is injecting mapped input right now. Deliberately separate from
/// <see cref="InjectorDescription"/>, which names the process that WOULD inject and its
/// integrity level: this says who is actually injecting, which is what the resident
/// daemon made the GUI lie about.
/// </summary>
public enum MappingOwner
{
    /// <summary>Nothing is injecting: this window's engine is stopped.</summary>
    None,

    /// <summary>This window's engine holds the injection mutex.</summary>
    ThisWindow,

    /// <summary>
    /// Another process holds it - the resident service, or a second DeziroJoy instance.
    /// The only way to reach this state is a start attempt that was refused.
    /// </summary>
    AnotherProcess,

    ResidentDaemon,
}

/// <summary>Which control of the global preset strip a gamepad focus index names.</summary>
public enum PresetStripAction
{
    /// <summary>A preset card: activating it switches the whole app to that slot.</summary>
    Switch,

    /// <summary>A card's delete badge: activating it arms, then confirms, the delete.</summary>
    Delete,

    /// <summary>The add button: activating it creates and switches to a new preset.</summary>
    Add,
}

/// <summary>
/// Main window orchestration: connection state, profile editing, mapping table and
/// the sensitivity panels. Avalonia-free so the whole flow is unit-testable; the view
/// only marshals host events onto the UI thread and calls <see cref="ApplySample"/>.
/// <para>
/// The daemon's input samples are the one stream that arrives faster than the display
/// refreshes, so they do not follow that shape: the receive loop keeps the latest sample
/// per device and a 16 ms frame clock (<see cref="IUiFrameClock"/>) drains it once per UI
/// frame, while the sensitivity panels' real-input calibration - the one consumer that
/// needs the complete stream - is fed every sample on its own path.
/// </para>
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly EngineHost _host;

    private readonly DaemonController? _daemon;

    private readonly bool _daemonRequired;

    /// <summary>
    /// The supervised restart the reconnect button falls back to when a client reconnect
    /// cannot reach the daemon. Production wires it to the render process's supervisor;
    /// a test wires it to a recorder, which is what makes the button's recovery testable
    /// without a real daemon process.
    /// </summary>
    private readonly Func<Task<DaemonRecoveryReport>>? _recoverDaemon;

    /// <summary>True while a supervised restart is running, so a second click waits instead of doubling it.</summary>
    private int _recoveryRunning;

    private readonly ThemeService? _settings;

    private readonly SynchronizationContext? _uiContext;

    private readonly CancellationTokenSource _lifetime = new();

    private bool _mappingEnabledRequested = true;

    private bool _disposed;

    private readonly IProfileStore _profileStore;

    private readonly PresetStore _presets;

    private readonly IForegroundRuleStore _foregroundRuleStore;

    private readonly ISchemeImportRecoveryStore _schemeImportRecoveryStore;

    private readonly IElevationService _elevationService;

    private readonly IUpdateService _updateService;

    private readonly IGameBarPopupGuard _gameBarPopupGuard;

    private bool _guideButtonSupported;

    /// <summary>
    /// The platform answer the window was built with: XInput has no Guide bit, evdev can
    /// report BTN_MODE, and an explicitly injected value stands in for either. The running
    /// backend is additive on top of it - SDL3 reports Guide everywhere - so a snapshot
    /// naming anything else returns to this answer rather than to a recomputed OS one.
    /// </summary>
    private readonly bool _platformGuideAnswer;
    private readonly bool _ownsUpdateService;

    /// <summary>
    /// The startup auto-check, or null when the persisted toggle is off. Kept so a test can
    /// await the fire-and-forget instead of racing it.
    /// </summary>
    private readonly Task? _startupUpdateCheck;

    /// <summary>
    /// One-shot platform probe the status area consults when no controller is connected. The
    /// render process owns no devices and no sink - the daemon is the mapping authority - so
    /// this is how "connect a gamepad" gets replaced by the actual reason (unreadable nodes, no
    /// nodes at all, uinput unwritable). Tests inject a stub; production asks the kernel.
    /// </summary>
    private readonly Func<LinuxRuntimeHints> _runtimeHints;

    /// <summary>
    /// Why this process's own output sink could not be created, or null when it was. Only a
    /// GUI that tried and failed carries one - the daemon injects on its own behalf and a
    /// render process owns no sink by design - and a sink that failed stays failed for the
    /// session, which is why the status line keeps quoting it with a pad connected too.
    /// </summary>
    private readonly string? _sinkError;

    /// <summary>
    /// Per-axis deflection at which a stick counts as pushed for navigation. Half travel
    /// matches the engine's own digital threshold: a resting stick never reaches it and a
    /// deliberate push always does.
    /// </summary>
    private const float StickNavigationThreshold = 0.5f;

    private readonly Dictionary<string, GamepadState> _navigationBaselines = new(StringComparer.Ordinal);

    private string? _daemonInputDeviceId;

    /// <summary>
    /// The frame clock the coalesced daemon-sample dispatch rides, or null when none was wired -
    /// which is what every unit test drives, and what makes a sample apply inline exactly as
    /// the event always did. Production hands the window a 16 ms DispatcherTimer; the view
    /// model owns it and disposes it with itself.
    /// </summary>
    private readonly IUiFrameClock? _sampleFrameClock;

    /// <summary>
    /// Guards the coalesced sample state: the daemon's receive loop writes it, the frame
    /// clock's tick drains it. The clock itself is only ever started and stopped on the UI
    /// thread, so a drain never races a start or a stop.
    /// </summary>
    private readonly object _sampleGate = new();

    /// <summary>
    /// The latest sample per device waiting for the next frame, each tagged with the order it
    /// arrived in. One entry per device - a device that sampled ten times inside one frame
    /// costs one dispatch, and a second pad's sample never overwrites the first pad's - and
    /// the arrival order is what keeps "the device that sampled most recently" true across a
    /// flush, which is what the panel's device scope reads.
    /// </summary>
    private readonly Dictionary<string, (long Order, InputSampleMessage Sample)> _pendingSamples = new(StringComparer.Ordinal);

    /// <summary>Arrival counter for <see cref="_pendingSamples"/>; only ever moved under <see cref="_sampleGate"/>.</summary>
    private long _sampleOrder;

    /// <summary>
    /// What the daemon's profile body could not be read for, or null when it parsed whole.
    /// Kept so the notice survives the next accepted mutation, which clears the transfer
    /// message: the damaged body is still what the daemon is serving until the user commits
    /// a profile over it, so the reason part of the table is missing must not vanish.
    /// </summary>
    private string? _profileDamage;

    /// <summary>The device whose slots <see cref="PresetSlots"/> was last built for.</summary>
    private string? _presetSlotDeviceId;

    /// <summary>
    /// The slot whose delete is armed for a confirming second click, or null when none is.
    /// The two-step confirm is the anti-mistouch half of a destructive action: the first
    /// press arms, the second on the same slot deletes, and any other preset action disarms.
    /// </summary>
    private int? _presetDeleteArmedSlot;

    public MainWindowViewModel(
        EngineHost host,
        IProfileStore profileStore,
        IElevationService? elevationService = null,
        DaemonController? daemon = null,
        bool daemonRequired = false,
        ThemeService? settings = null,
        IUpdateService? updateService = null,
        Func<LinuxRuntimeHints>? runtimeHints = null,
        string? sinkError = null,
        bool? guideButtonSupported = null,
        IGameBarPopupGuard? gameBarPopupGuard = null,
        IForegroundRuleStore? foregroundRuleStore = null,
        ISchemeImportRecoveryStore? schemeImportRecoveryStore = null,
        Func<Task<bool>>? restartDaemon = null,
        Func<Task<DaemonRecoveryReport>>? recoverDaemon = null,
        IUiFrameClock? sampleFrameClock = null)
    {
        _host = host;
        _daemon = daemon;
        _daemonRequired = daemonRequired || daemon is not null;
        _recoverDaemon = recoverDaemon;
        _sampleFrameClock = sampleFrameClock;
        _settings = settings;
        _uiContext = SynchronizationContext.Current;
        _profileStore = profileStore;
        _presets = new PresetStore(_profileStore);
        _foregroundRuleStore = foregroundRuleStore ?? new InMemoryForegroundRuleStore();
        _schemeImportRecoveryStore = schemeImportRecoveryStore ?? new InMemorySchemeImportRecoveryStore();
        _elevationService = elevationService ?? new ElevationService();
        _updateService = updateService ?? new UpdateService();
        _ownsUpdateService = updateService is null;
        _runtimeHints = runtimeHints ?? LinuxRuntimeProbe.Probe;
        _sinkError = sinkError;
        _platformGuideAnswer = guideButtonSupported ?? PlatformFactory.GuideButtonSupported;
        _guideButtonSupported = _platformGuideAnswer;
        _gameBarPopupGuard = gameBarPopupGuard ?? new GameBarPopupGuard();
        try
        {
            RefreshGameBarState();
        }
        catch (Exception exception)
        {
            GameBarPopupError = $"无法读取 Windows Game Bar 设置：{exception.Message}";
        }

        Editor = new MappingEditorViewModel(_guideButtonSupported);
        MouseSensitivity = new MouseSensitivityViewModel();
        ScrollSensitivity = new ScrollSensitivityViewModel();
        GenericPanel = new GenericGamepadPanelViewModel
        {
            OpenControlRequested = OpenGenericControl,
            RequestStickModeChange = (item, mode) => SetStickDirectionMode(item.Stick!.Value, mode),
        };
        Editor.PropertyChanged += OnEditorPropertyChanged;
        Visualizer = new GamepadVisualizerViewModel();
        Appearance = new AppearanceViewModel();
        DaemonSettings = new DaemonSettingsViewModel(
            restartDaemon: daemon is not null ? restartDaemon : null);
        Update = new UpdateSettingsViewModel(_updateService, _settings);
        LayerHint = new LayerHintViewModel();

        // The floating screen keyboard types into the application that has focus, and the
        // daemon is the sole injection authority, so its two requests are wired here - to the
        // daemon when one is in play, to this window's own host when it is not - and never to
        // a sink of its own. A test that constructs this view model without a daemon gets the
        // local host, which is what makes the whole path testable.
        ScreenKeyboard = new ScreenKeyboardViewModel(
            text => InjectScreenKeyboardTextAsync(text),
            (key, modifiers) => InjectScreenKeyboardKeyAsync(key, modifiers));
        ScreenKeyboard.CloseRequested += () => SetScreenKeyboardOpen(false);

        // The hint starts dismissed when the persisted preference is off, so a user who turned
        // it off keeps it hidden from the first frame rather than seeing one flash of it.
        LayerHint.IsSuppressed = !(_settings?.LayerHintEnabled ?? true);

        // The foreground rules run on their own timer against their own target: the engine host
        // while this window owns mapping, nothing at all while the resident daemon does. The
        // daemon runs the same watcher over its own engine host for its whole life, so a window
        // that also drove would send every switch and pause twice; what it still owes the user
        // is the panel - the program in front from its own source, the lock and the decisions
        // mirrored from the daemon's snapshots. Neither path touches the injection mutex, so
        // the three pauses stay independent.
        IForegroundTarget foregroundTarget = _daemon is null
            ? new EngineHostForegroundTarget(_host, _presets)
            : new NullForegroundTarget();

        Foreground = new ForegroundWatcher(
            ForegroundSourceFactory.Create(), foregroundTarget, _foregroundRuleStore.Load());

        _schemeUndo = _schemeImportRecoveryStore.Load();
        CanUndoSchemeImport = _schemeUndo is not null;
        if (_schemeImportRecoveryStore.LastError is { } recoveryError)
        {
            SetTransferError(recoveryError);
        }

        // Fixed for the lifetime of the process: elevation is decided at launch.
        IsElevationSupported = _elevationService.IsSupported;
        IsElevated = _elevationService.IsElevated;

        if (_daemon is not null)
        {
            _daemon.SnapshotChanged += OnDaemonSnapshot;
            _daemon.InputSampled += OnDaemonInputSample;
            _daemon.Connected += OnDaemonConnected;
            _daemon.Disconnected += OnDaemonDisconnected;
            if (_daemon.Snapshot is { } snapshot)
            {
                OnDaemonSnapshot(snapshot);
            }

            foreach (var sample in _daemon.InputSamples)
            {
                OnDaemonInputSample(sample);
            }

            if (_sampleFrameClock is not null)
            {
                // The clock runs for as long as this connection can deliver samples. The
                // window is usually built before the daemon answers - production connects
                // right after construction - so starting here covers a controller that was
                // already connected when this window was built, and OnDaemonConnected covers
                // the one that answers later; both are idempotent.
                _sampleFrameClock.Tick += FlushCoalescedSamples;
                StartSampleClock();
            }
        }

        _host.PresetSwitchRequested += OnHostPresetSwitchRequested;
        _host.ScreenKeyboardToggleRequested += OnHostScreenKeyboardToggleRequested;

        if (_settings?.LegacyLeaderButton is { } leaderButton)
        {
            _legacyLeaderButton = leaderButton;
            _legacyLeaderMode = _settings.LegacyLeaderMode;
            _legacyLeaderPending = true;
        }

        ActivePresetSlot = _host.ActivePresetSlot;
        RefreshStatus();
        ApplyInputLease();

        _startupUpdateCheck = RunStartupUpdateCheck();
    }

    /// <summary>
    /// The update surface of the settings panel: running version, manual check, the persisted
    /// startup auto-check and auto-download toggles, the download progress and the one-click
    /// install with its restart. Shares the single <see cref="IUpdateService"/> for the
    /// lifetime of the window and drives its own update orchestrator, which this view model
    /// disposes with the window because the card built it.
    /// </summary>
    public UpdateSettingsViewModel Update { get; }

    /// <summary>
    /// Runs the release check once at startup when the persisted toggle is on. The check is
    /// fire-and-forget: it must never delay the window, its outcome only ever lands in the
    /// settings panel, and a failure (offline, timeout, API refusal) stays there rather than
    /// interrupting startup. When the auto-download toggle is on, a release the check finds
    /// downloads itself the same way - still off the startup path, because the card owns that
    /// step. The returned task is faulted rather than crashing the process, and it is exposed
    /// through <see cref="StartupUpdateCheck"/> so tests can await it.
    /// </summary>
    private Task? RunStartupUpdateCheck()
    {
        if (!Update.AutoCheckEnabled)
        {
            return null;
        }

        var check = Update.CheckForUpdatesAsync(_lifetime.Token);
        _ = check.ContinueWith(
            static failed => Trace.WriteLine(
                $"[DeziroJoy] 启动时更新检查未完成：{failed.Exception?.Flatten().InnerException?.Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return check;
    }

    /// <summary>The startup auto-check task, or null when the toggle is off. For tests.</summary>
    internal Task? StartupUpdateCheck => _startupUpdateCheck;

    public ObservableCollection<ControllerRuntime> Controllers { get; } = [];

    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];

    /// <summary>One card per physical input, containing all of its mappings on both layers.</summary>
    public ObservableCollection<MappingCardViewModel> MappingCards { get; } = [];

    /// <summary>
    /// 精准键, Leader 键 and 屏幕键盘键, one row each, configured or not. Precision and
    /// leader settings are shared across presets; all three are listed by kind above the
    /// mapping list, with every collision reported beside them. Rebuilt on every refresh, so
    /// an edit, a preset switch or a shared-key fan-out re-lists them immediately.
    /// </summary>
    public ObservableCollection<GlobalSpecialKeyItem> SpecialKeys { get; } = [];

    /// <summary>
    /// The preset slots of the device in scope, one bindable item per slot in ascending
    /// order. The selector iterates this, so the initial four and any slot the user adds
    /// appear here without the view knowing a fixed count.
    /// </summary>
    public ObservableCollection<PresetSlotItem> PresetSlots { get; } = [];

    public MappingEditorViewModel Editor { get; }

    public EngineHost Host => _host;

    public MouseSensitivityViewModel MouseSensitivity { get; }

    public ScrollSensitivityViewModel ScrollSensitivity { get; }

    /// <summary>
    /// The data-driven gamepad panel: one card per control the connected device declares, in
    /// the Core's five fixed zones. It is the default mapping surface for a device that
    /// declares its controls; the hand-drawn model stays available as the alternative, and is
    /// the surface for a device that declares nothing. The panel never owns the editor - a
    /// card click opens the mapping settings for that control's source exactly as a click on
    /// the model does - and the profile rewrites a stick's execution mode asks for run through
    /// <see cref="SetStickDirectionMode"/>.
    /// </summary>
    public GenericGamepadPanelViewModel GenericPanel { get; }

    public GamepadVisualizerViewModel Visualizer { get; }

    /// <summary>
    /// Which mapping surface the stage shows: the data-driven generic panel, or the
    /// hand-drawn model. A device that declares its controls starts on the panel; a device
    /// that declares none has no panel to start on. Once the user picks, the choice stands
    /// for the session - the model stays the alternative for every backend - and nothing
    /// persists it, because it is a view of this session's device rather than a preference
    /// about the app.
    /// </summary>
    public bool IsGenericPanelMode
    {
        get => _isGenericPanelMode;
        private set
        {
            if (_isGenericPanelMode == value)
            {
                return;
            }

            _isGenericPanelMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SurfaceToggleLabel));
        }
    }

    /// <summary>Whether the connected device gives the panel anything to show at all.</summary>
    public bool CanUseGenericPanel => GenericPanel.HasItems;

    /// <summary>What the surface switch says, and what pressing it will do.</summary>
    public string SurfaceToggleLabel => IsGenericPanelMode ? "手柄模型" : "通用面板";

    /// <summary>Switches the stage between the generic panel and the hand-drawn model.</summary>
    [RelayCommand]
    private void ToggleMappingSurface()
    {
        if (!CanUseGenericPanel)
        {
            return;
        }

        _surfaceChosenByUser = true;
        IsGenericPanelMode = !IsGenericPanelMode;
    }

    public AppearanceViewModel Appearance { get; }

    public DaemonSettingsViewModel DaemonSettings { get; }

    /// <summary>
    /// The foreground rules watcher: the live rule table, the program in front and the decision
    /// that is in effect, driven by the window's own poll timer. Built here rather than by the
    /// rules panel because the target it drives depends on who owns mapping - the engine host
    /// locally, nothing in daemon mode, where the resident daemon runs its own watcher and this
    /// one mirrors the lock and decisions from the daemon's snapshots - which is this view
    /// model's knowledge. The panel it feeds is created by the view, so a window that never
    /// opens never builds it.
    /// </summary>
    public ForegroundWatcher Foreground { get; }

    /// <summary>
    /// What the layer hint overlay shows while a temporary layer is active: the layer's own
    /// mappings as readable action names. Rebuilt from every daemon snapshot (and every
    /// profile change) against the active layer the snapshot reports, so the overlay follows
    /// the layer the device is really running and hides the moment it is released.
    /// </summary>
    public LayerHintViewModel LayerHint { get; }

    /// <summary>
    /// The floating on-screen keyboard a 屏幕键盘 special key summons: an independent window
    /// that types into the application that has focus, rather than a card inside the mapping
    /// editor. Its open flag is the daemon's in daemon mode - the engine's toggle reaches the
    /// daemon, which publishes it, and this mirrors it - and this window's own host's
    /// otherwise, so the flag has exactly one owner in either mode. While it is open the input
    /// lease is taken and the pad drives the keyboard instead of the mapping.
    /// </summary>
    public ScreenKeyboardViewModel ScreenKeyboard { get; }

    public bool IsDaemonMode => _daemonRequired;

    public bool IsDaemonConnected => _daemon?.State == DaemonConnectionState.Connected;

    public string DaemonStatusText => !_daemonRequired
        ? "本地映射引擎"
        : DaemonSnapshot is null
            ? "守护进程不可用；未启用本地注入回退"
            : "守护进程已连接";

    [ObservableProperty]
    private StateSnapshot? _daemonSnapshot;

    private long _profileBaselineRevision = -1;
    private string? _profileDaemonInstanceId;
    private string? _profileBaselineDeviceId;

    // Which mapping surface the stage shows, the editor's button-capture lifecycle, and
    // the device the generic panel was last built for.
    private bool _isGenericPanelMode;
    private bool _surfaceChosenByUser;
    private InputBinding? _genericCaptureTarget;
    private DateTime _captureDeadline;
    private string? _genericPanelDeviceId;

    [ObservableProperty]
    private ControllerRuntime? _activeController;

    [ObservableProperty]
    private GamepadProfile? _activeProfile;

    [ObservableProperty]
    private MappingRowViewModel? _selectedMapping;

    /// <summary>
    /// First-level selection. Selecting deliberately does NOT open the editor. Injection is
    /// controlled by the explicit hijack preference and input scope, not by editor visibility.
    /// </summary>
    [ObservableProperty]
    private InputBinding? _selectedSource;

    [ObservableProperty]
    private string _statusMessage = "未检测到手柄，请连接手柄";

    [ObservableProperty]
    private bool _isMappingEnabled = true;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _hasController;

    [ObservableProperty]
    private bool _isEngineRunning;

    /// <summary>
    /// True while the settings surface is open. The surface is an overlay over the live
    /// view models rather than a second copy of them, so this flag is pure visibility.
    /// </summary>
    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>
    /// True while physical pad navigation and capture are in scope: the window is visible and
    /// active and the mapping tab is selected. The view owns that knowledge and feeds it here
    /// through <see cref="SetPhysicalInputScope"/>; the default is true so a view model driven
    /// without a window keeps its established behaviour.
    /// </summary>
    [ObservableProperty]
    private bool _isPhysicalInputInScope = true;

    /// <summary>
    /// Called by the view when the window's visibility, activation or selected tab changes.
    /// Leaving scope stops capture, forgets held edges and releases this connection's explicit
    /// hijack lease; reentry resets the edges and restores the lease only when hijack is enabled.
    /// </summary>
    public void SetPhysicalInputScope(bool inScope) => IsPhysicalInputInScope = inScope;

    /// <summary>
    /// Whether gamepad presses may drive the UI at all - the temporary 手柄劫持（临时） switch on
    /// the mapping surface. It is deliberately session-only, not a persisted preference: it
    /// governs the hijack lease for the life of the window and resets to off every launch, so a
    /// mapping session starts with presses following the active mapping until the user opts in.
    /// <para>
    /// While it is on, a new button press jumps straight to that element's mapping configuration
    /// - select and open in one step - and off returns every press to the active mapping at once.
    /// </para>
    /// </summary>
    public bool GamepadNavigationEnabled
    {
        get => _gamepadHijackEnabled;
        set => SetGamepadNavigationEnabled(value);
    }

    /// <summary>
    /// Turns the temporary gamepad hijack on or off for this session. Off hands every physical
    /// press back to the active mapping - the injector is no longer suspended for the UI - and
    /// ends any armed capture. Nothing is written to the persisted settings.
    /// </summary>
    public void SetGamepadNavigationEnabled(bool enabled)
    {
        if (_gamepadHijackEnabled == enabled)
        {
            return;
        }

        _gamepadHijackEnabled = enabled;

        if (!enabled)
        {
            Editor.EndKeyboardCapture();
            Editor.IsCapturingButton = false;
        }

        OnPropertyChanged(nameof(GamepadNavigationEnabled));
        ResetNavigationBaseline();
        ApplyInputLease();
    }

    private bool _gamepadHijackEnabled;

    /// <summary>
    /// Whether the temporary layer hint overlay is shown. A persisted preference the settings
    /// surface toggles; turning it off dismisses the hint even while a leader is held, which is
    /// the manual close - the overlay is click-through, so the close lives here rather than on
    /// the card. The render window and the tray read the same preference, so a dismissal made
    /// in the window carries into tray-only mode.
    /// </summary>
    public bool ShowLayerHint
    {
        get => _settings?.LayerHintEnabled ?? true;
        set
        {
            if (ShowLayerHint == value)
            {
                return;
            }

            _settings?.SetLayerHintEnabled(value);
            LayerHint.IsSuppressed = !value;
            OnPropertyChanged(nameof(ShowLayerHint));
        }
    }

    /// <summary>The overlay's placement mode, read from the persisted preferences.</summary>
    public LayerHintPositionMode OverlayPositionMode => _settings?.OverlayPositionMode ?? LayerHintPositionMode.Fixed;

    /// <summary>The screen edge or corner the fixed overlay parks at, from the persisted preferences.</summary>
    public LayerHintAnchor OverlayAnchor => _settings?.OverlayAnchor ?? LayerHintAnchor.BottomCenter;

    /// <summary>
    /// True while the overlay follows the pointer. The two placement radios in settings bind
    /// here - one directly, one through the negation converter - so a single setter drives the
    /// persisted mode, exactly the way the appearance radios share one flag.
    /// </summary>
    public bool IsOverlayFollowMouse
    {
        get => OverlayPositionMode == LayerHintPositionMode.FollowMouse;
        set
        {
            if (value == IsOverlayFollowMouse)
            {
                return;
            }

            _settings?.SetOverlayPositionMode(
                value ? LayerHintPositionMode.FollowMouse : LayerHintPositionMode.Fixed);
            OnPropertyChanged(nameof(IsOverlayFollowMouse));
        }
    }

    /// <summary>
    /// The fixed-anchor picker's selected index, mapped one-to-one onto
    /// <see cref="LayerHintAnchor"/>. The picker lists the anchors in that same order, so the
    /// index is the anchor; the overlay resolves the anchor from the persisted preference, so
    /// only the write needs to cross this seam.
    /// </summary>
    public int OverlayAnchorIndex
    {
        get => (int)OverlayAnchor;
        set => _settings?.SetOverlayAnchor((LayerHintAnchor)value);
    }

    /// <summary>Whether closing the render window hands the application to the tray.</summary>
    public bool CloseToTrayEnabled
    {
        get => ReadTrayPreference(nameof(CloseToTrayEnabled));
        set => SetCloseToTrayEnabled(value);
    }

    /// <summary>Whether minimizing the render window hands the application to the tray.</summary>
    public bool MinimizeToTrayEnabled
    {
        get => ReadTrayPreference(nameof(MinimizeToTrayEnabled));
        set => SetMinimizeToTrayEnabled(value);
    }

    public bool CloseToTray
    {
        get => CloseToTrayEnabled;
        set => SetCloseToTrayEnabled(value);
    }

    public bool MinimizeToTray
    {
        get => MinimizeToTrayEnabled;
        set => SetMinimizeToTrayEnabled(value);
    }

    public void SetCloseToTrayEnabled(bool enabled) => WriteTrayPreference(nameof(CloseToTrayEnabled), enabled);

    public void SetMinimizeToTrayEnabled(bool enabled) => WriteTrayPreference(nameof(MinimizeToTrayEnabled), enabled);

    private bool _closeToTrayEnabled = true;

    private bool _minimizeToTrayEnabled = true;

    private bool ReadTrayPreference(string name)
    {
        if (_settings?.GetType().GetProperty(name)?.GetValue(_settings) is bool value)
        {
            return value;
        }

        return name == nameof(CloseToTrayEnabled) ? _closeToTrayEnabled : _minimizeToTrayEnabled;
    }

    private void WriteTrayPreference(string name, bool enabled)
    {
        if (ReadTrayPreference(name) == enabled)
        {
            return;
        }

        var forwarded = false;
        if (_settings is not null)
        {
            var settingsType = _settings.GetType();
            var setter = settingsType.GetMethod($"Set{name}");
            if (setter?.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(bool))
            {
                setter.Invoke(_settings, [enabled]);
                forwarded = true;
            }
            else if (settingsType.GetProperty(name) is { CanWrite: true } property)
            {
                property.SetValue(_settings, enabled);
                forwarded = true;
            }
        }

        if (!forwarded)
        {
            if (name == nameof(CloseToTrayEnabled))
            {
                _closeToTrayEnabled = enabled;
            }
            else
            {
                _minimizeToTrayEnabled = enabled;
            }
        }

        OnPropertyChanged(name);
        OnPropertyChanged(name == nameof(CloseToTrayEnabled) ? nameof(CloseToTray) : nameof(MinimizeToTray));
    }

    /// <summary>
    /// The retired leader pair, captured at construction so the one-shot migration can run
    /// against the device that activates. The decision is taken once per stored legacy
    /// entry: the settings are cleared as the migration lands, so a view model constructed
    /// again finds nothing to migrate.
    /// </summary>
    private GamepadButton? _legacyLeaderButton;

    private LeaderMode _legacyLeaderMode;

    private bool _legacyLeaderPending;

    /// <summary>
    /// Converts the retired leader pair into the preset slots: the alternate (or a clone of
    /// the base profile) becomes slot 2 carrying one leader mapping that flips between
    /// slots 1 and 2. The store keys name a device, so the migration waits for the first
    /// controller to activate rather than running against no device at construction.
    /// </summary>
    private void MigrateLegacyLeader(string deviceId)
    {
        if (!_legacyLeaderPending)
        {
            return;
        }

        _legacyLeaderPending = false;

        var alternate = _profileStore.Load($"leader-alternate:{deviceId}");

        _presets.MigrateLegacyLeader(deviceId, _legacyLeaderButton, _legacyLeaderMode, alternate);
        _settings?.ClearLegacyLeader();

        // The convergence can include the slot this device is running - the shared leader now
        // lives in every slot - so the runtime is reloaded from the store: the engine runs the
        // migrated shape, and the profile the next edit commits from is no longer the
        // pre-migration one, which would fan the shared keys back out of every slot.
        if (_daemon is null
            && ActiveController is { } controller
            && controller.Descriptor.DeviceId == deviceId
            && _presets.LoadSlot(deviceId, controller.Profile.PresetSlot) is { } migrated)
        {
            ActiveProfile = migrated;
            _host.Reload(deviceId, migrated with { PresetSlot = controller.Profile.PresetSlot });
            ReloadSensitivityPanels();
            RefreshMappings();
        }
    }

    /// <summary>Which preset slot (1 and up) is active for the device in scope.</summary>
    [ObservableProperty]
    private int _activePresetSlot = 1;

    /// <summary>
    /// The local engine asked to switch preset slot. In daemon mode the daemon owns the
    /// switch and answers through its snapshots, so the presentation host's request is
    /// ignored; locally the slot is applied right here, exactly like the command does.
    /// </summary>
    private void OnHostPresetSwitchRequested(object? sender, int slot)
    {
        if (_daemon is null)
        {
            ApplyPresetSlotLocally(slot);
        }
    }

    /// <summary>
    /// Switches the active preset slot. In daemon mode the daemon applies it and answers
    /// through its snapshot; locally the slot's stored profile is loaded and reloaded into
    /// the running engine, the same path the daemon takes.
    /// </summary>
    [RelayCommand]
    private async Task SwitchPresetSlot(int slot)
    {
        // Switching is a deliberate move to another slot, so any half-finished delete confirm
        // is dropped rather than left armed behind the user's back.
        DisarmPresetDelete();

        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；配置槽位未切换。");
            return;
        }

        if (_daemon is not null)
        {
            try
            {
                // The server ignores DeviceId by contract - it keeps one app-wide slot scalar - so the empty string is deliberate, not a bug.
                var result = await _daemon.SwitchPresetSlotAsync(
                    ActiveController?.Descriptor.DeviceId ?? string.Empty, slot, _lifetime.Token);
                ReportMutationFailure(result, "切换配置槽位");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
            }

            return;
        }

        ApplyPresetSlotLocally(slot);
    }

    private void ApplyPresetSlotLocally(int slot)
    {
        if (slot < PresetStore.MinSlot)
        {
            return;
        }

        var profiles = new Dictionary<string, GamepadProfile>(StringComparer.Ordinal);
        foreach (var connected in _host.Controllers)
        {
            if (_presets.LoadSlot(connected.Descriptor.DeviceId, slot) is { } profile)
            {
                profiles.Add(connected.Descriptor.DeviceId, profile);
            }
        }

        if (profiles.Count == 0 && _host.Controllers.Count > 0)
        {
            SetTransferError("目标预设没有可用配置，未切换。");
            return;
        }

        try
        {
            _host.SwitchPresetSlot(slot, profiles);
        }
        catch (Exception exception)
        {
            SetTransferError($"切换配置槽位失败，已保留原槽位：{exception.Message}");
            return;
        }

        if (ActiveController is { } controller
            && _presets.LoadSlot(controller.Descriptor.DeviceId, slot) is { } activeProfile)
        {
            ActiveProfile = activeProfile;
        }

        ActivePresetSlot = slot;
        MarkActivePresetSlot(slot);
        ReloadSensitivityPanels();
        RefreshMappings();
    }

    /// <summary>
    /// Adds a new preset slot and switches to it. The new slot continues the device's slot
    /// sequence and starts as an independent copy of the profile in view (falling back to
    /// the primary slot), so it can be configured on its own, switched to immediately, and
    /// found again after a restart. Locally the copy is written straight to the store; in
    /// daemon mode the shared store already carries it by the time the switch is sent, so
    /// the daemon reloads the new slot exactly as it does for a click on an existing one.
    /// </summary>
    [RelayCommand]
    private async Task AddPreset()
    {
        DisarmPresetDelete();

        var deviceId = _daemon is null
            ? ActiveController?.Descriptor.DeviceId
            : PersistenceDeviceId;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            SetTransferError("新增预设失败：请先连接手柄。");
            return;
        }

        var nextSlot = _presets.SlotsInUse(deviceId).DefaultIfEmpty(PresetStore.MinSlot - 1).Max() + 1;
        var source = ActiveProfile ?? _presets.LoadSlot(deviceId, PresetStore.MinSlot);
        if (source is null)
        {
            SetTransferError("新增预设失败：没有可复制的配置。");
            return;
        }

        _presets.SaveSlot(deviceId, nextSlot, source with
        {
            Name = $"{source.Name} · 预设 {nextSlot}",
            DeviceId = PresetStore.KeyFor(deviceId, nextSlot),
        });

        ReconcilePresetSlots(deviceId);
        MarkActivePresetSlot(ActivePresetSlot);
        await SwitchPresetSlot(nextSlot);
    }

    /// <summary>
    /// Deletes one preset slot of the device in scope. The action is destructive, so it runs
    /// as a two-step confirm: the first press arms this slot's delete (the card shows the
    /// confirm state), the second press on the same slot performs it, and any other preset
    /// action disarms. In daemon mode the daemon owns the store and the app-wide slot, so the
    /// request is sent to it and the resulting snapshot reconciles this client; locally the
    /// slot is removed here. Deleting the active slot moves the whole app to a surviving slot
    /// first, so the running engine and every Leader reference never point at a slot that is
    /// gone, and the delete is rolled back if that switch fails. Slot 1 and the device's last
    /// remaining preset are refused.
    /// </summary>
    [RelayCommand]
    private async Task DeletePreset(int slot)
    {
        if (slot < PresetStore.MinSlot)
        {
            return;
        }

        if (_presetDeleteArmedSlot != slot)
        {
            _presetDeleteArmedSlot = slot;
            MarkPresetDeleteArmed();
            return;
        }

        DisarmPresetDelete();

        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；预设未删除。");
            return;
        }

        if (_daemon is not null)
        {
            if (PersistenceDeviceId is not { } deviceId)
            {
                SetTransferError("删除预设失败：请先连接手柄。");
                return;
            }

            try
            {
                var result = await _daemon.DeletePresetSlotAsync(deviceId, slot, _lifetime.Token);
                ReportMutationFailure(result, "删除预设");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
            }

            return;
        }

        PerformPresetDelete(slot);
    }

    /// <summary>
    /// The local-engine counterpart of the daemon's delete, run as one safe transaction: the
    /// store is repaired and the slot removed, then the affected runtime is reloaded with the
    /// repaired profile and the in-view profile follows it - so the running engine never keeps a
    /// stale leader aimed at the deleted slot. Deleting the active slot switches the whole app to
    /// a surviving slot; a reload failure restores the store from the rollback snapshot and leaves
    /// the slot intact.
    /// </summary>
    private void PerformPresetDelete(int slot)
    {
        if (ActiveController?.Descriptor.DeviceId is not { } deviceId)
        {
            SetTransferError("删除预设失败：请先连接手柄。");
            return;
        }

        if (slot == PresetStore.MinSlot)
        {
            SetTransferError("不能删除设备的主预设（槽位 1）。");
            return;
        }

        var inUse = _presets.SlotsInUse(deviceId);

        // Capture a rollback snapshot so a failed reload restores the store exactly.
        var rollback = new Dictionary<int, GamepadProfile>();
        foreach (var existing in inUse)
        {
            if (_presets.LoadSlot(deviceId, existing) is { } profile)
            {
                rollback[existing] = profile;
            }
        }

        // Reconcile against the slot the active controller's runtime is actually running - it can
        // differ from the app-wide scalar when this device lacks that slot - not only the global
        // scalar, so a device left running the deleted slot is moved rather than abandoned.
        var runtimeSlot = ActiveController.Profile.PresetSlot;
        var wasActive = slot == ActivePresetSlot;
        var deviceRunsDeleted = runtimeSlot == slot;

        // Repair + delete on the store first, so the profile reloaded below is already repaired.
        var outcome = _presets.DeleteSlot(deviceId, slot);
        if (outcome != PresetDeleteOutcome.Deleted)
        {
            SetTransferError(outcome switch
            {
                PresetDeleteOutcome.PrimarySlot => "不能删除设备的主预设（槽位 1）。",
                PresetDeleteOutcome.LastRemaining => "不能删除最后一套预设。",
                _ => "该槽位没有配置。",
            });
            return;
        }

        var surviving = inUse.Where(candidate => candidate != slot).ToList();
        var fallback = surviving.Where(candidate => candidate < slot).DefaultIfEmpty(PresetStore.MinSlot).Max();

        int deviceSlot;
        GamepadProfile? reloadProfile = null;
        var switchAll = false;

        if (wasActive)
        {
            // The app-wide active slot was deleted: move the whole app to a survivor.
            deviceSlot = fallback;
            switchAll = true;
        }
        else if (deviceRunsDeleted)
        {
            // This device was running the deleted slot (it lacked the app-wide active slot): move
            // just it to a survivor, leaving the app-wide slot where it is.
            deviceSlot = fallback;
            if (_presets.LoadSlot(deviceId, fallback) is { } survivor)
            {
                reloadProfile = survivor with { PresetSlot = fallback };
            }
        }
        else
        {
            // The device runs some other slot; reload only if its own profile was repaired.
            deviceSlot = runtimeSlot;
            if (rollback.TryGetValue(runtimeSlot, out var runtimeBefore)
                && _presets.LoadSlot(deviceId, runtimeSlot) is { } runtimeAfter
                && runtimeBefore != runtimeAfter)
            {
                reloadProfile = runtimeAfter with { PresetSlot = runtimeSlot };
            }
        }

        try
        {
            if (switchAll)
            {
                var profiles = new Dictionary<string, GamepadProfile>(StringComparer.Ordinal);
                foreach (var connected in _host.Controllers)
                {
                    if (_presets.LoadSlot(connected.Descriptor.DeviceId, fallback) is { } profile)
                    {
                        profiles.Add(connected.Descriptor.DeviceId, profile);
                    }
                }

                _host.SwitchPresetSlot(fallback, profiles);
                ActivePresetSlot = fallback;
            }
            else if (reloadProfile is not null && _host.Controllers.Any(c => c.Descriptor.DeviceId == deviceId))
            {
                _host.Reload(deviceId, reloadProfile);
            }
        }
        catch (Exception exception)
        {
            foreach (var (existing, profile) in rollback)
            {
                _presets.SaveSlot(deviceId, existing, profile);
            }

            SetTransferError($"删除预设失败，已保留原槽位：{exception.Message}");
            return;
        }

        ReconcilePresetSlots(deviceId);
        MarkActivePresetSlot(ActivePresetSlot);

        if (ActiveController is { } controller
            && _presets.LoadSlot(controller.Descriptor.DeviceId, deviceSlot) is { } activeProfile)
        {
            ActiveProfile = activeProfile;
        }

        ReloadSensitivityPanels();
        RefreshMappings();
    }

    /// <summary>
    /// Reconciles <see cref="PresetSlots"/> against the store for one device and keeps the
    /// leader slot options in step. The stored slot set is re-read on every call, so a slot
    /// another client added while this one runs is picked up on the next daemon snapshot; the
    /// items and the collection are rebuilt only when that set actually changed, so a stream
    /// of snapshots that add nothing costs one enumeration and no churn. With no device the
    /// collection is emptied and the leader options fall back to the initial four.
    /// </summary>
    private void ReconcilePresetSlots(string? deviceId)
    {
        IReadOnlyList<int> slots = deviceId is null ? [] : _presets.SlotsInUse(deviceId);

        if (deviceId == _presetSlotDeviceId
            && PresetSlots.Select(item => item.Slot).SequenceEqual(slots))
        {
            foreach (var item in PresetSlots)
            {
                item.Name = _presets.LoadSlot(deviceId!, item.Slot)?.Name ?? $"预设 {item.Slot}";
            }

            return;
        }

        _presetSlotDeviceId = deviceId;

        // The items are about to be replaced, so the armed-slot reference is dropped: a fresh
        // item is never born armed, and a stale slot number must not let the next click skip
        // the confirming step.
        _presetDeleteArmedSlot = null;
        PresetSlots.Clear();
        if (deviceId is not null)
        {
            foreach (var slot in slots)
            {
                var name = _presets.LoadSlot(deviceId, slot)?.Name ?? $"预设 {slot}";
                PresetSlots.Add(new PresetSlotItem(slot, name));
            }
        }

        UpdateLeaderSlotOptions(slots);

        // The strip's controls changed with the slot set, so keep the gamepad focus index inside
        // the new range rather than leaving it aimed at a control that no longer exists.
        if (PresetStripFocusIndex >= PresetStripFocusCount)
        {
            PresetStripFocusIndex = PresetStripFocusCount - 1;
        }
    }

    /// <summary>Moves the active flag onto the item for <paramref name="slot"/>, if present.</summary>
    private void MarkActivePresetSlot(int slot)
    {
        foreach (var item in PresetSlots)
        {
            item.IsActive = item.Slot == slot;
        }
    }

    /// <summary>Moves the delete-confirm flag onto the armed slot's item, clearing the rest.</summary>
    private void MarkPresetDeleteArmed()
    {
        foreach (var item in PresetSlots)
        {
            item.IsDeleteArmed = item.Slot == _presetDeleteArmedSlot;
        }
    }

    /// <summary>Drops any armed delete confirm, both the slot it named and the cards' flags.</summary>
    private void DisarmPresetDelete()
    {
        if (_presetDeleteArmedSlot is null)
        {
            return;
        }

        _presetDeleteArmedSlot = null;
        MarkPresetDeleteArmed();
    }

    /// <summary>
    /// The gamepad-focused control in the global preset strip, or -1 when none is. The view
    /// keeps real focus in step with this and reports focus changes back; while it is set the
    /// pad drives the strip (see <see cref="NavigateByPhysicalInput"/>) instead of the gamepad
    /// model, so the strip and its delete badges are reachable by gamepad, not only by Tab.
    /// </summary>
    [ObservableProperty]
    private int _presetStripFocusIndex = -1;

    /// <summary>How many controls the preset strip offers the gamepad, in focus order.</summary>
    public int PresetStripFocusCount
        => PresetSlots.Count + PresetSlots.Count(item => item.CanDelete) + 1;

    /// <summary>
    /// The strip control a focus index names, in the strip's focus order - each card, then its
    /// delete badge, then the add button - or null when the index is out of range. The view maps
    /// this to the matching control and back, so the order is defined in exactly one place.
    /// </summary>
    public (PresetStripAction Action, int Slot)? PresetStripActionAt(int index)
    {
        if (index < 0)
        {
            return null;
        }

        var position = 0;
        foreach (var item in PresetSlots)
        {
            if (index == position++)
            {
                return (PresetStripAction.Switch, item.Slot);
            }

            if (item.CanDelete && index == position++)
            {
                return (PresetStripAction.Delete, item.Slot);
            }
        }

        return index == position ? (PresetStripAction.Add, 0) : null;
    }

    /// <summary>Moves the gamepad focus along the strip by <paramref name="delta"/>, clamped to the strip.</summary>
    public void MovePresetStripFocus(int delta)
    {
        var count = PresetStripFocusCount;
        if (count <= 0)
        {
            return;
        }

        var current = PresetStripFocusIndex < 0 ? (delta < 0 ? count : -1) : PresetStripFocusIndex;
        PresetStripFocusIndex = Math.Clamp(current + delta, 0, count - 1);
    }

    /// <summary>Activates the gamepad-focused strip control: switch, delete (with its confirm) or add.</summary>
    private void ActivatePresetStripFocus()
    {
        if (PresetStripActionAt(PresetStripFocusIndex) is not { } action)
        {
            return;
        }

        switch (action.Action)
        {
            case PresetStripAction.Switch:
                _ = SwitchPresetSlot(action.Slot);
                break;
            case PresetStripAction.Delete:
                _ = DeletePreset(action.Slot);
                break;
            case PresetStripAction.Add:
                _ = AddPreset();
                break;
        }
    }

    /// <summary>
    /// Enters the preset strip by gamepad, parking the focus on the active card (or the first
    /// card when the device lacks the app-wide slot). This is the pad-only entry the Guide button
    /// drives, so the strip is reachable without any keyboard or pointer focus.
    /// </summary>
    private void EnterPresetStrip()
    {
        for (var index = 0; index < PresetStripFocusCount; index++)
        {
            if (PresetStripActionAt(index) is { Action: PresetStripAction.Switch, Slot: var slot }
                && slot == ActivePresetSlot)
            {
                PresetStripFocusIndex = index;
                return;
            }
        }

        PresetStripFocusIndex = 0;
    }

    /// <summary>Leaves the preset strip, returning the pad to the gamepad model.</summary>
    private void ExitPresetStrip() => PresetStripFocusIndex = -1;

    /// <summary>
    /// Points the mapping editor's leader slot pickers at the device's current slots, so a
    /// leader key can flip between a slot the user just added. Falls back to the initial four
    /// while no device is in scope, matching the editor's own default.
    /// </summary>
    private void UpdateLeaderSlotOptions(IReadOnlyList<int> slots)
    {
        Editor.SpecialSlotOptions = slots.Count > 0 ? slots : [1, 2, 3, 4];
    }

    /// <summary>
    /// Daemon-mode counterpart of activating a controller: reconcile the slot list against the
    /// store for the snapshot's device - another client may have added a slot since the last
    /// snapshot - and move the active flag to whatever slot the snapshot reports.
    /// </summary>
    private void SyncPresetSlotsForDaemon()
    {
        ReconcilePresetSlots(PersistenceDeviceId);
        MarkActivePresetSlot(ActivePresetSlot);
    }

    partial void OnIsPhysicalInputInScopeChanged(bool value)
    {
        if (!value)
        {
            Editor.EndKeyboardCapture();
            Editor.IsCapturingButton = false;
        }

        ResetNavigationBaseline();
        ApplyInputLease();
    }

    /// <summary>Forgets the navigation baseline so the next sample starts from a clean slate.</summary>
    private void ResetNavigationBaseline()
    {
        _navigationBaselines.Clear();
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    public bool ShowGuidePlatformNote => !_guideButtonSupported;

    public string GuidePlatformNote => _guideButtonSupported
        ? string.Empty
        : "Windows · XInput 无法读取 Guide 键：模型不会高亮，Guide 映射和界面导航都不会触发。请改用其他按键；守护进程切换到 SDL3 后端后即可绑定 Guide。";

    /// <summary>
    /// Whether the Guide button can be bound and fires. The running backend is additive on
    /// top of the platform answer: SDL3 reports the Guide button on every platform, so a
    /// daemon snapshot naming SDL3 makes it available on Windows too, while XInput has no
    /// bit for it and an evdev Linux keeps the answer it started with. Starts at the
    /// platform answer before the first snapshot.
    /// </summary>
    public bool GuideButtonSupported
    {
        get => _guideButtonSupported;
        private set
        {
            if (_guideButtonSupported == value)
            {
                return;
            }

            _guideButtonSupported = value;
            OnPropertyChanged(nameof(GuideButtonSupported));
            OnPropertyChanged(nameof(ShowGuidePlatformNote));
            OnPropertyChanged(nameof(GuidePlatformNote));
            Editor.UpdateGuideButtonSupport(value);
        }
    }

    /// <summary>
    /// Applies what the daemon's snapshot says about the running backend: SDL3 adds Guide
    /// everywhere, anything else - the legacy fallback, or an older daemon that never
    /// named its backend - returns to the platform answer this window was built with.
    /// </summary>
    private void ApplyRunningBackendGuideSupport(InputBackend? runningBackend)
        => GuideButtonSupported = runningBackend == InputBackend.Sdl3 || _platformGuideAnswer;

    public bool ShowGameBarPopupGuard => _gameBarPopupGuard.IsSupported;

    /// <summary>Layer one: the per-user preference that stops the controller opening Game Bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDisableGameBarControllerLaunch))]
    [NotifyPropertyChangedFor(nameof(GameBarPopupHint))]
    [NotifyPropertyChangedFor(nameof(GameBarPreferenceStatus))]
    private bool _isGameBarControllerLaunchDisabled;

    public bool CanDisableGameBarControllerLaunch => ShowGameBarPopupGuard && !IsGameBarControllerLaunchDisabled;

    /// <summary>Layer two: this app's no-op ms-gamebar handler is registered and current.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegisterGameBarPopupHandler))]
    [NotifyPropertyChangedFor(nameof(CanRestoreGameBarPopupHandler))]
    [NotifyPropertyChangedFor(nameof(IsGameBarPopupHandlerStale))]
    [NotifyPropertyChangedFor(nameof(RegisterGameBarPopupHandlerText))]
    [NotifyPropertyChangedFor(nameof(GameBarPopupHint))]
    [NotifyPropertyChangedFor(nameof(GameBarHandlerStatus))]
    private bool _isGameBarPopupHandlerRegistered;

    /// <summary>Layer two ownership, whatever the recorded path - what restore acts on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRestoreGameBarPopupHandler))]
    [NotifyPropertyChangedFor(nameof(IsGameBarPopupHandlerStale))]
    [NotifyPropertyChangedFor(nameof(RegisterGameBarPopupHandlerText))]
    [NotifyPropertyChangedFor(nameof(GameBarPopupHint))]
    [NotifyPropertyChangedFor(nameof(GameBarHandlerStatus))]
    private bool _ownsGameBarPopupHandler;

    /// <summary>
    /// The marker is still here but the command belongs to another program: it replaced
    /// this app's handler without touching the private marker. Neither restoring nor
    /// re-registering may act on it - both would destroy that program's registration.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegisterGameBarPopupHandler))]
    [NotifyPropertyChangedFor(nameof(CanRestoreGameBarPopupHandler))]
    [NotifyPropertyChangedFor(nameof(GameBarPopupHint))]
    [NotifyPropertyChangedFor(nameof(GameBarHandlerStatus))]
    private bool _isGameBarPopupHandlerTakenOver;

    public bool CanRegisterGameBarPopupHandler
        => ShowGameBarPopupGuard && !IsGameBarPopupHandlerRegistered && !IsGameBarPopupHandlerTakenOver;

    public string RegisterGameBarPopupHandlerText
        => IsGameBarPopupHandlerStale ? "重新注册静默处理程序" : "注册静默处理程序";

    public bool CanRestoreGameBarPopupHandler => ShowGameBarPopupGuard && OwnsGameBarPopupHandler;

    /// <summary>Registered by this app, but the recorded command no longer names this executable.</summary>
    public bool IsGameBarPopupHandlerStale => OwnsGameBarPopupHandler && !IsGameBarPopupHandlerRegistered;

    public string GameBarPreferenceStatus => IsGameBarControllerLaunchDisabled
        ? "已关闭：控制器不再经系统唤起 Game Bar。"
        : "未关闭：连接手柄或按 Guide 时系统仍可能尝试唤起 Game Bar。";

    public string GameBarHandlerStatus
    {
        get
        {
            if (IsGameBarPopupHandlerTakenOver)
            {
                return "已被其他程序接管：协议命令已不是本应用的，本应用的注册标记与备份原样未动；请检查系统设置，或安装/修复 Xbox Game Bar。";
            }

            if (IsGameBarPopupHandlerStale)
            {
                return "已注册但指向旧版程序路径：请重新注册，或恢复系统默认。";
            }

            return IsGameBarPopupHandlerRegistered
                ? "已为本用户注册静默处理程序：仅 ms-gamebar 链接由 DeziroJoy 静默处理；部分系统组件经其他上下文触发时仍可能弹窗，此时请用系统设置，或安装/修复 Xbox Game Bar。"
                : "未注册：系统找不到 ms-gamebar 处理程序时会弹出“选择应用”对话框。注册只覆盖 ms-gamebar，不保证抑制所有弹窗。";
        }
    }

    public string GameBarPopupHint
    {
        get
        {
            if (!ShowGameBarPopupGuard)
            {
                return string.Empty;
            }

            if (IsGameBarPopupHandlerTakenOver)
            {
                return "ms-gamebar 处理程序已被其他程序接管：为避免破坏对方的处理程序，不提供注册与恢复，本应用的注册标记和备份保持原样。请检查系统设置，或安装/修复 Xbox Game Bar。";
            }

            if (IsGameBarPopupHandlerStale)
            {
                return "本应用注册的 ms-gamebar 处理程序指向已不存在的程序路径（应用可能移动或更新过），请重新注册；也可恢复系统默认。";
            }

            if (IsGameBarPopupHandlerRegistered)
            {
                return IsGameBarControllerLaunchDisabled
                    ? "已为本用户注册静默处理程序：仅 ms-gamebar 链接由 DeziroJoy 静默处理（其他启动方式不主动拦截），控制器唤起偏好也已写入并核验。部分系统组件经其他上下文触发时仍可能弹窗，此时请用系统设置，或安装/修复 Xbox Game Bar。不修改系统录制设置，可随时恢复。"
                    : "已为本用户注册静默处理程序：仅 ms-gamebar 链接由 DeziroJoy 静默处理（其他启动方式不主动拦截），控制器唤起偏好仍未关闭。部分系统组件经其他上下文触发时仍可能弹窗，此时请用系统设置，或安装/修复 Xbox Game Bar。不修改系统录制设置，可随时恢复。";
            }

            if (IsGameBarControllerLaunchDisabled)
            {
                return "偏好已写入并核验。若仍弹出 ms-gamebar“选择应用”对话框，可注册本应用的静默处理程序（只覆盖 ms-gamebar，不保证抑制所有弹窗）；部分系统需要重新登录。";
            }

            return "出现 ms-gamebar 弹窗时：可写入当前用户的关闭偏好（“允许控制器打开 Game Bar”）；若仍弹窗，再注册本应用的 ms-gamebar 静默处理程序（只覆盖 ms-gamebar，不保证抑制所有弹窗；Game Bar 不再经该协议打开），仅当前用户、可随时恢复。不修改系统录制设置。";
        }
    }

    [ObservableProperty]
    private string? _gameBarPopupError;

    public bool HasGameBarPopupError => !string.IsNullOrEmpty(GameBarPopupError);

    private void RefreshGameBarState()
    {
        IsGameBarControllerLaunchDisabled = _gameBarPopupGuard.IsControllerLaunchDisabled;
        IsGameBarPopupHandlerRegistered = _gameBarPopupGuard.IsProtocolHandlerRegistered;
        OwnsGameBarPopupHandler = _gameBarPopupGuard.OwnsProtocolHandler;
        IsGameBarPopupHandlerTakenOver = _gameBarPopupGuard.IsProtocolHandlerTakenOver;
    }

    [RelayCommand]
    private void DisableGameBarControllerLaunch()
    {
        try
        {
            _gameBarPopupGuard.DisableControllerLaunch();
            RefreshGameBarState();
            GameBarPopupError = null;
        }
        catch (Exception exception)
        {
            GameBarPopupError = $"无法修改 Windows Game Bar 设置：{exception.Message}。也可在系统设置 → 游戏 → Xbox Game Bar 中手动关闭“允许控制器打开 Game Bar”。";
        }

        OnPropertyChanged(nameof(HasGameBarPopupError));
    }

    /// <summary>
    /// Layer two: install the per-user no-op handler for ms-gamebar. Deliberately separate
    /// from the preference - the preference asks the system not to launch Game Bar with the
    /// controller, the handler answers the link on the systems that launch it anyway.
    /// </summary>
    [RelayCommand]
    private void RegisterGameBarPopupHandler()
    {
        try
        {
            _gameBarPopupGuard.RegisterProtocolHandler();
            RefreshGameBarState();
            GameBarPopupError = null;
        }
        catch (Exception exception)
        {
            GameBarPopupError = $"无法注册 ms-gamebar 静默处理程序：{exception.Message}";
        }

        OnPropertyChanged(nameof(HasGameBarPopupError));
    }

    /// <summary>Layer two undo: hand the protocol back to whatever was registered before.</summary>
    [RelayCommand]
    private void RestoreGameBarPopupHandler()
    {
        try
        {
            _gameBarPopupGuard.RestoreProtocolHandler();
            RefreshGameBarState();
            GameBarPopupError = null;
        }
        catch (Exception exception)
        {
            GameBarPopupError = $"无法恢复 ms-gamebar 系统默认处理程序：{exception.Message}";
        }

        OnPropertyChanged(nameof(HasGameBarPopupError));
    }

    [RelayCommand]
    private void OpenGameBarSettings()
    {
        try
        {
            _gameBarPopupGuard.OpenSystemSettings();
            GameBarPopupError = null;
        }
        catch (Exception exception)
        {
            GameBarPopupError = $"无法打开 Windows 游戏设置：{exception.Message}。请手动打开系统设置 → 游戏 → Xbox Game Bar。";
        }

        OnPropertyChanged(nameof(HasGameBarPopupError));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InjectorDescription))]
    [NotifyPropertyChangedFor(nameof(InjectorIntegrity))]
    [NotifyPropertyChangedFor(nameof(InjectorStatusText))]
    [NotifyPropertyChangedFor(nameof(CanRelaunchElevated))]
    private bool _isElevationSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InjectorDescription))]
    [NotifyPropertyChangedFor(nameof(InjectorIntegrity))]
    [NotifyPropertyChangedFor(nameof(InjectorStatusText))]
    [NotifyPropertyChangedFor(nameof(CanRelaunchElevated))]
    private bool _isElevated;

    /// <summary>
    /// True when a relaunch would actually change anything: the platform cares about
    /// integrity levels and this process is not elevated yet.
    /// </summary>
    public bool CanRelaunchElevated => !_daemonRequired && IsElevationSupported && !IsElevated;

    /// <summary>
    /// Command-line flag an elevated relaunch carries when mapping is running, so the
    /// elevated copy can resume the user's intent. The startup path has to act on this
    /// flag; until it does, the relaunch transmits the request but the elevated copy
    /// still starts with mapping off.
    /// </summary>
    public const string ResumeMappingArgument = "--resume-mapping";

    /// <summary>
    /// Which process actually injects mapped input, named together with its integrity
    /// level. Daemon mode reads the daemon's authoritative snapshot; otherwise the local
    /// <see cref="EngineHost"/> owned through <see cref="EngineComposition"/> injects.
    /// </summary>
    public string InjectorDescription => _daemonRequired
        ? DaemonSnapshot is { } daemon
            ? IsElevationSupported
                ? $"提权守护进程（PID {daemon.InjectorPid}）"
                : $"守护进程（PID {daemon.InjectorPid}）"
            : IsElevationSupported
                ? "提权守护进程（不可用）"
                : "守护进程（不可用）"
        : IsElevationSupported
            ? IsElevated
                ? "DeziroJoy 主窗口（已提权）"
                : "DeziroJoy 主窗口（未提权）"
            : "DeziroJoy 主窗口";

    /// <summary>
    /// Integrity level of that injector: what this process's Windows token actually holds,
    /// or <see cref="InjectorIntegrityLevel.Unknown"/> where integrity levels do not apply.
    /// </summary>
    public InjectorIntegrityLevel InjectorIntegrity => _daemonRequired
        ? DaemonSnapshot?.InjectorIntegrity switch
        {
            "high" => InjectorIntegrityLevel.Elevated,
            "medium" => InjectorIntegrityLevel.Medium,
            _ => InjectorIntegrityLevel.Unknown,
        }
        : IsElevationSupported
            ? IsElevated
                ? InjectorIntegrityLevel.Elevated
                : InjectorIntegrityLevel.Medium
            : InjectorIntegrityLevel.Unknown;

    /// <summary>
    /// What the injector's integrity level means for reach, stated plainly. SendInput is
    /// subject to UIPI and fails silently when blocked, so this is the only place the user
    /// can tell "UIPI dropped my input" from "it worked" - and the only hint that the
    /// relaunch is the fix. The daemon branch splits by platform: on the same-user platform
    /// there is no integrity level to earn and no UIPI to be blocked by, so quoting either
    /// would describe a mechanism that platform does not have.
    /// </summary>
    public string InjectorStatusText => _daemonRequired
        ? DaemonSnapshot is null
            ? "守护进程不可用；未启动本地注入"
            : IsElevationSupported
                ? InjectorIntegrity == InjectorIntegrityLevel.Elevated
                    ? "由提权守护进程注入，可覆盖管理员目标"
                    : "守护进程未提权；管理员目标仍会被 UIPI 拦截"
                : "由守护进程注入；同用户运行，无需提权"
        : IsElevationSupported
            ? IsElevated
                ? "已提权：可向以管理员身份运行的应用注入输入"
                : "未提权：向以管理员身份运行的应用注入的输入会被 UIPI 静默丢弃"
            : "当前平台不受 UIPI 限制，无需提权";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MappingOwnerDescription))]
    private MappingOwner _mappingOwner;

    /// <summary>
    /// Who is injecting mapped input, in words. <see cref="MappingOwner.AnotherProcess"/>
    /// states the refusal outright, so a start the injection mutex blocked can never read
    /// as a start that succeeded.
    /// </summary>
    public string MappingOwnerDescription => MappingOwner switch
    {
        MappingOwner.None => "映射未启动",
        MappingOwner.ThisWindow => "本窗口正在注入映射",
        MappingOwner.AnotherProcess => "另一个进程正在注入映射（常驻服务或其他 DeziroJoy 实例），本次启动已被拒绝",
        MappingOwner.ResidentDaemon => IsElevationSupported
            ? $"提权守护进程正在注入映射（PID {DaemonSnapshot?.InjectorPid.ToString(CultureInfo.InvariantCulture) ?? "未知"}）"
            : $"守护进程正在注入映射（PID {DaemonSnapshot?.InjectorPid.ToString(CultureInfo.InvariantCulture) ?? "未知"}）",
        _ => "映射未启动",
    };

    /// <summary>
    /// Raised after a successful elevated relaunch so the view can shut this instance down
    /// and let the elevated copy take over.
    /// </summary>
    public event EventHandler? ElevationRelaunchRequested;

    partial void OnIsElevationSupportedChanged(bool value) => RelaunchElevatedCommand.NotifyCanExecuteChanged();

    partial void OnIsElevatedChanged(bool value) => RelaunchElevatedCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// The explicit hijack preference owns this connection's input lease, and the floating
    /// screen keyboard takes it outright while it is open: an open editor never pauses
    /// injection on its own, and an open keyboard always does, because the pad is what drives
    /// it. When hijack is off every physical press follows the active mapping even while a
    /// draft is visible - but not while the keyboard is up, which is a surface the user asked
    /// for and whose every press must land on a cap rather than in the game behind it.
    /// </summary>
    private void ApplyInputLease()
    {
        var suspended = ScreenKeyboard.IsOpen || (GamepadNavigationEnabled && IsPhysicalInputInScope);

        if (_daemon is not null)
        {
            _ = SetEditorInjectionSuspended(suspended);
        }
        else if (!_daemonRequired)
        {
            _host.SetInjectionSuspended(suspended);
        }
    }

    /// <summary>
    /// Opens or closes the floating screen keyboard at the user's request - the keyboard's
    /// own 完成 and 返回 keys. The engine's 屏幕键盘 key is the other way in, and it reaches
    /// the daemon rather than this method, so the flag has one owner in each mode: the daemon
    /// in daemon mode, this window's host otherwise. A window never flips the flag behind the
    /// daemon's back, because the next engine toggle would then open a keyboard the user
    /// already put away.
    /// </summary>
    private void SetScreenKeyboardOpen(bool open)
    {
        if (ScreenKeyboard.IsOpen == open)
        {
            return;
        }

        if (_daemon is null)
        {
            ScreenKeyboard.IsOpen = open;
            ApplyInputLease();
            return;
        }

        if (!IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；屏幕键盘状态未改变。");
            return;
        }

        // The lease follows the intent at once rather than one round trip later: the pad must
        // stop driving the mapping the moment the keyboard is asked for.
        _ = SetEditorInjectionSuspended(open || (GamepadNavigationEnabled && IsPhysicalInputInScope));
        _ = RequestScreenKeyboardAsync(open);
    }

    private async Task RequestScreenKeyboardAsync(bool open)
    {
        if (_daemon is null)
        {
            return;
        }

        try
        {
            var result = await _daemon.SetScreenKeyboardAsync(open, _lifetime.Token);
            ReportMutationFailure(result, open ? "打开屏幕键盘" : "关闭屏幕键盘");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
        }
    }

    /// <summary>
    /// Locks or unlocks the profile the foreground rules put in effect. In daemon mode the
    /// lock belongs to the daemon - its watcher is the one that applies the rules - so the
    /// request lands there and the snapshot that follows mirrors the authoritative state back
    /// into <see cref="Foreground"/>; a lock set here locally would freeze nothing. In local
    /// mode this window's own watcher is the authority, so the lock lands on it directly.
    /// </summary>
    public async Task SetForegroundLockedAsync(bool locked)
    {
        if (_daemon is null)
        {
            Foreground.SetLocked(locked);
            return;
        }

        if (!IsDaemonConnected)
        {
            SetTransferError("守护进程不可用；锁定状态未改变。");
            return;
        }

        try
        {
            var result = await _daemon.SetForegroundLockedAsync(locked, _lifetime.Token);
            ReportMutationFailure(result, locked ? "锁定当前配置" : "解锁前台规则");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
        }
    }

    /// <summary>
    /// The engine asked to toggle the floating screen keyboard - a 屏幕键盘 special key, in
    /// local mode where this window's own host is the mapping authority. The daemon answers
    /// the same request in daemon mode and publishes the outcome, which is why this path is
    /// the local one alone.
    /// </summary>
    private void OnHostScreenKeyboardToggleRequested()
    {
        ScreenKeyboard.IsOpen = !ScreenKeyboard.IsOpen;
        ApplyInputLease();
    }

    /// <summary>
    /// Types one fragment of the floating screen keyboard into whatever has focus. The daemon
    /// injects in daemon mode; this window's own host injects locally. Neither path throws -
    /// a failure is reported the way every other daemon mutation is - so a key press never
    /// turns into an unobserved exception.
    /// </summary>
    private async Task InjectScreenKeyboardTextAsync(string text)
    {
        if (_daemon is not null)
        {
            if (!IsDaemonConnected)
            {
                SetTransferError("提权守护进程不可用；屏幕键盘没有输出。");
                return;
            }

            try
            {
                var result = await _daemon.InjectTextAsync(text, _lifetime.Token);
                ReportMutationFailure(result, "屏幕键盘输入");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
            }

            return;
        }

        _host.InjectText(text);
    }

    /// <summary>
    /// Taps one key of the floating screen keyboard into whatever has focus - 退格 sends a
    /// Backspace, 清空 sends Ctrl+A and then Delete. Same authority and same error handling
    /// as <see cref="InjectScreenKeyboardTextAsync"/>.
    /// </summary>
    private async Task InjectScreenKeyboardKeyAsync(GamepadKey key, KeyModifiers modifiers)
    {
        if (_daemon is not null)
        {
            if (!IsDaemonConnected)
            {
                SetTransferError("提权守护进程不可用；屏幕键盘没有输出。");
                return;
            }

            try
            {
                var result = await _daemon.InjectKeyAsync(key, modifiers, _lifetime.Token);
                ReportMutationFailure(result, "屏幕键盘按键");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
            }

            return;
        }

        _host.TapKey(key, modifiers);
    }

    internal async Task SetEditorInjectionSuspended(bool suspended)
    {
        if (_daemon is null || !IsDaemonConnected)
        {
            return;
        }

        try
        {
            var result = await _daemon.SetInjectionSuspendedAsync(suspended, _lifetime.Token);
            ReportMutationFailure(result, "手柄劫持租约");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
        }
    }

    /// <summary>
    /// Relaunches the app elevated through the shell "runas" verb (one UAC prompt). The
    /// manifest stays at asInvoker, so this is the only thing that ever elevates. When
    /// mapping is running the relaunch carries <see cref="ResumeMappingArgument"/> so the
    /// elevated copy can pick the user's intent back up.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRelaunchElevated))]
    private void RelaunchElevated()
    {
        var resume = IsEngineRunning ? ResumeMappingArgument : null;

        if (!_elevationService.RelaunchElevated(resume))
        {
            // The UAC prompt was declined; keep running unelevated.
            return;
        }

        ElevationRelaunchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task ToggleEngine()
    {
        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；未启动本地注入回退。");
            return;
        }

        if (_daemon is not null)
        {
            try
            {
                var wasRunning = IsEngineRunning;
                var result = wasRunning
                    ? await _daemon.StopAsync(_lifetime.Token)
                    : await _daemon.StartAsync(_lifetime.Token);
                ReportMutationFailure(result, wasRunning ? "停止映射" : "启动映射");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
            }

            RefreshStatus();
            return;
        }

        ToggleLocalEngine();
    }

    private void ToggleLocalEngine()
    {
        if (IsEngineRunning)
        {
            _host.Stop();
            IsEngineRunning = false;
            MappingOwner = MappingOwner.None;
        }
        else
        {
            try
            {
                _host.Start(TimeSpan.FromMilliseconds(2));
                IsEngineRunning = true;
                MappingOwner = MappingOwner.ThisWindow;
            }
            catch (InjectionAlreadyActiveException)
            {
                IsEngineRunning = false;
                MappingOwner = MappingOwner.AnotherProcess;
            }
        }

        RefreshStatus();
    }

    [RelayCommand]
    private async Task ToggleMapping()
    {
        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；暂停状态未改变。");
            return;
        }

        if (_daemon is not null)
        {
            try
            {
                var requested = !_mappingEnabledRequested;
                var result = await _daemon.SetMappingEnabledAsync(requested, _lifetime.Token);
                if (result.Outcome == MutationOutcome.Accepted)
                {
                    _mappingEnabledRequested = requested;
                }

                ReportMutationFailure(result, requested ? "恢复映射" : "暂停映射");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
            }

            return;
        }

        _host.SetMappingEnabled(!_host.IsMappingEnabled);
        IsMappingEnabled = _host.IsMappingEnabled;
    }

    [RelayCommand]
    private void AddMapping()
    {
        Editor.OtherMappings = ActiveProfile?.Mappings.ToArray() ?? [];

        // A fresh editor arms the base layer of the element the user is about to pick: the
        // 所属层 switcher reaches the temporary layer from there, and the entry the commit
        // writes is added beside whatever the profile already carries.
        Editor.Reset();
        IsEditing = true;
    }

    [RelayCommand]
    private void EditMapping(MappingRowViewModel row)
    {
        // The editor's conflict hints measure the draft against the profile's other
        // mappings, so they are handed in before the draft loads - the entry being edited
        // excluded, or the draft would be measured against the very mapping it replaces.
        Editor.OtherMappings = ActiveProfile?.Mappings
            .Where(mapping => mapping.Id != row.Entry.Id)
            .ToArray() ?? [];

        // A button can carry a base-layer mapping and a temporary-layer one side by side,
        // so the entry the other layer of the same element carries is handed in as well:
        // the editor's 所属层 switcher is what moves between the two, and a commit writes
        // the layer being edited while the other layer's entry stays where it is.
        Editor.LoadFrom(row.Entry, SiblingEntryFor(row.Entry));
        IsEditing = true;
    }

    /// <summary>
    /// The entry the other layer of the same physical element carries, or null when that
    /// layer has none. When one layer carries several entries - a hand-edited profile may -
    /// the first in profile order is the one, so the choice never depends on hash order.
    /// </summary>
    private MappingEntry? SiblingEntryFor(MappingEntry entry)
    {
        if (ActiveProfile is not { } profile)
        {
            return null;
        }

        var layer = entry.Layer == 0 ? 0 : 1;

        return profile.Mappings.FirstOrDefault(mapping =>
            mapping.Id != entry.Id
            && (mapping.Layer == 0 ? 0 : 1) != layer
            && SamePhysicalSource(mapping.Source, entry.Source));
    }

    /// <summary>
    /// Opens the mapping behind one 全局特殊键 row in the editor, exactly as clicking that
    /// mapping's row in the list does: the editing interaction stays the one the mapping
    /// editor already has. A kind the device does not configure has no mapping to open, so
    /// its row carries no affordance - the guidance beside it says how to configure one.
    /// </summary>
    [RelayCommand]
    private void EditSpecialKey(GlobalSpecialKeyItem item)
    {
        if (item.MappingId is not { } id)
        {
            return;
        }

        if (Mappings.FirstOrDefault(row => row.Entry.Id == id) is { } row)
        {
            EditMapping(row);
        }
    }

    [RelayCommand]
    private async Task CommitEditor()
    {
        if (ActiveProfile is null)
        {
            return;
        }

        // A leader key whose two slots are the same is refused, and the editor's own hint
        // beside the slot pickers says why - the same pattern the gesture exclusion uses.
        // A complex-shortcut script that does not parse is refused the same way: the script
        // card already names the line and column, and committing it would write a program
        // the engine cannot build, failing the whole profile at the next reload.
        if (Editor.SpecialKeySlotConflict || Editor.ComplexShortcutScriptInvalid)
        {
            return;
        }

        // A temporary layer that could never behave is refused for the same reason: the
        // engine rejects the whole profile for it, so the commit would fail at the next
        // reload rather than on the one mapping. The editor's own hint names the fix.
        if (Editor.TemporaryLayerDefect is not null)
        {
            return;
        }

        var mappings = ActiveProfile.Mappings.ToList();

        // One write per layer the session touched: the layer being edited always, and the
        // other layer when this session left an edit on it. A button can therefore carry a
        // base-layer action and a temporary-layer one side by side, and neither ever
        // overwrites the other.
        foreach (var commit in Editor.BuildCommit())
        {
            if (commit.ReplacesId is not { } id)
            {
                mappings.Add(commit.Entry);
                continue;
            }

            var index = mappings.FindIndex(m => m.Id == id);
            if (index >= 0)
            {
                mappings[index] = commit.Entry with { Id = id };
            }
        }

        if (await ApplyProfile(ActiveProfile with { Mappings = mappings }))
        {
            IsEditing = false;
        }
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditing = false;
        if (_daemon is not null && DaemonSnapshot is { } snapshot)
        {
            AdoptDaemonProfile(snapshot);
        }
    }

    [RelayCommand]
    private async Task DeleteMapping(MappingRowViewModel row)
    {
        if (ActiveProfile is null)
        {
            return;
        }

        var mappings = ActiveProfile.Mappings.Where(m => m.Id != row.Entry.Id).ToList();
        await ApplyProfile(ActiveProfile with { Mappings = mappings });
    }

    private void ToggleMappingEnabled(MappingRowViewModel row, bool enabled)
    {
        if (ActiveProfile is null)
        {
            return;
        }

        var mappings = ActiveProfile.Mappings
            .Select(m => m.Id == row.Entry.Id ? m with { Enabled = enabled } : m)
            .ToList();

        _ = ApplyProfile(ActiveProfile with { Mappings = mappings });
    }

    [RelayCommand]
    private async Task ApplyMouseSensitivity()
    {
        if (ActiveProfile is null)
        {
            return;
        }

        var settings = MouseSensitivity.BuildSettings();

        // The precision key belongs to the special-key mapping, not to the mouse mapping's
        // legacy fields: the engine reads the special key and ignores the fields once one
        // exists. They are still written - from the special key, never from the panel - so
        // the body stays self-consistent if the key is ever removed, and applying
        // sensitivity can never move the key the engine runs. The special-key mapping itself
        // is not in the rewrite below and is left exactly as it is.
        if (PrecisionKeyOf(ActiveProfile.Mappings) is { } precision)
        {
            settings = settings with { PrecisionButton = precision.Button, PrecisionMode = precision.Mode };
        }

        var mappings = ActiveProfile.Mappings
            .Select(m => m is { Source: StickBinding { Stick: GamepadStick.Right }, Target: MouseMoveTarget }
                ? m with { Target = new MouseMoveTarget(settings) }
                : m)
            .ToList();

        await ApplyProfile(ActiveProfile with { Mappings = mappings });
    }

    [RelayCommand]
    private async Task ApplyScrollSensitivity()
    {
        if (ActiveProfile is null)
        {
            return;
        }

        var settings = ScrollSensitivity.BuildSettings();
        var mappings = ActiveProfile.Mappings
            .Select(m => m is { Source: StickBinding { Stick: GamepadStick.Left }, Target: WheelScrollTarget }
                ? m with { Target = new WheelScrollTarget(settings) }
                : m)
            .ToList();

        await ApplyProfile(ActiveProfile with { Mappings = mappings });
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (ActiveProfile is null || PersistenceDeviceId is not { } deviceId)
        {
            return;
        }

        _presets.SaveSlot(deviceId, ActivePresetSlot, ActiveProfile);
        ReconcilePresetSlots(deviceId);
    }

    private string? PersistenceDeviceId
    {
        get
        {
            if (_daemon is null)
            {
                return ActiveController?.Descriptor.DeviceId;
            }

            if (DaemonSnapshot is not { } snapshot)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(snapshot.ProfileJson))
            {
                var profileDeviceId = DaemonProfileDeviceId(snapshot.ProfileJson);
                if (!string.IsNullOrWhiteSpace(profileDeviceId)
                    && (snapshot.Devices.Count == 0
                        || snapshot.Devices.Any(device => device.DeviceId == profileDeviceId)))
                {
                    return profileDeviceId;
                }
            }

            return snapshot.Devices.Count > 0 ? snapshot.Devices[0].DeviceId : null;
        }
    }

    /// <summary>
    /// The device the daemon's profile body names, read leniently. A body this build cannot
    /// parse whole still names its device in the part that does parse, and losing the id over
    /// damage that costs nothing would make the profile un-editable - no save, no import, no
    /// delete - which is the failure this whole path exists to avoid.
    /// </summary>
    private static string? DaemonProfileDeviceId(string profileJson)
    {
        try
        {
            return ProfileSerializer.Load(profileJson).Profile.DeviceId;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [RelayCommand]
    private async Task ResetToDefault()
    {
        if (ActiveController is null)
        {
            return;
        }

        var fresh = DefaultProfileLibrary.CreateDefault(ActiveController.Descriptor);
        await ApplyProfile(fresh);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransferMessage))]
    private string _transferMessage = string.Empty;

    /// <summary>True when <see cref="TransferMessage"/> reports a failure rather than a success.</summary>
    [ObservableProperty]
    private bool _isTransferError;

    public bool HasTransferMessage => !string.IsNullOrEmpty(TransferMessage);

    private void Report(ProfileTransferResult result)
    {
        TransferMessage = result.Message;
        IsTransferError = !result.Succeeded;
    }

    private void SetTransferError(string message)
    {
        TransferMessage = message;
        IsTransferError = true;
    }

    // ---- Scheme bundle transfer ------------------------------------------------

    /// <summary>
    /// Raised when the user asks to export a whole scheme. The view owns the file dialog (this
    /// view model is Avalonia-free), so it answers by calling <see cref="ExportSchemeTo"/> with
    /// the chosen path, or with null when the dialog was dismissed.
    /// </summary>
    public event EventHandler? ExportSchemePathRequested;

    /// <summary>Import counterpart of <see cref="ExportSchemePathRequested"/>; answered by <see cref="ImportSchemeFrom"/>.</summary>
    public event EventHandler? ImportSchemePathRequested;

    /// <summary>
    /// Raised when a scheme import preview is ready to show. The view answers by opening the
    /// preview dialog; the dialog closes itself when <see cref="IsSchemePreviewOpen"/> goes back
    /// to false, so the view model stays the only owner of the flow's state.
    /// </summary>
    public event EventHandler? SchemeImportPreviewRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSchemePreview))]
    private SchemeImportPreview? _schemePreview;

    /// <summary>True while <see cref="SchemePreview"/> describes an import waiting for a decision.</summary>
    public bool HasSchemePreview => SchemePreview is not null;

    /// <summary>
    /// True while the preview dialog is open. The view watches it to show and hide the dialog;
    /// every path that ends the flow - cancel, a finished undo - clears it.
    /// </summary>
    [ObservableProperty]
    private bool _isSchemePreviewOpen;

    /// <summary>
    /// True once the pending scheme has been applied, so the dialog switches from the
    /// confirm state to the result state with its undo affordance.
    /// </summary>
    [ObservableProperty]
    private bool _schemeImportApplied;

    /// <summary>True while the last scheme import can still be undone.</summary>
    [ObservableProperty]
    private bool _canUndoSchemeImport;

    /// <summary>
    /// The latest recovery point, loaded from disk at startup and replaced only by a successful
    /// import. It survives the stateless render process; a failed undo leaves it available.
    /// </summary>
    private SchemeImportRecovery? _schemeUndo;

    private SchemeBundle? _pendingScheme;

    private string? _pendingSchemeDeviceId;

    [ObservableProperty]
    private bool _applySchemeForegroundRules;

    /// <summary>Raised after import/undo replaces the shared table so an open panel can reload it.</summary>
    public event EventHandler? ForegroundRulesReplaced;

    public IForegroundRuleStore ForegroundRuleStore => _foregroundRuleStore;

    /// <summary>Reopens the persisted recovery point after this GUI, or a previous GUI, closed.</summary>
    [RelayCommand]
    private void ShowSchemeUndo()
    {
        if (_schemeUndo is not { } undo)
        {
            return;
        }

        _pendingScheme = null;
        _pendingSchemeDeviceId = null;
        ApplySchemeForegroundRules = false;
        SchemePreview = new SchemeImportPreview
        {
            SchemeName = $"撤销「{undo.SchemeName}」导入",
            Notes = $"将恢复设备 {undo.DeviceId} 的 {undo.Slots.Count} 个预设槽位和活动槽位 {undo.ActiveSlot}。"
                + (undo.ForegroundRules is null ? string.Empty : "同时恢复导入前的前台规则。")
                + "恢复点来自磁盘，关闭本窗口不会将其清除。",
        };
        SchemeImportApplied = true;
        IsSchemePreviewOpen = true;
        SchemeImportPreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ExportScheme() => ExportSchemePathRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ImportScheme() => ImportSchemePathRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Imports the first built-in template - the Windows pair - through the same preview,
    /// atomic apply and undo a file import goes through.
    /// </summary>
    [RelayCommand]
    private void ImportWindowsTemplate() => BeginSchemeImport(BuiltInSchemeTemplates.Windows);

    /// <summary>
    /// Writes the device's whole scheme to the path the view picked. A null path means the
    /// dialog was dismissed, which is not an error and says nothing.
    /// </summary>
    public void ExportSchemeTo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (PersistenceDeviceId is not { } deviceId)
        {
            SetTransferError("导出失败：没有可导出的方案，请先连接手柄。");
            return;
        }

        var slots = _presets.SlotsInUse(deviceId);
        if (slots.Count == 0)
        {
            SetTransferError("导出失败：当前设备没有可导出的预设。");
            return;
        }

        var bundle = SchemeBundleFactory.FromDevice(
            _presets,
            deviceId,
            ActiveProfile?.Name ?? "未命名方案",
            $"导出自 DeziroJoy {SchemeBundleFactory.CurrentAppVersion()}：{slots.Count} 套预设，" +
            $"{_presets.SpecialKeysInEffect(deviceId).Count} 个全局特殊键，并携带当前前台规则。" +
            "导入后会整体替换目标设备的预设；前台规则需在预览中单独勾选，导入后可撤销一次。",
            SchemeBundleFactory.CurrentAppVersion()) with
        {
            ForegroundRules = _foregroundRuleStore.Load(),
        };

        Report(ProfileTransferService.ExportScheme(bundle, path));
    }

    /// <summary>
    /// Reads the scheme bundle in the file the view picked and opens its import preview. A
    /// malformed file or a bundle a newer build wrote is reported and changes nothing, while a
    /// plain single-profile file - the shape the old single-profile export wrote - is wrapped
    /// as a one-preset scheme and previewed like any bundle. A readable bundle is validated
    /// against its own slot set before the preview opens, so a leader aimed at a slot the
    /// bundle does not carry is refused here rather than imported broken.
    /// </summary>
    public void ImportSchemeFrom(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var loaded = SchemeBundleCodec.Load(path);

        if (!loaded.Succeeded || loaded.Bundle is null)
        {
            SetTransferError(loaded.Message);
            return;
        }

        BeginSchemeImport(loaded.Bundle);
    }

    /// <summary>
    /// Validates a bundle, normalizes the special-key subset it will install, builds the
    /// preview of what it replaces and opens the dialog. Nothing is written here - the device
    /// is touched only by <see cref="ConfirmSchemeImport"/>.
    /// </summary>
    private void BeginSchemeImport(SchemeBundle bundle)
    {
        if (PersistenceDeviceId is not { } deviceId)
        {
            SetTransferError("导入失败：请先连接手柄。");
            return;
        }

        if (FirstLeaderOutsideScheme(bundle) is { } outside)
        {
            SetTransferError($"导入失败：{outside} 上的 Leader 键指向方案之外的槽位。");
            return;
        }

        // The subset the device will end up with: the bundle's own section when it names one,
        // else the one its presets carry under the same first-occurrence rule a device uses.
        // Normalized once here, so the preview shows and the apply installs the same set.
        var normalized = bundle with
        {
            SpecialKeys = bundle.SpecialKeys
                ?? PresetStore.SpecialKeysInEffect(
                    bundle.Presets.OrderBy(preset => preset.Slot).Select(preset => preset.Profile)),
        };

        _pendingScheme = normalized;
        _pendingSchemeDeviceId = deviceId;
        ApplySchemeForegroundRules = false;
        SchemePreview = SchemeImportPreview.Build(normalized, _presets, deviceId);
        SchemeImportApplied = false;
        IsSchemePreviewOpen = true;
        SchemeImportPreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The button a leader key sits on when it names a slot the bundle does not carry, or null
    /// when every leader flips between slots the bundle ships. A leader aimed at nothing is a
    /// switch that silently does nothing, so it is refused before the import rather than
    /// installed. A leader sits in one of three places - the mapping's own target, or the
    /// gesture's two actions - and all three are examined, because the engine executes gesture
    /// actions exactly as it executes the mapping's own target. Only a leader that switches
    /// preset slots is examined: the layer leader switches the one temporary layer the profile
    /// already carries and names no slots, so its slot numbers say nothing.
    /// </summary>
    private static string? FirstLeaderOutsideScheme(SchemeBundle bundle)
    {
        var slots = bundle.Presets.Select(preset => preset.Slot).ToHashSet();

        foreach (var preset in bundle.Presets)
        {
            foreach (var mapping in preset.Profile.Mappings)
            {
                if (mapping.Source is not ButtonBinding button)
                {
                    continue;
                }

                foreach (var target in LeaderCandidates(mapping))
                {
                    if (target is SpecialKeyTarget { Kind: SpecialKeyKind.Leader, Action: LeaderAction.Preset } leader
                        && (!slots.Contains(leader.SlotA) || !slots.Contains(leader.SlotB)))
                    {
                        return MappingDescriptions.Button(button.Button, button.Code);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Every position one mapping can carry a leader key in.</summary>
    private static IEnumerable<MappingTarget?> LeaderCandidates(MappingEntry mapping)
    {
        yield return mapping.Target;

        if (mapping.Source is ButtonBinding { Gesture: { } gesture })
        {
            yield return gesture.DoubleClickAction;
            yield return gesture.LongPressAction;
        }
    }

    /// <summary>
    /// Applies the pending scheme as one atomic operation: the device's whole slot set is
    /// replaced, the device-global special keys are fanned out to every slot, and the running
    /// engine is brought back in step. If any step fails, the captured slot set is restored
    /// before the user is told, so a failed import leaves the device exactly as it was. On
    /// success the replaced set is kept as the one-level undo.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmSchemeImport()
    {
        if (_pendingScheme is not { } bundle || _pendingSchemeDeviceId is not { } deviceId)
        {
            return;
        }

        var slots = bundle.Presets.ToDictionary(preset => preset.Slot, preset => preset.Profile);
        var applyRules = ApplySchemeForegroundRules && bundle.ForegroundRules is not null;
        var recovery = new SchemeImportRecovery
        {
            DeviceId = deviceId,
            SchemeName = bundle.Name,
            Slots = _presets.CaptureSlots(deviceId),
            ActiveSlot = ActivePresetSlot,
            ForegroundRules = applyRules ? _foregroundRuleStore.Load() : null,
        };
        var previousRecovery = _schemeUndo;

        // Durable first: if this save fails, no preset or rule write is allowed to start. If the
        // GUI dies after this point, the next one still has enough state to undo a partial import.
        try
        {
            _schemeImportRecoveryStore.Save(recovery);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetTransferError($"导入失败：无法保存恢复点，尚未修改配置（{exception.Message}）");
            return;
        }

        try
        {
            _presets.ReplaceScheme(deviceId, slots);
            _presets.SyncSpecialKeys(deviceId, bundle.SpecialKeys!);

            if (applyRules)
            {
                ReplaceForegroundRules(bundle.ForegroundRules!);
            }

            if (!await ReloadSchemeSlotsAsync(deviceId, recovery.ActiveSlot, "导入方案"))
            {
                throw new InvalidOperationException("运行中的配置未能重载。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            var rollbackError = await RollbackFailedImportAsync(recovery, previousRecovery);
            SetTransferError($"导入失败：{exception.Message}{rollbackError}");
            return;
        }

        _schemeUndo = recovery;
        CanUndoSchemeImport = true;
        SchemeImportApplied = true;
        if (applyRules)
        {
            ForegroundRulesReplaced?.Invoke(this, EventArgs.Empty);
        }

        Report(ProfileTransferResult.SchemeImported(bundle.Name));
    }

    /// <summary>Restores both configuration halves after a failed import and restores the older undo point.</summary>
    private async Task<string> RollbackFailedImportAsync(
        SchemeImportRecovery failed,
        SchemeImportRecovery? previousRecovery)
    {
        try
        {
            _presets.RestoreSlots(failed.DeviceId, failed.Slots);
            if (failed.ForegroundRules is { } rules)
            {
                ReplaceForegroundRules(rules);
                ForegroundRulesReplaced?.Invoke(this, EventArgs.Empty);
            }

            if (!await ReloadSchemeSlotsAsync(failed.DeviceId, failed.ActiveSlot, "恢复方案"))
            {
                _schemeUndo = failed;
                CanUndoSchemeImport = true;
                return "；运行中的旧配置也未能重载。已保留恢复点，可重试撤销。";
            }

            if (previousRecovery is null)
            {
                _schemeImportRecoveryStore.Clear();
            }
            else
            {
                _schemeImportRecoveryStore.Save(previousRecovery);
            }

            _schemeUndo = previousRecovery;
            CanUndoSchemeImport = previousRecovery is not null;
            return string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            // The durable pre-import point is safer than pretending the older point survived a
            // failed restore. Keep it armed so the user can explicitly return to that state.
            _schemeUndo = failed;
            CanUndoSchemeImport = true;
            return $"；自动回滚未完成：{exception.Message}。已保留恢复点，可重试撤销。";
        }
    }

    private void ReplaceForegroundRules(ForegroundSettings settings)
    {
        _foregroundRuleStore.Save(settings);
        Foreground.Update(settings);
    }

    /// <summary>Dismisses the dialog without consuming the persisted recovery point.</summary>
    [RelayCommand]
    private void CancelSchemeImport()
    {
        _pendingScheme = null;
        _pendingSchemeDeviceId = null;
        SchemePreview = null;
        SchemeImportApplied = false;
        ApplySchemeForegroundRules = false;
        IsSchemePreviewOpen = false;
    }

    /// <summary>
    /// Restores the latest persisted pre-import state. The recovery point is deleted only after
    /// slots, optional rules and the running engine have all been restored, so a disconnected
    /// device/daemon or a write failure leaves the undo available for another attempt.
    /// </summary>
    [RelayCommand]
    private async Task UndoSchemeImport()
    {
        if (_schemeUndo is not { } undo)
        {
            return;
        }

        try
        {
            _presets.RestoreSlots(undo.DeviceId, undo.Slots);
            if (undo.ForegroundRules is { } rules)
            {
                ReplaceForegroundRules(rules);
            }

            if (!await ReloadSchemeSlotsAsync(undo.DeviceId, undo.ActiveSlot, "撤销方案导入"))
            {
                return;
            }

            _schemeImportRecoveryStore.Clear();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            SetTransferError($"撤销失败：{exception.Message}。恢复点已保留，可重试。");
            return;
        }

        _schemeUndo = null;
        CanUndoSchemeImport = false;
        _pendingScheme = null;
        _pendingSchemeDeviceId = null;
        SchemePreview = null;
        SchemeImportApplied = false;
        IsSchemePreviewOpen = false;
        if (undo.ForegroundRules is not null)
        {
            ForegroundRulesReplaced?.Invoke(this, EventArgs.Empty);
        }

        Report(ProfileTransferResult.SchemeImportUndone());
    }

    /// <summary>
    /// Brings the running engine back in step with a device's slot set after a scheme import
    /// or its undo. The target slot is the preferred one when the store still carries it, else
    /// the lowest slot it does carry - an import that removed the active slot has to land
    /// somewhere that exists. Locally the runtime is reloaded here and the in-view state
    /// follows; in daemon mode the daemon owns the store's runtime, so it is asked to switch
    /// to the same slot, which reloads its engine and publishes the snapshot this client then
    /// adopts. Returns false when the reload was refused or failed, so the caller rolls back.
    /// </summary>
    private async Task<bool> ReloadSchemeSlotsAsync(string deviceId, int preferredSlot, string operation)
    {
        var inUse = _presets.SlotsInUse(deviceId);
        var targetSlot = inUse.Contains(preferredSlot)
            ? preferredSlot
            : inUse.DefaultIfEmpty(PresetStore.MinSlot).Min();

        if (_daemon is null)
        {
            var profile = _presets.LoadSlot(deviceId, targetSlot);

            if (ActiveController is { } controller)
            {
                if (profile is { } loaded)
                {
                    _host.Reload(controller.Descriptor.DeviceId, loaded with { PresetSlot = targetSlot });
                }
            }

            if (profile is { } active)
            {
                ActiveProfile = active;
            }

            if (targetSlot != ActivePresetSlot)
            {
                ActivePresetSlot = targetSlot;
            }

            ReconcilePresetSlots(deviceId);
            MarkActivePresetSlot(ActivePresetSlot);
            ReloadSensitivityPanels();
            RefreshMappings();
            RefreshLayerHint();
            return true;
        }

        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError($"提权守护进程不可用；{operation}未生效。");
            return false;
        }

        try
        {
            var result = await _daemon.SwitchPresetSlotAsync(deviceId, targetSlot, _lifetime.Token);

            if (result.Outcome != MutationOutcome.Accepted)
            {
                ReportMutationFailure(result, operation);
                return false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
            return false;
        }

        return true;
    }

    /// <summary>Called from the UI thread for every host sample.</summary>
    public void ApplySample(ControllerSampledEventArgs args)
    {
        SyncControllers();
        Navigate(args.State, args.Runtime.Descriptor.DeviceId);

        if (ActiveController is null)
        {
            return;
        }

        Visualizer.Update(args.State);
        Visualizer.SetConnected(true);
        UpdateGenericPanel(args.State, args.Runtime.Descriptor.DeviceId);

        // The sensitivity panels' real-input calibration reads the same samples the visualizer
        // does; a panel showing the scripted sweep has no consumer for them, so only a
        // calibrating panel is fed.
        FeedCalibration(args.State);
    }

    /// <summary>
    /// Called from the UI thread for every presence sample - the engine-stopped counterpart of
    /// <see cref="ApplySample"/>. The host polls without ticking or injecting, so highlighting
    /// and pad-driven navigation keep working while the mapping loop is off.
    /// </summary>
    public void ApplyPresenceSample(ControllerSampledEventArgs args)
    {
        SyncControllers();
        Navigate(args.State, args.Runtime.Descriptor.DeviceId);

        if (ActiveController is null)
        {
            return;
        }

        Visualizer.Update(args.State);
        Visualizer.SetConnected(true);
        UpdateGenericPanel(args.State, args.Runtime.Descriptor.DeviceId);

        // The presence poll is a sample like any other, so the calibration sees the pad while
        // the mapping loop is off exactly as it does while it runs.
        FeedCalibration(args.State);
    }

    /// <summary>
    /// Feeds the two sensitivity panels' real-input calibration from one sample - the mouse
    /// panel the right stick, the scroll panel the left - exactly the way the visualizer is
    /// fed. The feed is gated on a panel actually calibrating: the readouts and the drift
    /// distribution are the only consumers, both of them live only while the panel is in
    /// real-input mode, so a panel showing the scripted sweep is not fed at all.
    /// </summary>
    private void FeedCalibration(IGamepadState state)
    {
        if (!MouseSensitivity.IsRealInputMode && !ScrollSensitivity.IsRealInputMode)
        {
            return;
        }

        MouseSensitivity.ApplySample(state);
        ScrollSensitivity.ApplySample(state);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread, or inline when this view model was
    /// built without one - the shape every daemon event handler already uses.
    /// </summary>
    private void RunOnUiThread(Action action)
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => action(), null);
            return;
        }

        action();
    }

    /// <summary>
    /// The daemon's receive loop delivers every input sample here, on its own thread. The
    /// display path keeps only the latest sample per device and lets the frame clock drain it
    /// once per UI frame, so a stream that outruns the refresh costs one dispatch per frame
    /// instead of one queued post per sample.
    /// <para>
    /// The calibration is the one consumer that needs the complete stream - the panels'
    /// real-input readouts are built from every sample, not from the frame's last one - so
    /// while a panel is calibrating each sample is marshalled across on its own, exactly as it
    /// always was. The flag is a UI-thread property read from the receive loop; a stale read
    /// costs one sample at the moment of the switch, and the readouts are latest-wins, so the
    /// next sample carries the current value anyway.
    /// </para>
    /// </summary>
    private void OnDaemonInputSample(InputSampleMessage sample)
    {
        if (_sampleFrameClock is null)
        {
            // No frame clock wired - a test, or a window built without one: the sample is
            // applied inline, display and calibration together, exactly as the event always
            // did.
            ApplyDaemonSample(sample);
            return;
        }

        if (MouseSensitivity.IsRealInputMode || ScrollSensitivity.IsRealInputMode)
        {
            var state = sample.ToGamepadState();
            RunOnUiThread(() => FeedCalibration(state));
        }

        lock (_sampleGate)
        {
            _pendingSamples[sample.DeviceId] = (++_sampleOrder, sample);
        }
    }

    /// <summary>
    /// Applies one daemon sample to the display surfaces: navigation and capture first, the
    /// visualizer and the generic panel second, the calibration last - the order the event
    /// always ran in. The device check drops a sample for a device the snapshot no longer
    /// lists, which is how a pad that just unplugged stops lighting the model.
    /// </summary>
    private void ApplyDaemonSample(InputSampleMessage sample)
    {
        if (DaemonSnapshot is { } snapshot
            && !snapshot.Devices.Any(device => device.DeviceId == sample.DeviceId))
        {
            return;
        }

        _daemonInputDeviceId = sample.DeviceId;

        // The panel is one device's surface, so when the device in scope moves - the
        // profile's device, or before any profile the pad being touched - the panel follows
        // it there rather than showing one device's cards lit by another's presses.
        if (_genericPanelDeviceId != ScopedDeviceId())
        {
            RefreshGenericControls();
        }

        var state = sample.ToGamepadState();
        Navigate(state, sample.DeviceId);
        Visualizer.Update(state);
        Visualizer.SetConnected(true);
        UpdateGenericPanel(state, sample.DeviceId);

        // The daemon's samples are the only input a render process sees, so the sensitivity
        // panels' real-input calibration is fed from here exactly as from the host's samples.
        FeedCalibration(state);
    }

    /// <summary>
    /// The frame clock's tick, on the UI thread: drains everything the receive loop queued
    /// since the last frame and applies it in arrival order. One dispatch per frame, however
    /// many samples landed inside it - and the samples a frame coalesced away were never
    /// displayed states, only intermediate ones the latest already stands for.
    /// </summary>
    private void FlushCoalescedSamples()
    {
        List<InputSampleMessage> batch;
        lock (_sampleGate)
        {
            if (_pendingSamples.Count == 0)
            {
                return;
            }

            batch = _pendingSamples.Values
                .OrderBy(entry => entry.Order)
                .Select(entry => entry.Sample)
                .ToList();
            _pendingSamples.Clear();
        }

        foreach (var sample in batch)
        {
            ApplyDaemonSample(sample);
        }
    }

    /// <summary>
    /// Starts the frame clock the coalesced dispatch rides. Called on the UI thread - the
    /// construction thread in production, the daemon's connect handler otherwise - and
    /// idempotent, so a reconnect restarts a clock a disconnect stopped.
    /// </summary>
    private void StartSampleClock() => _sampleFrameClock?.StartTicking();

    /// <summary>
    /// Stops the clock and drops whatever the last frames never dispatched: the samples belong
    /// to a connection that is gone, and a window that lost the daemon shows no pad at all.
    /// </summary>
    private void StopSampleClock()
    {
        if (_sampleFrameClock is null)
        {
            return;
        }

        lock (_sampleGate)
        {
            _pendingSamples.Clear();
            _sampleFrameClock.StopTicking();
        }
    }

    /// <summary>Selects an element without opening the editor or changing the input lease.</summary>
    public void SelectSource(InputBinding source)
    {
        SelectedSource = source;
        SelectedMapping = Mappings.FirstOrDefault(row => SamePhysicalSource(row.Entry.Source, source));
    }

    /// <summary>
    /// Opens the mapping settings for one source, exactly as clicking that element on the model
    /// does: an element that already has a mapping loads it (so confirming replaces that
    /// layer's entry instead of adding a second one for it), and a fresh one is armed anew.
    /// Identity is physical - see <see cref="SamePhysicalSource"/> - so an element reopens its
    /// mapping after a gesture or a direction was configured on it, and an element that carries
    /// both a base-layer and a temporary-layer mapping opens on the base one with the other
    /// reachable through the editor's 所属层 switcher. The matching row is selected so
    /// the list side of the link highlights too; an element with no mapping selects nothing.
    /// </summary>
    public void OpenEditorForSource(InputBinding source)
    {
        SelectedSource = source;

        // Read the selection before it is overwritten below: when one element carries several
        // mappings, the row the user already picked is the one that reopens.
        var entry = ExistingMappingFor(source, SelectedMapping);

        if (entry is not null && Mappings.FirstOrDefault(row => row.Entry == entry) is { } row)
        {
            SelectedMapping = row;
            EditMapping(row);
            return;
        }

        SelectedMapping = null;
        AddMapping();
        Editor.LoadSource(source);

        // The panel's contextual actions point at the control this opens, so the push comes
        // after the source has landed in the editor.
        SyncGenericEditorControl();
    }

    /// <summary>
    /// The mapping a click on one physical element reopens, or null when that element has no
    /// mapping yet. When one element carries several mappings - a stick with a direction per
    /// mapping, a button with a layer per mapping - the row the user already selected wins;
    /// otherwise the base layer's mapping does, because that is the layer a fresh editor
    /// opens on and the one the 所属层 switcher moves up from; and failing both the first in
    /// profile order does, so the choice never depends on hash or dictionary order.
    /// </summary>
    private MappingEntry? ExistingMappingFor(InputBinding source, MappingRowViewModel? selected)
    {
        if (ActiveProfile is not { } profile)
        {
            return null;
        }

        var matches = profile.Mappings
            .Where(mapping => SamePhysicalSource(mapping.Source, source))
            .ToList();

        return matches.FirstOrDefault(mapping => selected is not null && mapping.Id == selected.Entry.Id)
            ?? matches.FirstOrDefault(mapping => mapping.Layer == 0)
            ?? matches.FirstOrDefault();
    }

    /// <summary>
    /// True when two bindings name the same physical element. A gesture or a direction is
    /// configuration ON the element, not part of its identity: a button stays the same button
    /// once a double-click is configured on it, and a stick stays the same stick once it is
    /// pointed at one direction.
    /// </summary>
    private static bool SamePhysicalSource(InputBinding left, InputBinding right) => (left, right) switch
    {
        (ButtonBinding a, ButtonBinding b) => a.Button == b.Button && a.Code == b.Code,
        (StickBinding a, StickBinding b) => a.Stick == b.Stick,
        _ => false,
    };

    private static InputBinding PhysicalSourceKey(InputBinding source) => source switch
    {
        ButtonBinding button => button with { Direction = null, Gesture = null },
        StickBinding stick => stick with { Direction = null },
        _ => source,
    };

    /// <summary>
    /// Routes one gamepad sample to the editor's capture and to navigation, in that order, so
    /// the two never fight over the same press: a press consumed by capture must not also
    /// navigate. Both are scoped to a visible, active mapping tab, so out of scope the sample
    /// only feeds the visualizer.
    /// <para>
    /// The floating screen keyboard takes the whole pad while it is open, ahead of both: it is
    /// a surface the user asked for, it floats above the target application rather than inside
    /// this window, and injection is suspended for it - so a press spent on a cap must never
    /// also navigate, capture or reach the mapping behind it.
    /// </para>
    /// </summary>
    private void Navigate(IGamepadState state, string? deviceId)
    {
        // The capture budget is enforced before anything else reads the sample: a capture
        // that outlived it must not also spend this press.
        EnforceButtonCaptureBudget();

        if (ScreenKeyboard.IsOpen)
        {
            NavigateScreenKeyboard(state, deviceId);
            return;
        }

        // Off means the active mapping owns every press. Even a visible editor must not
        // capture or navigate behind the user's back; highlighting still stays live.
        if (!IsPhysicalInputInScope)
        {
            Editor.CaptureGamepadSample(state);
            return;
        }

        deviceId ??= ActiveController?.Descriptor.DeviceId ?? _daemonInputDeviceId;

        if (!GamepadNavigationEnabled)
        {
            // Off hands every press back to the active mapping - with one exception: an
            // armed button capture. The user asked for that press to name a mapping's
            // source, and the capture lease already keeps it out of the game, so the
            // press completes the capture whoever armed it and navigates nowhere - the
            // hijack stays off, so nothing jumps.
            if (IsEditing && Editor.IsCapturingButton && !Editor.IsCapturingKey)
            {
                CaptureGamepadSample(state, deviceId);
                RememberNavigation(state, deviceId);
            }

            return;
        }

        if (IsEditing)
        {
            // Keyboard capture owns the input while it is armed. A gamepad button is not a
            // keyboard key, so it cannot set the target; and navigating away would abort the
            // capture the user just started, so the press is swallowed.
            if (Editor.IsCapturingKey)
            {
                Editor.CaptureGamepadSample(state);
                RememberNavigation(state, deviceId);
                return;
            }

            // Button capture is armed: the press becomes the mapping's source.
            if (CaptureGamepadSample(state, deviceId))
            {
                RememberNavigation(state, deviceId);
                return;
            }

            // And a press the capture declined is spent here too: the scoped device's every
            // edge is taken above, so what is left is a second pad's press - which must not
            // navigate the editor away from the binding the user just started, exactly as a
            // keyboard capture's presses are swallowed.
            if (Editor.IsCapturingButton)
            {
                RememberNavigation(state, deviceId);
                return;
            }
        }

        NavigateByPhysicalInput(state, deviceId);
    }

    private bool CaptureGamepadSample(IGamepadState state, string? deviceId)
    {
        if (!Editor.IsCapturingButton || deviceId is null)
        {
            return Editor.CaptureGamepadSample(state);
        }

        // The capture answers the device being bound and no other. A press on a second pad
        // must not spend it - that pad was never isolated, so its press still belongs to its
        // own mapping - and it falls through to navigation like any other press would.
        if (deviceId != ScopedDeviceId())
        {
            return false;
        }

        var previous = _navigationBaselines.GetValueOrDefault(deviceId, GamepadState.Neutral);
        Editor.IsCapturingButton = false;
        Editor.CaptureGamepadSample(previous);
        Editor.IsCapturingButton = true;
        return Editor.CaptureGamepadSample(state);
    }

    /// <summary>
    /// Routes one sample's press edges to the floating screen keyboard while it is open. The
    /// same navigation the editor's own keyboard uses - the D-pad moves the cursor, A presses
    /// the key it is on, the bumpers turn the page - plus the two ways out: B and the button
    /// the profile's own 屏幕键盘 key sits on both dismiss it, so the key that summoned the
    /// window is also the key that puts it away. Edges are taken against the same per-device
    /// baseline every other navigation reads, so a held button fires once rather than per
    /// sample.
    /// </summary>
    private void NavigateScreenKeyboard(IGamepadState state, string? deviceId)
    {
        if (deviceId is null)
        {
            _navigationBaselines.Clear();
            return;
        }

        if (!_navigationBaselines.TryGetValue(deviceId, out var previous))
        {
            RememberNavigation(state, deviceId);
            return;
        }

        var edges = state.PressedButtons & ~previous.PressedButtons;
        RememberNavigation(state, deviceId);

        if (edges == GamepadButtonMask.None)
        {
            return;
        }

        // The engine is suspended while the keyboard is open, so the key that summoned it
        // cannot reach the engine to toggle a second time - the window answers it instead.
        if (edges.Has(GamepadButton.B) || IsScreenKeyboardKey(edges))
        {
            SetScreenKeyboardOpen(false);
            return;
        }

        ScreenKeyboard.Navigate(edges);
    }

    /// <summary>
    /// The direction one set of press edges names, or null when they name none. The D-pad is
    /// the keyboard's own cursor control, so this is the whole of what a direction means here.
    /// </summary>
    private static InputDirection? DirectionOf(GamepadButtonMask edges)
    {
        if (edges.Has(GamepadButton.DPadUp))
        {
            return InputDirection.Up;
        }

        if (edges.Has(GamepadButton.DPadDown))
        {
            return InputDirection.Down;
        }

        if (edges.Has(GamepadButton.DPadLeft))
        {
            return InputDirection.Left;
        }

        if (edges.Has(GamepadButton.DPadRight))
        {
            return InputDirection.Right;
        }

        return null;
    }

    /// <summary>
    /// True when one of the newly pressed buttons carries the profile's own 屏幕键盘 key, on
    /// the click or on either gesture action. The engine is suspended while the keyboard is
    /// open, so the key cannot reach the engine to toggle a second time - which is why the
    /// window answers it here instead: the key that summons the keyboard is also the key that
    /// dismisses it.
    /// </summary>
    private bool IsScreenKeyboardKey(GamepadButtonMask edges)
    {
        if (ActiveProfile is not { } profile)
        {
            return false;
        }

        foreach (var mapping in profile.Mappings)
        {
            if (mapping.Source is not ButtonBinding binding || !edges.Has(binding.Button))
            {
                continue;
            }

            if (CarriesScreenKeyboard(mapping.Target)
                || binding.Gesture is { } gesture
                && (CarriesScreenKeyboard(gesture.DoubleClickAction) || CarriesScreenKeyboard(gesture.LongPressAction)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CarriesScreenKeyboard(MappingTarget? target)
        => target is SpecialKeyTarget { Kind: SpecialKeyKind.Keyboard };

    /// <summary>
    /// Handles a newly pressed button or deflected stick while the explicit hijack lease is
    /// active. Every device has an independent baseline, so interleaved samples cannot erase
    /// another pad's edges. Each device's first sample establishes a baseline without navigating.
    /// <para>
    /// A new button press jumps straight to that element's mapping configuration - select and
    /// open in one step, with the element's card scrolled into view - which is the whole point
    /// of the temporary hijack: configuring by touching the pad. A stick deflection stays a
    /// level-1 selection, because analog movement is not a button press; the stick's own click
    /// button is a button and jumps like any other.
    /// </para>
    /// </summary>
    private void NavigateByPhysicalInput(IGamepadState state, string? deviceId)
    {
        if (deviceId is null)
        {
            _navigationBaselines.Clear();
            return;
        }

        if (!_navigationBaselines.TryGetValue(deviceId, out var previous))
        {
            RememberNavigation(state, deviceId);
            return;
        }

        var buttonEdges = state.PressedButtons & ~previous.PressedButtons;
        var extraEdges = state.ExtraButtons.Where(code => !previous.ExtraButtons.Contains(code)).ToList();
        var leftEdge = IsDeflected(state.LeftStickX, state.LeftStickY)
            && !IsDeflected(previous.LeftStickX, previous.LeftStickY);
        var rightEdge = IsDeflected(state.RightStickX, state.RightStickY)
            && !IsDeflected(previous.RightStickX, previous.RightStickY);

        RememberNavigation(state, deviceId);

        // The Guide button is the pad-only route into and out of the preset strip, reserved so it
        // never also selects its own model element. It works from the normal initial state - no
        // keyboard or pointer focus required - which is what makes the strip gamepad-reachable.
        if (buttonEdges.Has(GamepadButton.Guide))
        {
            if (PresetStripFocusIndex >= 0)
            {
                ExitPresetStrip();
            }
            else
            {
                EnterPresetStrip();
            }

            return;
        }

        // While the preset strip is entered the pad drives it rather than the gamepad model:
        // D-pad left/right move focus along it and A activates the focused control (switch /
        // delete-with-confirm / add).
        if (PresetStripFocusIndex >= 0)
        {
            if (buttonEdges.Has(GamepadButton.A))
            {
                ActivatePresetStripFocus();
            }
            else if (buttonEdges.Has(GamepadButton.DPadLeft))
            {
                MovePresetStripFocus(-1);
            }
            else if (buttonEdges.Has(GamepadButton.DPadRight))
            {
                MovePresetStripFocus(1);
            }

            return;
        }

        if (NavigationTarget(buttonEdges, extraEdges, leftEdge, rightEdge) is not { } source)
        {
            return;
        }

        if (IsEditing)
        {
            if (!IsAlreadyEditing(source))
            {
                JumpToSource(source);
            }

            return;
        }

        // The hijack jump answers a new button press with that element's mapping
        // configuration directly - select and open in one step - and asks the view to scroll
        // the element's card into view. A stick deflection is not a button press: pure analog
        // movement keeps the level-1 selection, so flicking a stick never opens an editor.
        // The stick's own click button arrives as a button like any other and does jump.
        if (source is ButtonBinding)
        {
            JumpToSource(source);
            return;
        }

        SelectSource(source);
    }

    /// <summary>
    /// The hijack jump: opens one physical input's mapping configuration - the entry the
    /// element already carries when it has one, a fresh draft when it does not - and asks the
    /// view to scroll that element's card into view. It only ever loads the editor: nothing
    /// is saved, no profile is written, and a draft for another element is replaced exactly
    /// as a click on the model would replace it. A card only exists once the element carries
    /// a mapping, so a fresh draft has nothing to scroll to yet.
    /// </summary>
    private void JumpToSource(InputBinding source)
    {
        OpenEditorForSource(source);

        if (MappingCards.FirstOrDefault(card => SamePhysicalSource(card.Source, source)) is { } card)
        {
            MappingCardVisibleRequested?.Invoke(card);
        }
    }

    /// <summary>
    /// Raised when the hijack jump opened a mapping configuration whose card should be
    /// scrolled into view. The view owns the scroll region, so it answers by bringing the
    /// card's container into view; the view model never touches layout.
    /// </summary>
    public event Action<MappingCardViewModel>? MappingCardVisibleRequested;

    /// <summary>Records the current sample as only this device's navigation/capture baseline.</summary>
    private void RememberNavigation(IGamepadState state, string? deviceId)
    {
        if (deviceId is not null)
        {
            _navigationBaselines[deviceId] = GamepadState.Create(
                state.PressedButtons,
                state.LeftStickX,
                state.LeftStickY,
                state.RightStickX,
                state.RightStickY,
                state.ExtraButtons);
        }
    }

    private static bool IsDeflected(float x, float y)
        => Math.Abs(x) >= StickNavigationThreshold || Math.Abs(y) >= StickNavigationThreshold;

    /// <summary>
    /// The source a sample's edges stand for, or null when nothing new was pressed. Fixed
    /// buttons win over raw codes and buttons over sticks, in the picker's own category order,
    /// so a chord always resolves the same way.
    /// </summary>
    private static InputBinding? NavigationTarget(
        GamepadButtonMask buttonEdges,
        List<int> extraEdges,
        bool leftEdge,
        bool rightEdge)
    {
        foreach (var button in ButtonCategories.All)
        {
            if (buttonEdges.Has(button))
            {
                return new ButtonBinding(button);
            }
        }

        if (extraEdges.Count > 0)
        {
            return new ButtonBinding(GamepadButton.Extra, extraEdges[0]);
        }

        if (leftEdge)
        {
            return new StickBinding(GamepadStick.Left);
        }

        if (rightEdge)
        {
            return new StickBinding(GamepadStick.Right);
        }

        return null;
    }

    /// <summary>
    /// True when the editor is already open for exactly this source. Re-navigating would reload
    /// the mapping from the profile and throw away unsaved edits, so a repeat press of the
    /// element being edited is a no-op.
    /// </summary>
    private bool IsAlreadyEditing(InputBinding source) => IsEditing && source switch
    {
        ButtonBinding button => !Editor.SourceIsStick
            && Editor.SelectedButton == button.Button
            && Editor.SelectedExtraCode == button.Code,
        StickBinding stick => Editor.SourceIsStick && Editor.SelectedStick == stick.Stick,
        _ => false,
    };

    /// <summary>Called from the UI thread when controllers connect or disconnect.</summary>
    public void SyncControllers()
    {
        // Presence must be reflected even while the mapping loop is stopped. While the
        // loop runs, TickOnce already refreshes the runtime table every tick, so asking
        // the provider again here would only duplicate that scan.
        if (!_host.IsRunning)
        {
            _host.RefreshPresence();
        }

        var controllers = _host.Controllers;

        if (!Controllers.SequenceEqual(controllers))
        {
            Controllers.Clear();
            foreach (var controller in controllers)
            {
                Controllers.Add(controller);
            }
        }

        if (controllers.Count == 0)
        {
            // In daemon mode the presentation host is a NoGamepadProvider seam that never
            // sees the pad, so an empty local table is not evidence of absence: the daemon's
            // snapshot owns presence. Clearing here would wipe the snapshot's verdict 300ms
            // after it landed.
            if (_daemon is not null)
            {
                return;
            }

            // A pad that was here and is gone now is not the same as one never plugged in: the
            // wireless idle timer fires silently after 15 idle minutes and no public API can
            // prevent it, so "connect a gamepad" would be misleading for a pad that is asleep.
            var wasConnected = ActiveController is not null;

            if (wasConnected)
            {
                _navigationBaselines.Clear();
                ActiveController = null;
                ActiveProfile = null;
                Mappings.Clear();
                RefreshSpecialKeys([]);
                MouseSensitivity.Reset();
                ScrollSensitivity.Reset();

                // The pad the capture waited for is gone, so the capture ends here rather
                // than on its budget - and with no device there are no declared controls,
                // so the panel empties and the model takes the stage back.
                EndButtonCapture("设备已断开，按键捕获已取消。");
                GenericPanel.Clear();
                RefreshGenericControls();
                IsGenericPanelMode = false;
            }

            HasController = false;
            RefreshStatus(wasConnected);
            Visualizer.SetConnected(false);
            return;
        }

        if (ActiveController is null || !controllers.Contains(ActiveController))
        {
            Activate(controllers[0]);
        }

        Visualizer.SetConnected(true);
    }

    private void RefreshStatus(bool wasConnected = false)
    {
        if (_daemonRequired && DaemonSnapshot is null)
        {
            // Windows names the elevated resident process, because earning that token is
            // what makes injection reach elevated targets there. Linux runs the same daemon
            // as this user with no elevation to earn, so the 提权 wording would describe a
            // mechanism the platform does not have.
            StatusMessage = IsElevationSupported
                ? "提权守护进程不可用；未启用本地注入回退"
                : "守护进程不可用；未启用本地注入回退";
            return;
        }

        // Daemon mode: the snapshot is the presence authority and the local presentation
        // host never sees a pad either way, so its empty table says nothing here. A pad the
        // daemon lost is worded exactly like a local disconnect.
        if (_daemon is not null)
        {
            var daemonDevice = DaemonSnapshot is { Devices.Count: > 0 } snapshot ? snapshot.Devices[0] : null;
            StatusMessage = HasController
                ? daemonDevice is not null
                    ? $"{daemonDevice.Name} · {MappingDescriptions.Layout(daemonDevice.Layout)}"
                    : "已连接手柄 · 等待设备信息"
                : NoControllerText(wasConnected);
            return;
        }

        if (ActiveController is null)
        {
            StatusMessage = NoControllerText(wasConnected);
            return;
        }

        var device = $"{ActiveController.Descriptor.Name} · {MappingDescriptions.Layout(ActiveController.Descriptor.Layout)}";

        var status = IsEngineRunning
            ? device
            : MappingOwner == MappingOwner.AnotherProcess
                ? $"{device} · 启动被拒绝：另一个进程（常驻服务或其他 DeziroJoy 实例）正在注入映射"
                : $"{device} · 映射未启动";

        // With a pad connected the injection reason matters most: the device line alone
        // reads as a healthy session while every injected event goes nowhere. The unmapped-axis
        // diagnostic rides the same line for the same reason - an axis no mapping reaches
        // otherwise passes for a broken stick.
        StatusMessage = status + InjectionUnavailableSuffix() + UnhandledAxisSuffix();
    }

    /// <summary>
    /// The resident half of the injection diagnosis. A sink that failed to create is failed
    /// for the whole session, so its reason stays on the status line with a pad connected
    /// too - the old wiring quoted it only while no pad was plugged in, so a user whose pad
    /// worked and whose mapping did nothing saw a perfectly healthy status line. The wording
    /// comes from the same LinuxDeviceAccess source the probe, the settings card and the
    /// daemon log print.
    /// </summary>
    private string InjectionUnavailableSuffix()
        => _sinkError is { Length: > 0 } ? $" · 注入不可用：{_sinkError}" : string.Empty;

    /// <summary>
    /// The unmapped-axis half of the pad diagnosis, appended beside the sink error with a pad
    /// connected. An ABS axis the tracker does not map can never light up, which is otherwise
    /// indistinguishable from a broken stick or trigger, so the probe's one-line diagnostic -
    /// raw code, range and rest value for an <c>evtest</c> comparison - rides the same status
    /// line. Empty, leaving the line byte-for-byte unchanged, when nothing is unmapped. The
    /// probe is diagnostics and never a reason to break the line, so a failed scan is swallowed
    /// exactly as <see cref="NoControllerText"/> swallows it; the sink error is concatenated
    /// separately, so a throwing probe cannot cost the user that half either.
    /// </summary>
    private string UnhandledAxisSuffix()
    {
        try
        {
            return _runtimeHints().UnhandledAxisGuidance is { Length: > 0 } hint
                ? $" · {hint}"
                : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The "no gamepad" line, plus whatever the platform's one-shot probe can say about why.
    /// "Connect a gamepad" is useless advice when the nodes are unreadable or absent, so the
    /// evdev scan (permission / no nodes / nothing that looks like a pad) and the uinput check
    /// are quoted here - the same words the daemon prints and the settings card explains.
    /// </summary>
    private string NoControllerText(bool wasConnected)
    {
        if (wasConnected)
        {
            // Worded exactly like a local disconnect: a pad the daemon lost is not one never
            // plugged in, and the idle-timer case must not read as "connect a gamepad".
            return "手柄已断开连接，可能已进入休眠，请按手柄电源键唤醒；若仍无反应请检查连接";
        }

        var text = "未检测到手柄，请连接手柄";

        // The sink error outranks the probe's output half: it is this process's actual sink
        // state for the session, it covers everything the open-check probe can see with the
        // same wording, and it also catches ioctl failures the probe never performs. A probe
        // that throws must not swallow it either, so it is appended outside the try.
        var output = _sinkError;

        try
        {
            var hints = _runtimeHints();

            if (hints.DeviceGuidance is { Length: > 0 } device)
            {
                text += $" · {device}";
            }

            output ??= hints.OutputGuidance;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A probe is diagnostics, never a reason to break the status line.
        }

        if (output is { Length: > 0 })
        {
            text += $" · 注入不可用：{output}";
        }

        return text;
    }

    private void Activate(ControllerRuntime runtime)
    {
        ActiveController = runtime;
        ActiveProfile = runtime.Profile;
        HasController = true;

        // The hand-drawn model reads no description of its own, so the device's glyphs are
        // handed to it here: a Nintendo pad labels its face buttons its own way, while the
        // mapping still binds the logical position - the display follows the device, the
        // profile does not.
        Visualizer.SetDevice(runtime.Descriptor);
        RefreshStatus();

        ReloadSensitivityPanels();
        RefreshMappings();
        MigrateLegacyLeader(runtime.Descriptor.DeviceId);
        ReconcilePresetSlots(runtime.Descriptor.DeviceId);
        MarkActivePresetSlot(ActivePresetSlot);

        // The device's own declaration is what the generic panel shows; detection is latched
        // from the samples below, so there is nothing to seed here. A device that declares
        // nothing leaves the panel empty and the hand-drawn model as the surface.
        //
        // A different device is a different panel: the latches, hides and switches belong to
        // the pad that reported them, so they are cleared rather than carried over - the
        // same-device refreshes (a daemon snapshot) keep them, which is what the panel's own
        // reuse is for.
        GenericPanel.Clear();
        RefreshGenericControls();
    }

    internal async Task<bool> ApplyProfile(GamepadProfile updated)
    {
        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；配置未应用。");
            return false;
        }

        if (_daemon is not null)
        {
            var expectedRevision = _profileBaselineRevision;
            var expectedInstanceId = _profileDaemonInstanceId;
            if (expectedInstanceId is null
                || expectedRevision != _daemon.Revision
                || expectedInstanceId != _daemon.DaemonInstanceId)
            {
                SetTransferError("配置已在后台更新；请取消当前编辑并重新加载后再提交。");
                return false;
            }

            if (PersistenceDeviceId is { } targetDeviceId)
            {
                updated = updated with { DeviceId = targetDeviceId };
            }
            else if (DaemonSnapshot is { } snapshot
                && (snapshot.Devices.Count > 0 || !string.IsNullOrEmpty(snapshot.ProfileJson)))
            {
                SetTransferError("守护进程配置或目标设备不可用；配置未应用。");
                return false;
            }
            try
            {
                var result = await _daemon.ApplyAsync(
                    updated.Name,
                    ProfileSerializer.Serialize(updated),
                    expectedRevision,
                    expectedInstanceId,
                    _lifetime.Token);
                if (result.Outcome != MutationOutcome.Accepted)
                {
                    ReportMutationFailure(result, "应用配置");
                    return false;
                }

                _profileBaselineRevision = result.Revision;
                _profileDaemonInstanceId = _daemon.DaemonInstanceId;
                _profileBaselineDeviceId = updated.DeviceId;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                ReportDaemonUnavailable(exception.Message);
                return false;
            }
        }

        ApplyProfileLocally(updated);
        return true;
    }

    private void ApplyProfileLocally(GamepadProfile updated)
    {
        ActiveProfile = updated;

        if (_daemon is null && ActiveController is { } controller)
        {
            // A committed edit belongs to the active preset slot, so the reloaded runtime is
            // tagged with it (Reload no longer re-tags on its own).
            _host.Reload(controller.Descriptor.DeviceId, updated with { PresetSlot = ActivePresetSlot });

            // A committed edit belongs to the active preset slot, not only to the running
            // engine: writing it here is what keeps the slots independent and lets a switch
            // away and back - or a restart - find the edit where it was made. In daemon mode
            // the daemon owns this persistence through its apply path, so it stays local.
            _presets.SaveSlot(controller.Descriptor.DeviceId, ActivePresetSlot, updated);

            // Special keys are the one device-global part of a profile: an edit that added,
            // changed or removed one is fanned out to the device's other slots at store level,
            // leaving every other mapping where it was written. The fan-out writes the store
            // directly - it never re-enters this path - so there is no recursion and no second
            // revision to bump, and it is a no-op when the edit carried no special key.
            _presets.SyncSpecialKeys(controller.Descriptor.DeviceId, updated);
        }

        ReloadSensitivityPanels();
        RefreshMappings();
        RefreshLayerHint();
    }

    /// <summary>
    /// Rebuilds the layer hint from the profile in view and the daemon's latest snapshot.
    /// Runs on every profile change as well as every snapshot, so a mapping committed while
    /// a layer leader is held is named correctly the moment it lands.
    /// </summary>
    private void RefreshLayerHint() => LayerHint.Update(ActiveProfile, DaemonSnapshot);

    partial void OnDaemonSnapshotChanged(StateSnapshot? value)
    {
        OnPropertyChanged(nameof(DaemonStatusText));
        OnPropertyChanged(nameof(InjectorDescription));
        OnPropertyChanged(nameof(InjectorIntegrity));
        OnPropertyChanged(nameof(InjectorStatusText));
        OnPropertyChanged(nameof(MappingOwnerDescription));
        OnPropertyChanged(nameof(CanRelaunchElevated));
        RelaunchElevatedCommand.NotifyCanExecuteChanged();

        // A render process never sees the pad itself: the daemon's snapshot is the device's
        // declaration, so the panel is refreshed from the snapshot and not from the
        // presentation host's empty table. Detection stays latched from the samples.
        RefreshGenericControls();
    }

    private void OnDaemonSnapshot(StateSnapshot snapshot)
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonSnapshot(snapshot), null);
            return;
        }

        // The supervisor's recovery ends the daemon that stopped answering by identity,
        // and the identity is the PID this snapshot names for the injector. Recording it
        // here - at the moment the daemon was seen alive - is what lets the recovery
        // confirm a PID before ending it, and what keeps a recycled one from being
        // mistaken for the daemon. No supervisor in play (a test's view model) is a
        // no-op, not a failure.
        if (snapshot.InjectorPid > 0)
        {
            DesktopLauncher.Daemon?.RecordDaemonPid(snapshot.InjectorPid);
        }

        DaemonSnapshot = snapshot;
        IsEngineRunning = snapshot.Running;
        IsMappingEnabled = snapshot.MappingEnabled;
        _mappingEnabledRequested = snapshot.MappingEnabled;
        ActivePresetSlot = snapshot.ActivePresetSlot;
        SyncPresetSlotsForDaemon();
        MappingOwner = snapshot.Running ? MappingOwner.ResidentDaemon : MappingOwner.None;
        AdoptDaemonProfile(snapshot);

        // The backend the daemon is really running, with the reason a selected one could not
        // start. The settings card shows it beside the saved selection, so a backend switch
        // is only ever reported as done once the daemon's own snapshot says so.
        DaemonSettings.ReportRunningBackend(snapshot.ActiveInputBackend, snapshot.InputBackendError);

        // The same answer decides Guide: SDL3 reports the button on Windows too, XInput has
        // no bit for it, and a snapshot that names its backend is the only surface that
        // knows which of the two is live right now.
        ApplyRunningBackendGuideSupport(snapshot.ActiveInputBackend);
        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            SetTransferError(snapshot.LastError);
        }

        // The daemon is the presence authority: the presentation host is a NoGamepadProvider
        // seam that never sees the pad, so its empty table is not evidence of absence. A pad
        // that was here and is gone now reads as asleep, worded exactly like a local
        // disconnect.
        var wasConnected = HasController;
        HasController = snapshot.HasController;
        if (!snapshot.Devices.Any(device => device.DeviceId == _daemonInputDeviceId))
        {
            _daemonInputDeviceId = snapshot.Devices.Count > 0 ? snapshot.Devices[0].DeviceId : null;
        }

        foreach (var deviceId in _navigationBaselines.Keys
            .Where(id => !snapshot.Devices.Any(device => device.DeviceId == id))
            .ToList())
        {
            _navigationBaselines.Remove(deviceId);
        }

        Visualizer.SetConnected(snapshot.HasController);

        // A render process never sees the pad, so the snapshot's device is the model's only
        // channel to the device's own glyphs - the same description the generic panel shows.
        Visualizer.SetDevice(snapshot.HasController ? DaemonDescriptor() : null);
        RefreshStatus(wasConnected && !snapshot.HasController);

        // The floating screen keyboard follows the flag the daemon owns: the engine's
        // screen-keyboard key toggles it there and the snapshot carries it here, which is what
        // puts the window on screen in whichever process is showing one. The lease moves with
        // it, and only when the flag actually moved - a snapshot per keystroke would otherwise
        // re-send the same suspension on every push.
        if (ScreenKeyboard.IsOpen != snapshot.ScreenKeyboardOpen)
        {
            ScreenKeyboard.IsOpen = snapshot.ScreenKeyboardOpen;
            ApplyInputLease();
        }

        // The layer hint follows the layer the snapshot reports, resolved against the profile
        // it carries: the two arrive together, so the hint is rebuilt once per snapshot.
        RefreshLayerHint();

        // The foreground lock and decisions are the daemon's: mirroring them here is what
        // keeps the rules panel quoting the authority instead of a local evaluation, and what
        // makes a lock survive closing and reopening the window. A daemon that predates the
        // field reports null and the window keeps its own local evaluation.
        if (snapshot.ForegroundState is { } foregroundState)
        {
            Foreground.MirrorDaemonState(foregroundState);
        }
    }

    private void AdoptDaemonProfile(StateSnapshot snapshot)
    {
        if (IsEditing)
        {
            return;
        }

        GamepadProfile? profile = null;
        if (!string.IsNullOrEmpty(snapshot.ProfileJson))
        {
            // The daemon's body is the GUI's only view of the mapping table, and it is not
            // always one this build can read whole - a resident daemon from another version,
            // or a hand-edited profile, can name a target no type here claims. Refusing the
            // whole body over the part that does not parse is what left the table empty and
            // the daemon's mappings undeletable, so what parses is adopted and what does not
            // is reported: the rows the user can still see are the rows they can delete, and
            // the next commit writes the readable profile back over the damaged one.
            var load = ProfileSerializer.Load(snapshot.ProfileJson);
            ReportProfileDamage(load.Problems);
            profile = load.Profile;
        }
        else
        {
            ReportProfileDamage([]);
        }

        var profileDeviceId = !string.IsNullOrWhiteSpace(profile?.DeviceId)
            ? profile.DeviceId
            : snapshot.Devices.Count > 0 ? snapshot.Devices[0].DeviceId : null;
        var instanceId = _daemon?.DaemonInstanceId;
        if (snapshot.Revision == _profileBaselineRevision
            && instanceId == _profileDaemonInstanceId
            && profileDeviceId == _profileBaselineDeviceId)
        {
            return;
        }

        _profileBaselineRevision = snapshot.Revision;
        _profileDaemonInstanceId = instanceId;
        _profileBaselineDeviceId = profileDeviceId;
        if (profile is not null)
        {
            ApplyProfileLocally(profile);
        }
        else
        {
            ActiveProfile = null;
            RefreshMappings();
        }
    }

    /// <summary>
    /// Records what the daemon's profile body could not be read for, and says so. The notice
    /// is state rather than a one-shot banner: the transfer message is cleared by the next
    /// accepted mutation, and a body the daemon still holds damaged - the table in view is
    /// only the part of it that parsed - has to stay explained until the user commits a fix.
    /// </summary>
    private void ReportProfileDamage(IReadOnlyList<string> problems)
    {
        _profileDamage = problems.Count > 0
            ? $"守护进程配置部分无法解析，已显示可读取的部分：{string.Join("；", problems)}"
            : null;

        if (_profileDamage is { } damage)
        {
            SetTransferError(damage);
        }
    }

    private void OnDaemonConnected()
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonConnected(), null);
            return;
        }

        // The clock stopped when the last connection went; a reconnected daemon delivers
        // samples again, so it runs again. Idempotent, so a connect that lands while it
        // already runs changes nothing.
        StartSampleClock();

        if (_daemon?.Snapshot is { } snapshot)
        {
            OnDaemonSnapshot(snapshot);
        }

        TransferMessage = string.Empty;
        IsTransferError = false;
        ApplyInputLease();
    }

    private void OnDaemonDisconnected()
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonDisconnected(), null);
            return;
        }

        // Stop dispatching before the state below is torn down: the samples still queued
        // belong to the connection that just went, and a frame that ran after this would
        // paint a pad the daemon no longer reports.
        StopSampleClock();

        DaemonSnapshot = null;
        _daemonInputDeviceId = null;
        _navigationBaselines.Clear();
        IsEngineRunning = false;
        IsMappingEnabled = false;
        MappingOwner = MappingOwner.None;

        // A capture armed for a device that is gone spends nothing: the pad it waits for
        // will not answer, so the capture ends here rather than on its budget.
        EndButtonCapture("设备已断开，按键捕获已取消。");

        // No device means no declared controls, so the panel empties and the hand-drawn
        // model - the surface that needs no declaration - takes the stage back.
        GenericPanel.Clear();
        RefreshGenericControls();
        IsGenericPanelMode = false;

        // No snapshot means no active layer, so the hint empties and the overlay hides.
        RefreshLayerHint();
        SetTransferError("守护进程连接已断开；映射状态未知，未启用本地注入回退。");
        RefreshStatus();
    }

    public void ReportDaemonUnavailable(string reason)
    {
        // The prefix follows the platform, exactly as RefreshStatus words it: on the
        // same-user platform there is no elevated token the daemon needed.
        SetTransferError($"{(IsElevationSupported ? "提权守护进程" : "守护进程")}不可用：{reason}；未启用本地注入回退。");
        RefreshStatus();
    }

    /// <summary>
    /// How long the reconnect button waits for an already-connected daemon to answer one
    /// state read before treating it as lost. The controller's own request timeout is ten
    /// seconds - a budget sized for a slow mutation, not for a button press - so the
    /// liveness read carries its own, and the supervised restart is what follows a daemon
    /// that does not answer inside it.
    /// </summary>
    private static readonly TimeSpan DaemonLivenessProbeTimeout = TimeSpan.FromSeconds(3);

    [RelayCommand]
    private async Task ReconnectDaemon()
    {
        if (!_daemonRequired || _daemon is null)
        {
            return;
        }

        // Phase 1 - the plain client reconnect, for a daemon that is merely not
        // connected: still opening its pipe after a start, or a connection the last
        // window close dropped. It costs the resident daemon nothing.
        string? clientFailure = null;
        if (_daemon.State != DaemonConnectionState.Connected)
        {
            try
            {
                await _daemon.ConnectAsync(_lifetime.Token);
                await AdoptReconnectedDaemon();
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                // The daemon did not answer: the supervised restart below is the only
                // thing that can bring it back.
                clientFailure = exception.Message;
            }
        }
        else
        {
            // Already connected: prove the daemon answers before calling it healthy. A
            // daemon whose pipe still accepts connects but whose requests never
            // complete reads as connected here, and that is exactly the state a
            // reconnect button used to do nothing about.
            using var liveness = new CancellationTokenSource(DaemonLivenessProbeTimeout);
            try
            {
                await _daemon.GetStateAsync(liveness.Token);
                RefreshStatus();
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
            {
                // Unresponsive: fall through to the supervised restart.
                clientFailure = exception.Message;
            }
        }

        // Phase 2 - the supervised restart. A client reconnect cannot bring back a
        // daemon that stopped answering: the process holding the mapping pipe, the
        // injection mutex and the instance slot is still resident, so only the
        // supervisor can end that exact daemon - the owned child, the systemd unit's,
        // the planned task's - and start its replacement. That is what this button has
        // to mean, and the phases below are what it reports.
        if (_recoverDaemon is null)
        {
            ReportDaemonUnavailable(clientFailure is { } reason
                ? $"客户端重连失败（{reason}），且没有可用的监督重启入口"
                : "没有可用的守护进程监督重启入口");
            return;
        }

        if (Interlocked.CompareExchange(ref _recoveryRunning, 1, 0) != 0)
        {
            // One supervised restart at a time: a second click while the first is
            // ending and replacing the daemon would race it for the same slot.
            return;
        }

        try
        {
            TransferMessage = "正在安全重启失联的守护进程…";
            IsTransferError = false;

            var report = await _recoverDaemon();

            if (!report.Recovered)
            {
                SetTransferError(DescribeRecoveryFailure(report));
                RefreshStatus();
                return;
            }

            // The replacement daemon is up: connect this client to it. The daemon this
            // controller held was just ended, so its pipe has broken and the receive
            // loop is on its way to Disconnected - waiting for that, bounded, is what
            // makes the reconnect below re-handshake against the replacement instead
            // of adopting the last snapshot the dead daemon served.
            if (_daemon.State == DaemonConnectionState.Connected)
            {
                var broken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnBroken() => broken.TrySetResult();
                _daemon.Disconnected += OnBroken;
                try
                {
                    await broken.Task.WaitAsync(TimeSpan.FromSeconds(2), _lifetime.Token);
                }
                catch (TimeoutException)
                {
                    // The pipe never broke: the daemon this controller held was not the
                    // one the recovery ended, and the reconnect below runs against
                    // whatever answers.
                }
                finally
                {
                    _daemon.Disconnected -= OnBroken;
                }
            }

            // The controller's own reconnect loop is spinning against the same pipe, so
            // an attempt can land mid-flight, and a daemon that is still opening its pipe
            // answers the supervisor's probe a moment before it serves a handshake: both
            // are retries, not failures - and none of them may erase the phases of a
            // restart that did happen.
            var connected = false;
            Exception? lastConnectFailure = null;
            for (var attempt = 1; attempt <= 5 && !connected; attempt++)
            {
                try
                {
                    await _daemon.ConnectAsync(_lifetime.Token);
                    connected = true;
                }
                catch (Exception exception) when (exception
                    is InvalidOperationException or DaemonUnavailableException or DaemonHandshakeException
                        or DaemonDisconnectedException or IOException or TimeoutException or ObjectDisposedException)
                {
                    lastConnectFailure = exception;
                    if (attempt < 5)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), _lifetime.Token);
                    }
                }
            }

            if (!connected)
            {
                // The daemon was replaced but this client could not re-handshake. The
                // phases are what happened and they stay on the line: a report that
                // names only the reconnect failure erases the restart that succeeded.
                SetTransferError(
                    $"守护进程已重启，但重新连接失败：{lastConnectFailure?.Message ?? "原因未知"}"
                    + $"（{string.Join("；", report.Phases)}）");
                RefreshStatus();
                return;
            }

            await AdoptReconnectedDaemon();
            TransferMessage = report.Phases.Count > 0
                ? $"守护进程已重启并恢复连接：{string.Join("；", report.Phases)}。"
                : "守护进程已重启并恢复连接。";
            IsTransferError = false;
            RefreshStatus();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
        }
        finally
        {
            Volatile.Write(ref _recoveryRunning, 0);
        }
    }

    /// <summary>
    /// Adopts whatever the reconnected daemon is serving: its snapshot, the cleared
    /// transfer error and this connection's editor lease. Shared by the client
    /// reconnect and the post-restart reconnect so the two land in exactly the same
    /// state.
    /// </summary>
    private async Task AdoptReconnectedDaemon()
    {
        if (_daemon?.Snapshot is { } snapshot)
        {
            OnDaemonSnapshot(snapshot);
        }

        TransferMessage = string.Empty;
        IsTransferError = false;
        await SetEditorInjectionSuspended(GamepadNavigationEnabled && IsPhysicalInputInScope);

        RefreshStatus();
    }

    /// <summary>
    /// The reconnect button's failure line: the phases the supervised restart got
    /// through, then the reason it stopped. A recovery that reports neither is not one.
    /// </summary>
    private static string DescribeRecoveryFailure(DaemonRecoveryReport report)
    {
        var phases = report.Phases.Count > 0 ? $"{string.Join("；", report.Phases)}；" : string.Empty;
        return $"守护进程重启失败：{phases}{report.Failure ?? "原因未知"}";
    }

    private void ReportMutationFailure(MutationResult result, string operation)
    {
        switch (result.Outcome)
        {
            case MutationOutcome.Unknown:
                SetTransferError($"{operation}结果未知；已请求重新读取状态，未自动重试。");
                break;
            case MutationOutcome.Refused:
                SetTransferError($"{operation}被拒绝：{result.ErrorMessage ?? result.ErrorCode ?? "守护进程拒绝"}");
                if (result.ErrorCode == IpcErrorCodes.RevisionMismatch)
                {
                    _ = RefreshDaemonStateAfterRefusal();
                }
                break;
            case MutationOutcome.Accepted when IsTransferError:
                // A body the daemon still holds damaged stays explained rather than being
                // cleared along with the mutation's own error.
                if (_profileDamage is { } damage)
                {
                    SetTransferError(damage);
                }
                else
                {
                    TransferMessage = string.Empty;
                    IsTransferError = false;
                }

                break;
        }
    }

    private async Task RefreshDaemonStateAfterRefusal()
    {
        if (_daemon is null)
        {
            return;
        }

        try
        {
            await _daemon.GetStateAsync(_lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            SetTransferError($"重新读取守护状态失败：{exception.Message}");
        }
    }

    public async Task DisconnectDaemonAsync()
    {
        if (_daemon is not null)
        {
            _daemon.SnapshotChanged -= OnDaemonSnapshot;
            _daemon.InputSampled -= OnDaemonInputSample;
            _daemon.Connected -= OnDaemonConnected;
            _daemon.Disconnected -= OnDaemonDisconnected;
        }

        // The events are gone, so nothing can queue another sample: the clock stops here and
        // whatever the last frames never dispatched is dropped with it.
        StopSampleClock();

        await _lifetime.CancelAsync();
        if (_daemon is not null)
        {
            await _daemon.DisposeAsync();
        }

        _lifetime.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_daemon is null && !_daemonRequired)
        {
            _host.SetInjectionSuspended(false);
        }

        Editor.PropertyChanged -= OnEditorPropertyChanged;

        // The frame clock is unsubscribed before the daemon connection goes, so a tick that
        // races the shutdown finds no pending work rather than a half-torn-down window; the
        // clock itself is the window's to release.
        if (_sampleFrameClock is not null)
        {
            _sampleFrameClock.Tick -= FlushCoalescedSamples;
        }

        // Released before the daemon connection goes, so a pause a foreground rule left behind
        // is lifted while the engine that holds it is still reachable - and with it the platform
        // foreground source the watcher opened. In daemon mode the pause was never this window's
        // to begin with: the daemon's own watcher owns it and lifts it on its own shutdown.
        Foreground.Dispose();

        await DisconnectDaemonAsync();

        // The default service owns an HttpClient this view model created; an injected one is
        // the caller's to dispose. This runs after the lifetime token is cancelled above, so
        // an in-flight startup check is already settling rather than racing the disposal.
        if (_ownsUpdateService && _updateService is IDisposable disposable)
        {
            disposable.Dispose();
        }

        // The update card owns the orchestrator it built around the shared IUpdateService.
        // Disposing the card first stops the fire-and-forget auto-download the startup check
        // may have started; the service itself was passed in, so only the downloader the
        // orchestrator created is released. An orchestrator the card did not build stays the
        // caller's.
        Update.Dispose();
        if (Update.OwnsOrchestrator)
        {
            Update.Orchestrator.Dispose();
        }

        // DisconnectDaemonAsync above already stopped the clock and dropped the queue; this
        // releases the timer itself, which nothing else owns.
        _sampleFrameClock?.Dispose();

        GC.SuppressFinalize(this);
    }

    private void ReloadSensitivityPanels()
    {
        if (ActiveProfile is null)
        {
            return;
        }

        // The engine sources the precision key from the profile's special-key mapping once
        // one exists and ignores the mouse mapping's own fields, which migration leaves at
        // their defaults - so the panel reports the special key, and only a profile without
        // one falls back to the fields, which is what an un-migrated profile still runs on.
        var precision = PrecisionKeyOf(ActiveProfile.Mappings);

        foreach (var mapping in ActiveProfile.Mappings)
        {
            switch (mapping.Target)
            {
                case MouseMoveTarget mouse when mapping.Source is StickBinding { Stick: GamepadStick.Right }:
                    MouseSensitivity.LoadFrom(mouse.Settings);
                    if (precision is { } key)
                    {
                        MouseSensitivity.ShowPrecisionKey(key.Button, key.Mode);
                    }
                    break;
                case WheelScrollTarget wheel when mapping.Source is StickBinding { Stick: GamepadStick.Left }:
                    ScrollSensitivity.LoadFrom(wheel.Settings);
                    break;
            }
        }
    }

    /// <summary>
    /// The precision key the engine actually reads, as the sensitivity panel reports it: the
    /// profile's precision special key - its own target or one of the source gesture's two
    /// actions, the positions the engine registers from - when it is enabled and its source
    /// names a button. Null when the profile carries no such key, and the caller then falls
    /// back to the mouse mapping's own legacy fields: a disabled key is not registered by the
    /// engine either, and a deflected stick also engages a special key, a shape the panel's
    /// button label cannot name.
    /// </summary>
    private static (GamepadButton Button, PrecisionMode Mode)? PrecisionKeyOf(IReadOnlyList<MappingEntry> mappings)
    {
        foreach (var entry in mappings)
        {
            if (!entry.Enabled || entry.Source is not ButtonBinding binding)
            {
                continue;
            }

            if (SpecialKeyConflicts.KeyOf(entry) is { Kind: SpecialKeyKind.Precision } key)
            {
                return (binding.Button, key.PrecisionMode);
            }
        }

        return null;
    }

    // ---- The generic panel -------------------------------------------------------

    /// <summary>
    /// How long the editor's button capture stays armed before it cancels itself. A capture
    /// the user walked away from would otherwise spend every later press on a draft nobody
    /// is watching; the budget is generous enough to reach for the pad and press, and short
    /// enough that a forgotten capture cannot linger. Settable so a test can watch the
    /// budget expire without waiting out the real one - per window, so tests running beside
    /// each other never borrow one another's clock.
    /// </summary>
    internal TimeSpan ButtonCaptureTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The clock the capture budget reads, so a test can walk one past its deadline without
    /// waiting out the real seconds. Production reads the wall clock.
    /// </summary>
    internal Func<DateTime> CaptureClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Opens the mapping settings for one generic-panel control, exactly as a click on the
    /// hand-drawn model does. The control is remembered as the capture target, so the panel
    /// can mark it while the editor's button capture is armed for it.
    /// </summary>
    private void OpenGenericControl(GenericControlItem item)
    {
        _genericCaptureTarget = item.Source;
        OpenEditorForSource(item.Source);
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MappingEditorViewModel.SelectedButton)
            or nameof(MappingEditorViewModel.SelectedExtraCode)
            or nameof(MappingEditorViewModel.SelectedStick)
            or nameof(MappingEditorViewModel.SourceIsStick))
        {
            // The editor moved to another control: the panel's contextual actions follow it.
            SyncGenericEditorControl();
            return;
        }

        if (e.PropertyName != nameof(MappingEditorViewModel.IsCapturingButton))
        {
            return;
        }

        if (Editor.IsCapturingButton)
        {
            _captureDeadline = CaptureClock() + ButtonCaptureTimeout;
            GenericPanel.UpdateCapture(isCapturing: true, _genericCaptureTarget);
        }
        else
        {
            GenericPanel.UpdateCapture(isCapturing: false, null);
        }

        // The capture lease follows the capture itself: the device being bound stops
        // dispatching its mappings - only that device - while the user reaches for the
        // button, and every way the capture ends releases the lease with it.
        ApplyInputCaptureDevice();
    }

    /// <summary>
    /// Takes or releases the editor's input-capture lease on the device being bound. Locally
    /// the host holds the lease and pauses exactly that device's mapping dispatch - the other
    /// pads keep mapping - while still polling and publishing its samples; in daemon mode the
    /// same request travels over IPC, because there the resident daemon owns the dispatch.
    /// The keyboard and hijack leases are separate pauses and are untouched by this one.
    /// </summary>
    private void ApplyInputCaptureDevice()
    {
        var deviceId = Editor.IsCapturingButton ? ScopedDeviceId() : null;

        if (_daemon is null)
        {
            _host.SetInputCaptureDevice(deviceId);
            return;
        }

        _ = ApplyDaemonInputCaptureAsync(_daemon, deviceId);
    }

    private async Task ApplyDaemonInputCaptureAsync(DaemonController daemon, string? deviceId)
    {
        try
        {
            var result = await daemon.SetInputCaptureDeviceAsync(deviceId, _lifetime.Token);
            if (result.Outcome != MutationOutcome.Accepted)
            {
                ReportMutationFailure(result, "输入捕获");

                if (deviceId is not null)
                {
                    // A lease the daemon refused is a capture that is not happening: the pad
                    // was never isolated, so its presses still belong to the active mapping
                    // and waiting for one would spend them on a draft nobody is binding.
                    // Standing down releases the local lease with it, exactly as a
                    // successful end does.
                    EndButtonCapture("输入捕获租约被拒绝，捕获已取消。");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
        }
    }

    /// <summary>
    /// Ends the editor's button capture, saying why on the status line when the user did not
    /// ask for it. Besides a successful press there are three ways out: the budget running
    /// out, the device going away, and the editor closing - a capture armed for an editor
    /// that is no longer open would spend the next press on a draft nobody sees.
    /// </summary>
    private void EndButtonCapture(string? reason)
    {
        if (!Editor.IsCapturingButton)
        {
            return;
        }

        Editor.IsCapturingButton = false;

        // The control the capture waited for is nobody's target any more: the panel's mark
        // is cleared by the property change below, and the remembered source goes with it,
        // so a later capture can never mark a control left over from an earlier one.
        _genericCaptureTarget = null;

        if (reason is not null)
        {
            StatusMessage = reason;
        }
    }

    /// <summary>
    /// The capture budget, checked on the sample stream - the one clock that is already
    /// running while a capture is armed - so a capture that outlives its budget ends on the
    /// next sample rather than on a timer of its own. The window's sync tick calls it too:
    /// a pad nobody is touching produces no samples, so a forgotten capture would otherwise
    /// never expire, and the 300 ms UI clock is what ends it without waiting for input.
    /// </summary>
    internal void EnforceButtonCaptureBudget()
    {
        // A deadline nobody set is not an expired one: the check runs on the UI's clock and
        // on the sample stream, and a capture armed a heartbeat ago must not be ended by a
        // read that raced the arming.
        if (Editor.IsCapturingButton && _captureDeadline != default && CaptureClock() >= _captureDeadline)
        {
            EndButtonCapture("按键捕获已超时取消，请重试。");
        }
    }

    partial void OnIsEditingChanged(bool value)
    {
        if (!value)
        {
            EndButtonCapture(reason: null);
        }

        // The panel's contextual actions follow the editor: they act on the control being
        // edited, and on nothing once the card closes.
        SyncGenericEditorControl();
    }

    partial void OnActiveProfileChanged(GamepadProfile? value)
    {
        // The declaration itself moves with the profile: a source the device never declared
        // gains its first mapping - or loses its last - so the panel's items follow without
        // a reload that would drop every detection latch and hide.
        GenericPanel.SyncDeclarations(DeviceControls(), IsControlBound);

        // The panel's bound state is derived from the profile, so every adoption and apply
        // moves a control between the panel and 更多输入 - display only, the mappings
        // themselves are untouched - and each stick's mode selector follows the mappings the
        // profile really carries, including after a reload from the daemon.
        GenericPanel.ApplyBindings(IsControlBound);

        if (value is { } profile)
        {
            GenericPanel.SyncStickModes(profile.Mappings);
        }
    }

    private bool IsControlBound(InputBinding source)
        => ActiveProfile is { } profile && profile.Mappings.Any(entry => SamePhysicalSource(entry.Source, source));

    /// <summary>
    /// Brings the generic panel to the device's own declaration. The declaration is the
    /// device's - the active controller's descriptor, or the daemon snapshot's entry for the
    /// device in scope - and it is the only source of the panel's items: a device whose
    /// platform has written no description produces none, and the hand-drawn model stays the
    /// mapping surface for it.
    /// <para>
    /// The items themselves are reused where the declaration still names them, so a snapshot
    /// that re-describes the same device keeps every latch, hide and switch the panel holds;
    /// what the declaration gained or lost is all that moves.
    /// </para>
    /// </summary>
    private void RefreshGenericControls()
    {
        var descriptors = DeviceControls();
        GenericPanel.Load(descriptors, IsControlBound);

        // A reused item keeps the mode its mappings last said, and a reload - or another
        // client's edit - can have moved the mappings under it, so the selector is re-derived
        // from the profile rather than trusted to remember.
        if (ActiveProfile is { } profile)
        {
            GenericPanel.SyncStickModes(profile.Mappings);
        }

        if (descriptors.Count == 0)
        {
            IsGenericPanelMode = false;
        }
        else if (!_surfaceChosenByUser)
        {
            // A device that describes its controls starts on the panel; the model stays one
            // switch away for anyone who prefers it.
            IsGenericPanelMode = true;
        }

        _genericPanelDeviceId = ScopedDeviceId();
        OnPropertyChanged(nameof(CanUseGenericPanel));

        // A reload can replace the items, so the control the editor has open is re-found
        // rather than left pointing at an item the new declaration no longer carries.
        SyncGenericEditorControl();
    }

    /// <summary>
    /// Paints one sample onto the generic panel - the panel of the device in scope, and no
    /// other. A second pad's press must not light the first pad's cards or latch its
    /// detections, exactly as its capture only answers the device being bound: the panel is
    /// one device's surface, whichever device produced the sample.
    /// </summary>
    private void UpdateGenericPanel(IGamepadState state, string? deviceId)
    {
        if (deviceId is not null && deviceId == ScopedDeviceId())
        {
            GenericPanel.Update(state);
        }
    }

    /// <summary>
    /// The controls the device in scope declares, as the Core's descriptors. Locally that is
    /// the active controller's own declaration; in daemon mode a render process never sees the
    /// pad, so it is the snapshot's entry for the device the samples come from (falling back
    /// to the snapshot's first device before any sample has named one).
    /// <para>
    /// A profile can also carry sources the declaration never names - a config written before
    /// the device described itself, a raw code, a control a future backend adds. A bound
    /// control is always shown, so those join the panel as extensions rather than sitting
    /// invisible in the profile: they are appended after the declared ones in the profile's
    /// own fixed order, and the match against what is already there is physical - the same
    /// control whatever direction or gesture a mapping configures on it - so a declared stick
    /// is not appended a second time because one of its four directions is bound. A device
    /// that declares nothing stays declarationless: the hand-drawn model is its surface, and
    /// a legacy profile is not a reason to build a panel for it.
    /// </para>
    /// </summary>
    private IReadOnlyList<GamepadControlDescriptor> DeviceControls()
    {
        var declared = _daemon is null
            ? ActiveController?.Descriptor.Controls ?? []
            : CurrentDaemonDevice()?.Controls ?? [];

        if (declared.Count == 0 || ActiveProfile is not { } profile)
        {
            return declared;
        }

        var merged = new List<GamepadControlDescriptor>(declared);
        foreach (var entry in profile.Mappings)
        {
            if (merged.Any(descriptor => SamePhysicalSource(descriptor.Source, entry.Source)))
            {
                continue;
            }

            merged.Add(new GamepadControlDescriptor(
                entry.Source,
                BoundSourceLabel(entry.Source),
                GamepadControlKind.Other,
                GamepadControlZone.Extensions));
        }

        return merged;
    }

    /// <summary>
    /// What the panel calls a bound control the device never declared. The Core's own naming
    /// where it has a case - a standard button, a stick, a raw code by its ordinal - and the
    /// source's own description where it does not, so an unsupported binding a newer build
    /// wrote still reads as itself.
    /// </summary>
    private static string BoundSourceLabel(InputBinding source) => source switch
    {
        ButtonBinding { Button: GamepadButton.Extra } extra => GamepadControlLabels.Generic(extra.Code),
        ButtonBinding button => GamepadControlLabels.Button(button.Button),
        StickBinding stick => GamepadControlLabels.Stick(stick.Stick),
        _ => source.ToString() ?? "未知输入",
    };

    /// <summary>
    /// The scoped daemon device as a descriptor, so the surfaces that want a device rather
    /// than a snapshot entry - the hand-drawn model's labels - can read one. Null when the
    /// snapshot names no device, which is the same "no device" answer a null descriptor
    /// carries everywhere else.
    /// </summary>
    private GamepadDescriptor? DaemonDescriptor()
        => CurrentDaemonDevice() is { } device
            ? new GamepadDescriptor(device.DeviceId, device.Name) { Controls = device.Controls ?? [] }
            : null;

    /// <summary>
    /// The physical control the mapping editor is editing, as the panel's own item - or null
    /// when the editor is closed or the control is not on this device's panel. The panel's
    /// contextual actions (hide, the stick's two switches) act on exactly this, which is why
    /// the window pushes it in rather than letting the panel guess.
    /// </summary>
    private void SyncGenericEditorControl()
        => GenericPanel.SetEditorControl(
            IsEditing && EditorControlSource() is { } source ? GenericPanel.ItemFor(source) : null);

    /// <summary>The source the editor is editing, read the same way the connector line reads it.</summary>
    private InputBinding? EditorControlSource()
        => Editor.SourceIsStick
            ? new StickBinding(Editor.SelectedStick)
            : new ButtonBinding(Editor.SelectedButton, Editor.SelectedExtraCode);

    /// <summary>
    /// The daemon snapshot's device the window is currently scoped to: the one the profile
    /// being edited belongs to, so the panel, its highlights and the capture all follow the
    /// device the user is actually configuring. Until a profile names one, the device the
    /// user is touching - the last to send a sample - is, and the snapshot's first is the
    /// last resort before any sample has arrived.
    /// </summary>
    private ConnectedDeviceSnapshot? CurrentDaemonDevice()
    {
        if (DaemonSnapshot is not { } snapshot || snapshot.Devices.Count == 0)
        {
            return null;
        }

        if (ActiveProfile is { } profile
            && !string.IsNullOrWhiteSpace(profile.DeviceId)
            && snapshot.Devices.FirstOrDefault(device => device.DeviceId == profile.DeviceId) is { } profileDevice)
        {
            return profileDevice;
        }

        if (_daemonInputDeviceId is { } sampled
            && snapshot.Devices.FirstOrDefault(device => device.DeviceId == sampled) is { } lastSampled)
        {
            return lastSampled;
        }

        return snapshot.Devices[0];
    }

    /// <summary>
    /// The device the window is scoped to - the one the capture leases, the one the capture
    /// answers, and the one the generic panel shows. Locally that is the active controller;
    /// in daemon mode a render process never sees the pad, so it is the snapshot's device
    /// for the profile in hand.
    /// </summary>
    private string? ScopedDeviceId()
        => _daemon is null
            ? ActiveController?.Descriptor.DeviceId
            : CurrentDaemonDevice()?.DeviceId;

    /// <summary>
    /// Rewrites one stick's mappings between the continuous axis and the four directions.
    /// The user picks the mode on the panel; nothing about the device or the model picks it,
    /// and an axis resting near an edge never switches anything on its own.
    /// <para>
    /// Four-way keeps every direction mapping the profile already carries - a direction the
    /// user configured is never overwritten - and gives the directions that have none the
    /// default directional key, because a stick driving a digital target must say which
    /// deflection presses it. Continuous collapses the four into one omnidirectional
    /// mapping, keeping a continuous target the directions already carried - mouse move
    /// with the settings tuned on it - and otherwise the canonical default, which is what a
    /// fresh stick mapping is.
    /// </para>
    /// </summary>
    internal async Task<bool> SetStickDirectionMode(GamepadStick stick, GenericStickMode mode)
    {
        if (ActiveProfile is not { } profile)
        {
            return false;
        }

        if (IsEditing)
        {
            // The editor holds an unconfirmed draft of this profile. Converting the mode
            // rewrites the mappings under it, and committing the draft afterwards would
            // silently overwrite the conversion - so the switch waits for the user to
            // confirm or cancel what is open, and the panel's selector rolls back.
            StatusMessage = "请先确定或取消当前编辑，再切换摇杆执行方式。";
            return false;
        }

        var mappings = profile.Mappings.ToList();
        var others = mappings
            .Where(entry => entry.Source is not StickBinding binding || binding.Stick != stick)
            .ToList();
        var directional = mappings
            .Where(entry => entry.Source is StickBinding { Direction: { } } binding && binding.Stick == stick)
            .ToList();

        var rebuilt = new List<MappingEntry>();
        if (mode == GenericStickMode.FourWay)
        {
            foreach (var direction in new[]
                     {
                         InputDirection.Up, InputDirection.Down, InputDirection.Left, InputDirection.Right,
                     })
            {
                var existing = directional.FirstOrDefault(
                    entry => entry.Source is StickBinding { Direction: { } } binding && binding.Direction == direction);
                rebuilt.Add(existing ?? new MappingEntry
                {
                    Source = new StickBinding(stick) { Direction = direction },
                    Target = new KeyPressTarget(DefaultDirectionKey(direction)),
                });
            }
        }
        else
        {
            var carried = directional
                .Select(entry => entry.Target)
                .OfType<MouseMoveTarget>()
                .FirstOrDefault();
            rebuilt.Add(new MappingEntry
            {
                Source = new StickBinding(stick),
                Target = carried ?? new MouseMoveTarget(new MouseStickSettings()),
            });
        }

        others.AddRange(rebuilt);
        return await ApplyProfile(profile with { Mappings = others });
    }

    private static GamepadKey DefaultDirectionKey(InputDirection direction) => direction switch
    {
        InputDirection.Up => GamepadKey.ArrowUp,
        InputDirection.Down => GamepadKey.ArrowDown,
        InputDirection.Left => GamepadKey.ArrowLeft,
        InputDirection.Right => GamepadKey.ArrowRight,
        _ => GamepadKey.ArrowUp,
    };

    private void RefreshMappings()
    {
        Mappings.Clear();

        // One analysis serves every surface: the rows carry the collisions they are involved
        // in, and the 全局特殊键 area carries the ones on each global key's buttons.
        var conflicts = ActiveProfile is { } profile
            ? SpecialKeyConflicts.Analyze(profile.Mappings)
            : [];

        if (ActiveProfile is { } active)
        {
            foreach (var entry in active.Mappings)
            {
                Mappings.Add(new MappingRowViewModel(
                    entry,
                    row => EditMapping(row),
                    row => { _ = DeleteMapping(row); },
                    ToggleMappingEnabled,
                    SpecialKeyConflicts.Involving(conflicts, entry.Id)));
            }
        }

        MappingCards.Clear();
        foreach (var group in Mappings.GroupBy(row => PhysicalSourceKey(row.Entry.Source)))
        {
            var rows = group
                .OrderBy(row => row.Entry.Layer == 0 ? 0 : 1)
                .ToArray();
            MappingCards.Add(new MappingCardViewModel(rows[0].Entry.Source, rows));
        }

        RefreshSpecialKeys(conflicts);
    }

    /// <summary>
    /// Re-lists 精准键, Leader 键 and 屏幕键盘键 above the mapping list, in that order.
    /// A kind the device configures names its trigger button
    /// and how it engages; one it does not still takes its row, so the area says where the
    /// key would live rather than looking like a feature that is missing. Every collision of
    /// the kind rides along, whatever button carries it.
    /// </summary>
    private void RefreshSpecialKeys(IReadOnlyList<SpecialKeyConflict> conflicts)
    {
        IReadOnlyList<MappingEntry> mappings = ActiveProfile?.Mappings ?? [];

        GlobalSpecialKeyItem ItemFor(SpecialKeyKind kind) => new(
            kind,
            SpecialKeyConflicts.EntryFor(mappings, kind),
            conflicts.Where(conflict => conflict.Kind == kind).ToArray());

        var items = new[]
        {
            ItemFor(SpecialKeyKind.Precision),
            ItemFor(SpecialKeyKind.Leader),
            ItemFor(SpecialKeyKind.Keyboard),
        };

        SpecialKeys.Clear();
        foreach (var item in items)
        {
            SpecialKeys.Add(item);
        }
    }
}

/// <summary>
/// One row of the dynamic preset selector: the slot number, its current stored name,
/// activity and delete confirmation. The view model keeps those fields in step with the store.
/// </summary>
public sealed partial class PresetSlotItem : ObservableObject
{
    public PresetSlotItem(int slot, string name)
    {
        Slot = slot;
        Name = name;
    }

    public int Slot { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>
    /// Whether this slot offers a delete affordance. Slot 1 is the device's own profile and
    /// the anchor the rest of the system reads from, so it is never deletable; every later
    /// slot is.
    /// </summary>
    public bool CanDelete => Slot > PresetStore.MinSlot;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// True while this card's delete is armed for a second, confirming click - the
    /// anti-mistouch half of a destructive action. Kept in step by the view model exactly
    /// like <see cref="IsActive"/>: at most one slot is armed at a time.
    /// </summary>
    [ObservableProperty]
    private bool _isDeleteArmed;

    /// <summary>The delete badge's tooltip: the confirm wording while armed, the plain prompt otherwise.</summary>
    public string DeleteTip => IsDeleteArmed ? "再次点击确认删除" : "删除此预设";

    partial void OnIsDeleteArmedChanged(bool value) => OnPropertyChanged(nameof(DeleteTip));
}

/// <summary>
/// One special key as the 按键配置 top area shows it: 精准键, Leader 键 or 屏幕键盘键,
/// the button that triggers it, how it engages, and every collision on that kind's buttons.
/// Precision and leader keys are device-level configuration shared across presets; the screen
/// keyboard is listed beside them for quick access. Entries are looked for by kind,
/// not hunted for inside one preset's rows.
/// <para>
/// A kind the device does not configure still takes its row: the area is where the user
/// looks for the key, and a row that says 未配置 with the way to configure it answers the
/// question a missing section would not. Editing stays where it always was - the mapping
/// editor - and the row's 编辑 affordance opens exactly that mapping.
/// </para>
/// </summary>
public sealed class GlobalSpecialKeyItem
{
    public GlobalSpecialKeyItem(
        SpecialKeyKind kind,
        MappingEntry? entry,
        IReadOnlyList<SpecialKeyConflict> conflicts)
    {
        Kind = kind;
        Entry = entry;
        Conflicts = conflicts;
    }

    public SpecialKeyKind Kind { get; }

    /// <summary>The mapping that carries the key, or null when this kind is not configured.</summary>
    public MappingEntry? Entry { get; }

    /// <summary>Every collision on this kind's buttons, whatever entry carries each one.</summary>
    public IReadOnlyList<SpecialKeyConflict> Conflicts { get; }

    /// <summary>True when the device configures this kind at all.</summary>
    public bool IsConfigured => Entry is not null;

    /// <summary>The configured mapping's id, for the 编辑 affordance; null when unconfigured.</summary>
    public Guid? MappingId => Entry?.Id;

    /// <summary>What the kind is called, the way its own picker calls it.</summary>
    public string Title => Kind switch
    {
        SpecialKeyKind.Leader => "Leader 键",
        SpecialKeyKind.Keyboard => "屏幕键盘键",
        _ => "精准键",
    };

    /// <summary>
    /// What the key does today: the trigger button, the gesture position it sits on when it
    /// is not the click, and how it engages - 未配置 when the device carries none.
    /// </summary>
    public string Summary => Entry is { } entry ? Describe(entry) : "未配置";

    /// <summary>
    /// How to configure the kind when it is not configured yet: the way the mapping editor
    /// reaches it, stated beside the empty row so the next step is never guesswork. The
    /// summary line above already says 未配置, so this starts at the doing.
    /// </summary>
    public string Guide => $"在手柄模型上点击一个按键，把目标类型设为「特殊键」、子类型选「{Title}」即可。";

    /// <summary>True while this kind's buttons carry a collision.</summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>The collisions as one wrapped paragraph; hints only, never an automatic fix.</summary>
    public string? ConflictHint => Conflicts.Count > 0
        ? string.Join(" ", Conflicts.Select(conflict => conflict.Message))
        : null;

    /// <summary>
    /// The summary of one configured key. The position is named only when the key is not the
    /// click, because the click is the ordinary case and naming it would be noise; the engage
    /// wording is the picker's own, so the area and the editor never name a mode differently.
    /// </summary>
    private static string Describe(MappingEntry entry)
    {
        var target = SpecialKeyConflicts.KeyOf(entry);
        if (target is null)
        {
            return "未配置";
        }

        var position = PositionOf(entry);
        var source = MappingDescriptions.Source(entry.Source);
        var engage = target.Kind switch
        {
            SpecialKeyKind.Leader when target.Action == LeaderAction.Layer
                => $"{MappingDescriptions.LeaderSwitchModeLabel(target.SwitchMode)}：临时层",
            SpecialKeyKind.Leader
                => $"{MappingDescriptions.LeaderSwitchModeLabel(target.SwitchMode)}：预设 {target.SlotA} ↔ {target.SlotB}",
            SpecialKeyKind.Keyboard => "切换屏幕键盘",
            _ => MappingDescriptions.PrecisionModeLabel(target.PrecisionMode),
        };
        var disabled = entry.Enabled ? string.Empty : "已禁用 · ";

        return position is null
            ? $"{disabled}{source} · {engage}"
            : $"{disabled}{source} · {position} · {engage}";
    }

    /// <summary>
    /// The gesture position the key sits on, or null when it is the mapping's own click. The
    /// verdict is the analyzer's own, so the summary and the conflict wording can never
    /// disagree about which gesture carries the key. A key the engine would never engage
    /// still reads as configured here; that verdict is the conflict hint's to give.
    /// </summary>
    private static string? PositionOf(MappingEntry entry) => SpecialKeyConflicts.TriggerOf(entry) switch
    {
        SpecialKeyTrigger.DoubleClick => "双击",
        SpecialKeyTrigger.LongPress => "长按",
        _ => null,
    };
}
