using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;

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
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly EngineHost _host;

    private readonly DaemonController? _daemon;

    private readonly bool _daemonRequired;

    private readonly ThemeService? _settings;

    private readonly SynchronizationContext? _uiContext;

    private readonly CancellationTokenSource _lifetime = new();

    private bool _mappingEnabledRequested = true;

    private bool _disposed;

    private readonly IProfileStore _profileStore;

    private readonly PresetStore _presets;

    private readonly IElevationService _elevationService;

    private readonly IUpdateService _updateService;

    private readonly bool _ownsUpdateService;

    /// <summary>
    /// The startup auto-check, or null when the persisted toggle is off. Kept so a test can
    /// await the fire-and-forget instead of racing it.
    /// </summary>
    private readonly Task? _startupUpdateCheck;

    private MappingEntry? _editingEntry;

    /// <summary>
    /// Per-axis deflection at which a stick counts as pushed for navigation. Half travel
    /// matches the engine's own digital threshold: a resting stick never reaches it and a
    /// deliberate push always does.
    /// </summary>
    private const float StickNavigationThreshold = 0.5f;

    private readonly Dictionary<string, GamepadState> _navigationBaselines = new(StringComparer.Ordinal);

    private string? _daemonInputDeviceId;

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
        IUpdateService? updateService = null)
    {
        _host = host;
        _daemon = daemon;
        _daemonRequired = daemonRequired || daemon is not null;
        _settings = settings;
        _uiContext = SynchronizationContext.Current;
        _profileStore = profileStore;
        _presets = new PresetStore(_profileStore);
        _elevationService = elevationService ?? new ElevationService();
        _updateService = updateService ?? new UpdateService();
        _ownsUpdateService = updateService is null;
        Editor = new MappingEditorViewModel();
        MouseSensitivity = new MouseSensitivityViewModel();
        ScrollSensitivity = new ScrollSensitivityViewModel();
        Visualizer = new GamepadVisualizerViewModel();
        Appearance = new AppearanceViewModel();
        DaemonSettings = new DaemonSettingsViewModel();
        Update = new UpdateSettingsViewModel(_updateService, _settings);

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
        }

        _host.PresetSwitchRequested += OnHostPresetSwitchRequested;

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
    /// The update surface of the settings panel: running version, manual check and the
    /// persisted startup auto-check. Shares the single <see cref="IUpdateService"/> for the
    /// lifetime of the window.
    /// </summary>
    public UpdateSettingsViewModel Update { get; }

    /// <summary>
    /// Runs the release check once at startup when the persisted toggle is on. The check is
    /// fire-and-forget: it must never delay the window, its outcome only ever lands in the
    /// settings panel, and a failure (offline, timeout, API refusal) stays there rather than
    /// interrupting startup. The returned task is faulted rather than crashing the process,
    /// and it is exposed through <see cref="StartupUpdateCheck"/> so tests can await it.
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

    /// <summary>
    /// The two global special keys as the 按键配置 top area shows them: 精准键 and Leader 键,
    /// one row each, configured or not. They are device-level configuration - shared across
    /// every preset of the device - so they lead the page above the per-preset mapping list
    /// instead of being searched for inside it, and every collision on their buttons is
    /// reported beside them. Rebuilt on every refresh, so an edit, a preset switch or a
    /// shared-key fan-out re-lists them immediately.
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

    public GamepadVisualizerViewModel Visualizer { get; }

    public AppearanceViewModel Appearance { get; }

    public DaemonSettingsViewModel DaemonSettings { get; }

    public bool IsDaemonMode => _daemonRequired;

    public bool IsDaemonConnected => _daemon?.State == DaemonConnectionState.Connected;

    public string DaemonStatusText => !_daemonRequired
        ? "本地映射引擎"
        : DaemonSnapshot is null
            ? "守护进程不可用；未启用本地注入回退"
            : $"守护进程已连接 · 修订 {DaemonSnapshot.Revision} · 序列 {DaemonSnapshot.Sequence}";

    [ObservableProperty]
    private StateSnapshot? _daemonSnapshot;

    private long _profileBaselineRevision = -1;
    private string? _profileDaemonInstanceId;
    private string? _profileBaselineDeviceId;

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
    /// Whether gamepad presses may drive the UI at all. The setting lives in the persisted
    /// app preferences and defaults to on, so a view model built without them behaves as
    /// before.
    /// </summary>
    public bool GamepadNavigationEnabled
    {
        get => _settings?.GamepadNavigationEnabled ?? _gamepadNavigationEnabled;
        set => SetGamepadNavigationEnabled(value);
    }

    /// <summary>Turns gamepad-driven navigation off or on and persists the choice.</summary>
    public void SetGamepadNavigationEnabled(bool enabled)
    {
        if (GamepadNavigationEnabled == enabled)
        {
            return;
        }

        if (_settings is null)
        {
            _gamepadNavigationEnabled = enabled;
        }
        else
        {
            _settings.SetGamepadNavigation(enabled);
        }

        if (!enabled)
        {
            Editor.EndKeyboardCapture();
            Editor.IsCapturingButton = false;
        }

        OnPropertyChanged(nameof(GamepadNavigationEnabled));
        ResetNavigationBaseline();
        ApplyInputLease();
    }

    private bool _gamepadNavigationEnabled = true;

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
            ? $"提权守护进程（PID {daemon.InjectorPid}）"
            : "提权守护进程（不可用）"
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
    /// relaunch is the fix.
    /// </summary>
    public string InjectorStatusText => _daemonRequired
        ? DaemonSnapshot is null
            ? "守护进程不可用；未启动本地注入"
            : InjectorIntegrity == InjectorIntegrityLevel.Elevated
                ? "由提权守护进程注入，可覆盖管理员目标"
                : "守护进程未提权；管理员目标仍会被 UIPI 拦截"
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
        MappingOwner.ResidentDaemon => $"提权守护进程正在注入映射（PID {DaemonSnapshot?.InjectorPid.ToString(CultureInfo.InvariantCulture) ?? "未知"}）",
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
    /// The explicit hijack preference owns this connection's input lease. An open editor
    /// never pauses injection on its own; when hijack is off every physical press follows
    /// the active mapping even while a draft is visible.
    /// </summary>
    private void ApplyInputLease()
    {
        var suspended = GamepadNavigationEnabled && IsPhysicalInputInScope;

        if (_daemon is not null)
        {
            _ = SetEditorInjectionSuspended(suspended);
        }
        else if (!_daemonRequired)
        {
            _host.SetInjectionSuspended(suspended);
        }
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
        Editor.Reset();
        _editingEntry = null;
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
        Editor.LoadFrom(row.Entry);
        _editingEntry = row.Entry;
        IsEditing = true;
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

        var entry = Editor.BuildEntry();
        var mappings = ActiveProfile.Mappings.ToList();

        if (_editingEntry is null)
        {
            mappings.Add(entry);
        }
        else
        {
            var index = mappings.FindIndex(m => m.Id == _editingEntry.Id);
            if (index >= 0)
            {
                mappings[index] = entry with { Id = _editingEntry.Id };
            }
        }

        if (await ApplyProfile(ActiveProfile with { Mappings = mappings }))
        {
            IsEditing = false;
            _editingEntry = null;
        }
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditing = false;
        _editingEntry = null;
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
                try
                {
                    var profileDeviceId = ProfileSerializer.Deserialize(snapshot.ProfileJson).DeviceId;
                    if (!string.IsNullOrWhiteSpace(profileDeviceId)
                        && (snapshot.Devices.Count == 0
                            || snapshot.Devices.Any(device => device.DeviceId == profileDeviceId)))
                    {
                        return profileDeviceId;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    return null;
                }
            }

            return snapshot.Devices.Count > 0 ? snapshot.Devices[0].DeviceId : null;
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

    /// <summary>
    /// Raised when the user asks to export. The view owns the file dialog (this view model is
    /// Avalonia-free), so it answers by calling <see cref="ExportTo"/> with the chosen path, or
    /// with null when the dialog was dismissed.
    /// </summary>
    public event EventHandler? ExportPathRequested;

    /// <summary>Import counterpart of <see cref="ExportPathRequested"/>; answered by <see cref="ImportFrom"/>.</summary>
    public event EventHandler? ImportPathRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransferMessage))]
    private string _transferMessage = string.Empty;

    /// <summary>True when <see cref="TransferMessage"/> reports a failure rather than a success.</summary>
    [ObservableProperty]
    private bool _isTransferError;

    public bool HasTransferMessage => !string.IsNullOrEmpty(TransferMessage);

    [RelayCommand]
    private void ExportProfile() => ExportPathRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ImportProfile() => ImportPathRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Writes the active profile to the path the view picked. A null path means the dialog was
    /// dismissed, which is not an error and says nothing.
    /// </summary>
    public void ExportTo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (ActiveProfile is null)
        {
            SetTransferError("导出失败：没有可导出的配置，请先连接手柄。");
            return;
        }

        Report(ProfileTransferService.Export(ActiveProfile, path));
    }

    /// <summary>
    /// Applies the profile in the file the view picked, replacing the active device's profile.
    /// The imported profile is rebound to the connected device so it takes effect immediately
    /// and survives a reconnect; a malformed file leaves the current profile untouched.
    /// </summary>
    public void ImportFrom(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (PersistenceDeviceId is not { } deviceId)
        {
            SetTransferError("导入失败：请先连接手柄。");
            return;
        }

        var result = ProfileTransferService.Import(path);

        if (!result.Succeeded || result.Profile is null)
        {
            SetTransferError(result.Message);
            return;
        }

        var imported = result.Profile with { DeviceId = deviceId };

        _ = ApplyImportedProfile(imported, result);
    }

    private async Task ApplyImportedProfile(GamepadProfile imported, ProfileTransferResult result)
    {
        // ApplyProfile persists the import to the active slot on its own (locally through
        // ApplyProfileLocally, in daemon mode through the daemon's apply), so there is no
        // second save to make here.
        if (await ApplyProfile(imported))
        {
            Report(result);
        }
    }

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
    }

    private void OnDaemonInputSample(InputSampleMessage sample)
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonInputSample(sample), null);
            return;
        }

        if (DaemonSnapshot is { } snapshot
            && !snapshot.Devices.Any(device => device.DeviceId == sample.DeviceId))
        {
            return;
        }

        _daemonInputDeviceId = sample.DeviceId;
        var state = sample.ToGamepadState();
        Navigate(state, sample.DeviceId);
        Visualizer.Update(state);
        Visualizer.SetConnected(true);
    }

    /// <summary>Selects an element without opening the editor or changing the input lease.</summary>
    public void SelectSource(InputBinding source)
    {
        SelectedSource = source;
        SelectedMapping = Mappings.FirstOrDefault(row => SamePhysicalSource(row.Entry.Source, source));
    }

    /// <summary>
    /// Opens the mapping settings for one source, exactly as clicking that element on the model
    /// does: an element that already has a mapping loads it (so confirming replaces it instead
    /// of adding a second entry for the same element), and a fresh one is armed anew. Identity
    /// is physical - see <see cref="SamePhysicalSource"/> - so an element reopens its mapping
    /// after a gesture or a direction was configured on it. The matching row is selected so
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
    }

    /// <summary>
    /// The mapping a click on one physical element reopens, or null when that element has no
    /// mapping yet. When one element carries several mappings - a stick with a direction per
    /// mapping - the row the user already selected wins; otherwise the first in profile order
    /// does, so the choice never depends on hash or dictionary order.
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

    /// <summary>
    /// Routes one gamepad sample to the editor's capture and to navigation, in that order, so
    /// the two never fight over the same press: a press consumed by capture must not also
    /// navigate. Both are scoped to a visible, active mapping tab, so out of scope the sample
    /// only feeds the visualizer.
    /// </summary>
    private void Navigate(IGamepadState state, string? deviceId)
    {
        // Off means the active mapping owns every press. Even a visible editor must not
        // capture or navigate behind the user's back; highlighting still stays live.
        if (!IsPhysicalInputInScope)
        {
            Editor.CaptureGamepadSample(state);
            return;
        }

        if (!GamepadNavigationEnabled)
        {
            return;
        }

        deviceId ??= ActiveController?.Descriptor.DeviceId ?? _daemonInputDeviceId;
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
        }

        NavigateByPhysicalInput(state, deviceId);
    }

    private bool CaptureGamepadSample(IGamepadState state, string? deviceId)
    {
        if (!Editor.IsCapturingButton || deviceId is null)
        {
            return Editor.CaptureGamepadSample(state);
        }

        var previous = _navigationBaselines.GetValueOrDefault(deviceId, GamepadState.Neutral);
        Editor.IsCapturingButton = false;
        Editor.CaptureGamepadSample(previous);
        Editor.IsCapturingButton = true;
        return Editor.CaptureGamepadSample(state);
    }

    /// <summary>
    /// Handles a newly pressed button or deflected stick while the explicit hijack lease is
    /// active. Every device has an independent baseline, so interleaved samples cannot erase
    /// another pad's edges. Each device's first sample establishes a baseline without navigating.
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
                OpenEditorForSource(source);
            }

            return;
        }

        SelectSource(source);
    }

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

        Controllers.Clear();
        foreach (var controller in controllers)
        {
            Controllers.Add(controller);
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
            StatusMessage = "提权守护进程不可用；未启用本地注入回退";
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
                : wasConnected
                    ? "手柄已断开连接，可能已进入休眠，请按手柄电源键唤醒；若仍无反应请检查连接"
                    : "未检测到手柄，请连接手柄";
            return;
        }

        if (ActiveController is null)
        {
            StatusMessage = wasConnected
                ? "手柄已断开连接，可能已进入休眠，请按手柄电源键唤醒；若仍无反应请检查连接"
                : "未检测到手柄，请连接手柄";
            return;
        }

        var device = $"{ActiveController.Descriptor.Name} · {MappingDescriptions.Layout(ActiveController.Descriptor.Layout)}";

        StatusMessage = IsEngineRunning
            ? device
            : MappingOwner == MappingOwner.AnotherProcess
                ? $"{device} · 启动被拒绝：另一个进程（常驻服务或其他 DeziroJoy 实例）正在注入映射"
                : $"{device} · 映射未启动";
    }

    private void Activate(ControllerRuntime runtime)
    {
        ActiveController = runtime;
        ActiveProfile = runtime.Profile;
        HasController = true;
        RefreshStatus();

        ReloadSensitivityPanels();
        RefreshMappings();
        MigrateLegacyLeader(runtime.Descriptor.DeviceId);
        ReconcilePresetSlots(runtime.Descriptor.DeviceId);
        MarkActivePresetSlot(ActivePresetSlot);
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
    }

    partial void OnDaemonSnapshotChanged(StateSnapshot? value)
    {
        OnPropertyChanged(nameof(DaemonStatusText));
        OnPropertyChanged(nameof(InjectorDescription));
        OnPropertyChanged(nameof(InjectorIntegrity));
        OnPropertyChanged(nameof(InjectorStatusText));
        OnPropertyChanged(nameof(MappingOwnerDescription));
        OnPropertyChanged(nameof(CanRelaunchElevated));
        RelaunchElevatedCommand.NotifyCanExecuteChanged();
    }

    private void OnDaemonSnapshot(StateSnapshot snapshot)
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonSnapshot(snapshot), null);
            return;
        }

        DaemonSnapshot = snapshot;
        IsEngineRunning = snapshot.Running;
        IsMappingEnabled = snapshot.MappingEnabled;
        _mappingEnabledRequested = snapshot.MappingEnabled;
        ActivePresetSlot = snapshot.ActivePresetSlot;
        SyncPresetSlotsForDaemon();
        MappingOwner = snapshot.Running ? MappingOwner.ResidentDaemon : MappingOwner.None;
        AdoptDaemonProfile(snapshot);
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
        RefreshStatus(wasConnected && !snapshot.HasController);
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
            try
            {
                profile = ProfileSerializer.Deserialize(snapshot.ProfileJson);
            }
            catch (System.Text.Json.JsonException exception)
            {
                SetTransferError($"守护进程配置无法解析：{exception.Message}");
                return;
            }
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

    private void OnDaemonConnected()
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonConnected(), null);
            return;
        }

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

        DaemonSnapshot = null;
        _daemonInputDeviceId = null;
        _navigationBaselines.Clear();
        IsEngineRunning = false;
        IsMappingEnabled = false;
        MappingOwner = MappingOwner.None;
        SetTransferError("守护进程连接已断开；映射状态未知，未启用本地注入回退。");
        RefreshStatus();
    }

    public void ReportDaemonUnavailable(string reason)
    {
        SetTransferError($"提权守护进程不可用：{reason}；未启用本地注入回退。");
        RefreshStatus();
    }

    [RelayCommand]
    private async Task ReconnectDaemon()
    {
        if (!_daemonRequired || _daemon is null)
        {
            return;
        }

        try
        {
            await _daemon.ConnectAsync(_lifetime.Token);
            if (_daemon.Snapshot is { } snapshot)
            {
                OnDaemonSnapshot(snapshot);
            }

            TransferMessage = string.Empty;
            IsTransferError = false;
            await SetEditorInjectionSuspended(GamepadNavigationEnabled && IsPhysicalInputInScope);

            RefreshStatus();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            ReportDaemonUnavailable(exception.Message);
        }
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
                TransferMessage = string.Empty;
                IsTransferError = false;
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

        await DisconnectDaemonAsync();

        // The default service owns an HttpClient this view model created; an injected one is
        // the caller's to dispose. This runs after the lifetime token is cancelled above, so
        // an in-flight startup check is already settling rather than racing the disposal.
        if (_ownsUpdateService && _updateService is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private void ReloadSensitivityPanels()
    {
        if (ActiveProfile is null)
        {
            return;
        }

        foreach (var mapping in ActiveProfile.Mappings)
        {
            switch (mapping.Target)
            {
                case MouseMoveTarget mouse when mapping.Source is StickBinding { Stick: GamepadStick.Right }:
                    MouseSensitivity.LoadFrom(mouse.Settings);
                    break;
                case WheelScrollTarget wheel when mapping.Source is StickBinding { Stick: GamepadStick.Left }:
                    ScrollSensitivity.LoadFrom(wheel.Settings);
                    break;
            }
        }
    }

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

        RefreshSpecialKeys(conflicts);
    }

    /// <summary>
    /// Re-lists the two global special keys above the mapping list: 精准键 and Leader 键,
    /// one row each, in that order. A kind the device configures names its trigger button
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
        };

        SpecialKeys.Clear();
        foreach (var item in items)
        {
            SpecialKeys.Add(item);
        }
    }
}

/// <summary>
/// One row of the dynamic preset selector: the slot number, the preset's stored name, and
/// whether it is the app-wide active slot. The view iterates
/// <see cref="MainWindowViewModel.PresetSlots"/> over these; <see cref="IsActive"/> is the
/// only mutable field, kept in step by the view model as the active slot moves.
/// </summary>
public sealed partial class PresetSlotItem : ObservableObject
{
    public PresetSlotItem(int slot, string name)
    {
        Slot = slot;
        Name = name;
    }

    public int Slot { get; }

    public string Name { get; }

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
/// One global special key as the 按键配置 top area shows it: 精准键 or Leader 键, the button
/// that triggers it, how it engages, and every collision on that kind's buttons. The two
/// keys are device-level configuration - shared across every preset of the device - which is
/// why the area lists them above the per-preset mapping list: they are looked for by kind,
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
    public string Title => Kind == SpecialKeyKind.Leader ? "Leader 键" : "精准键";

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
        var engage = target.Kind == SpecialKeyKind.Leader
            ? $"{MappingDescriptions.LeaderSwitchModeLabel(target.SwitchMode)}：预设 {target.SlotA} ↔ {target.SlotB}"
            : MappingDescriptions.PrecisionModeLabel(target.PrecisionMode);
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
