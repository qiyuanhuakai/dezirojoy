using System.Collections.ObjectModel;
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

    private MappingEntry? _editingEntry;

    /// <summary>
    /// Per-axis deflection at which a stick counts as pushed for navigation. Half travel
    /// matches the engine's own digital threshold: a resting stick never reaches it and a
    /// deliberate push always does.
    /// </summary>
    private const float StickNavigationThreshold = 0.5f;

    private string? _navigationDeviceId;

    private GamepadButtonMask _navigationButtons;

    private readonly SortedSet<int> _navigationExtra = [];

    private bool _navigationLeftDeflected;

    private bool _navigationRightDeflected;

    public MainWindowViewModel(
        EngineHost host,
        IProfileStore profileStore,
        IElevationService? elevationService = null,
        DaemonController? daemon = null,
        bool daemonRequired = false,
        ThemeService? settings = null)
    {
        _host = host;
        _daemon = daemon;
        _daemonRequired = daemonRequired || daemon is not null;
        _settings = settings;
        _uiContext = SynchronizationContext.Current;
        _profileStore = profileStore;
        _presets = new PresetStore(_profileStore);
        _elevationService = elevationService ?? new ElevationService();
        Editor = new MappingEditorViewModel();
        MouseSensitivity = new MouseSensitivityViewModel();
        ScrollSensitivity = new ScrollSensitivityViewModel();
        Visualizer = new GamepadVisualizerViewModel();
        Appearance = new AppearanceViewModel();
        DaemonSettings = new DaemonSettingsViewModel();

        // Fixed for the lifetime of the process: elevation is decided at launch.
        IsElevationSupported = _elevationService.IsSupported;
        IsElevated = _elevationService.IsElevated;

        if (_daemon is not null)
        {
            _daemon.SnapshotChanged += OnDaemonSnapshot;
            _daemon.Disconnected += OnDaemonDisconnected;
            if (_daemon.Snapshot is { } snapshot)
            {
                OnDaemonSnapshot(snapshot);
            }
        }

        _host.PresetSwitchRequested += OnHostPresetSwitchRequested;

        RefreshStatus();
    }

    public ObservableCollection<ControllerRuntime> Controllers { get; } = [];

    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];

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

    [ObservableProperty]
    private ControllerRuntime? _activeController;

    [ObservableProperty]
    private GamepadProfile? _activeProfile;

    [ObservableProperty]
    private MappingRowViewModel? _selectedMapping;

    /// <summary>
    /// First-level selection. Selecting deliberately does NOT open the editor: browsing the
    /// pad must never suspend injection. Only entering the editor hijacks the pad.
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
    /// Leaving scope stops capture, forgets the held edges and releases this connection's
    /// editor suspension lease; reentry resets the edges again and restores the lease when a
    /// draft is still open on the mapping tab.
    /// </summary>
    public void SetPhysicalInputScope(bool inScope) => IsPhysicalInputInScope = inScope;

    /// <summary>
    /// Whether gamepad presses may drive the UI at all. The setting lives in the persisted
    /// app preferences and defaults to on, so a view model built without them behaves as
    /// before.
    /// </summary>
    public bool GamepadNavigationEnabled
    {
        get => _settings?.GamepadNavigationEnabled ?? true;
        set => SetGamepadNavigationEnabled(value);
    }

    /// <summary>Turns gamepad-driven navigation off or on and persists the choice.</summary>
    public void SetGamepadNavigationEnabled(bool enabled) => _settings?.SetGamepadNavigation(enabled);

    /// <summary>Which of the two leader profiles the engine is currently running.</summary>
    [ObservableProperty]
    private LeaderSlot _activeLeaderSlot = LeaderSlot.Primary;

    public string LeaderSlotLabel => ActiveLeaderSlot == LeaderSlot.Alternate
        ? "当前生效：第二套配置"
        : "当前生效：主配置";

    partial void OnActiveLeaderSlotChanged(LeaderSlot value)
        => OnPropertyChanged(nameof(LeaderSlotLabel));

    /// <summary>Leader-key choices, starting with "not configured".</summary>
    public IReadOnlyList<GamepadButton?> LeaderButtonOptions { get; } =
        [null, .. Enum.GetValues<GamepadButton>().Where(button => button != GamepadButton.Extra)];

    /// <summary>The configured leader button, or null when the leader key is off.</summary>
    public GamepadButton? LeaderButton
    {
        get => _settings?.LeaderButton;
        set => _settings?.SetLeader(value, LeaderMode);
    }

    /// <summary>Whether the leader key swaps on a press or only while held.</summary>
    public LeaderMode LeaderMode
    {
        get => _settings?.LeaderMode ?? LeaderMode.Press;
        set => _settings?.SetLeader(LeaderButton, value);
    }

    public IReadOnlyList<LeaderModeOption> LeaderModeOptions { get; } =
        [new(LeaderMode.Press, "按下切换"), new(LeaderMode.Hold, "按住切换")];

    /// <summary>Stores the profile in force right now as the leader alternate.</summary>
    [RelayCommand]
    private void SaveCurrentAsLeaderAlternate()
    {
        if (ActiveProfile is { } profile)
        {
            SaveLeaderAlternate(profile);
        }
    }

    /// <summary>
    /// Saves <paramref name="profile"/> as the alternate the leader key swaps to. It is stored
    /// beside the device's own profile under a derived key, so the two never collide and the
    /// alternate survives a restart; the real device id is restored when it is applied.
    /// </summary>
    public void SaveLeaderAlternate(GamepadProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _profileStore.Save(profile with { DeviceId = LeaderKeyFor(profile.DeviceId) });
    }

    private static string LeaderKeyFor(string? deviceId)
        => string.IsNullOrEmpty(deviceId) ? "leader-alternate" : $"leader-alternate:{deviceId}";

    private void ApplyLeader(IGamepadState state)
    {
        var leader = _settings?.LeaderButton;

        if (leader is not GamepadButton configured)
        {
            return;
        }

        if (_settings!.LeaderMode == LeaderMode.Hold)
        {
            var wanted = state.PressedButtons.Has(configured) ? LeaderSlot.Alternate : LeaderSlot.Primary;

            if (wanted != ActiveLeaderSlot)
            {
                _ = SwapLeaderSlotAsync(wanted);
            }

            return;
        }

        var newlyPressed = state.PressedButtons & ~_navigationButtons;

        if (newlyPressed.Has(configured))
        {
            _ = SwapLeaderSlotAsync(ActiveLeaderSlot == LeaderSlot.Primary ? LeaderSlot.Alternate : LeaderSlot.Primary);
        }
    }

    private async Task SwapLeaderSlotAsync(LeaderSlot target)
    {
        if (ActiveController is null)
        {
            return;
        }

        var deviceId = ActiveController.Descriptor.DeviceId;
        var profile = target == LeaderSlot.Alternate
            ? _profileStore.Load(LeaderKeyFor(deviceId))
            : _profileStore.Load(deviceId);

        if (profile is null)
        {
            return;
        }

        if (await ApplyProfile(profile with { DeviceId = deviceId }))
        {
            ActiveLeaderSlot = target;
        }
    }

    /// <summary>
    /// The button counterpart of pressing the leader key: swaps between the device's own
    /// profile and the stored alternate, so both configurations are reachable from the
    /// mapping page without a pad in hand.
    /// </summary>
    [RelayCommand]
    private Task ToggleLeaderSlot()
        => SwapLeaderSlotAsync(ActiveLeaderSlot == LeaderSlot.Primary ? LeaderSlot.Alternate : LeaderSlot.Primary);

    /// <summary>Which preset slot (1-4) is active for the device in scope.</summary>
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
        if (_daemonRequired && !IsDaemonConnected)
        {
            SetTransferError("提权守护进程不可用；配置槽位未切换。");
            return;
        }

        if (_daemon is not null)
        {
            if (ActiveController is not { } controller)
            {
                return;
            }

            try
            {
                var result = await _daemon.SwitchPresetSlotAsync(
                    controller.Descriptor.DeviceId, slot, _lifetime.Token);
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
        if (ActiveController is not { } controller)
        {
            return;
        }

        if (_presets.LoadSlot(controller.Descriptor.DeviceId, slot) is not { } profile)
        {
            return;
        }

        ApplyProfileLocally(profile);
        ActivePresetSlot = slot;
    }

    partial void OnIsPhysicalInputInScopeChanged(bool value)
    {
        if (!value)
        {
            // Leaving scope: an armed capture must stand down, and the held edges are
            // forgotten so a button held across the boundary is never read as a press on
            // reentry.
            Editor.EndKeyboardCapture();
            Editor.IsCapturingButton = false;
            ResetNavigationBaseline();
        }

        // The editor lease is this connection's: releasing it here is what lets the daemon
        // (or the local engine) inject again while no window is presenting, and taking it
        // back on reentry keeps the retained draft hijacking the pad. A discarded draft
        // never re-arms it.
        ApplyEditorLease();
    }

    /// <summary>Forgets the navigation baseline so the next sample starts from a clean slate.</summary>
    private void ResetNavigationBaseline()
    {
        _navigationDeviceId = null;
        _navigationButtons = default;
        _navigationExtra.Clear();
        _navigationLeftDeflected = false;
        _navigationRightDeflected = false;
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

    partial void OnIsEditingChanged(bool value)
    {
        // The editor hijacks the pad: while a mapping is open, physical presses navigate the
        // editor instead of being injected, so the engine must not dispatch. Sampling continues
        // either way - navigation works with the engine running or stopped.
        ApplyEditorLease();
    }

    /// <summary>
    /// Takes or releases this connection's editor suspension lease. The lease is held exactly
    /// while a draft is open AND physical input is in scope, so leaving the mapping tab,
    /// deactivating the window or destroying it hands the pad straight back to the injector -
    /// whichever side owns it, the resident daemon or the local engine on Linux. Transitions
    /// go through the daemon's own mutation gate, so they are serialized with every other
    /// mutation this connection makes.
    /// </summary>
    private void ApplyEditorLease()
    {
        var suspended = IsEditing && IsPhysicalInputInScope;

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
            ReportMutationFailure(result, "编辑器输入挂起");
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
        Editor.Reset();
        _editingEntry = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void EditMapping(MappingRowViewModel row)
    {
        Editor.LoadFrom(row.Entry);
        _editingEntry = row.Entry;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task CommitEditor()
    {
        if (ActiveProfile is null)
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
        if (ActiveController is null || ActiveProfile is null)
        {
            return;
        }

        _profileStore.Save(ActiveProfile);
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

        if (ActiveController is null)
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

        var imported = result.Profile with { DeviceId = ActiveController.Descriptor.DeviceId };

        _ = ApplyImportedProfile(imported, result);
    }

    private async Task ApplyImportedProfile(GamepadProfile imported, ProfileTransferResult result)
    {
        if (await ApplyProfile(imported))
        {
            _profileStore.Save(imported);
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
        Navigate(args.State);

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
        Navigate(args.State);

        if (ActiveController is null)
        {
            return;
        }

        Visualizer.Update(args.State);
        Visualizer.SetConnected(true);
    }

    /// <summary>Selects an element without opening the editor, so injection is never suspended.</summary>
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
    private void Navigate(IGamepadState state)
    {
        // With the setting off the pad stops driving the UI entirely: it still highlights the
        // model and still feeds the mapping editor, but it never navigates and never opens
        // the editor on its own.
        if (!IsPhysicalInputInScope || !GamepadNavigationEnabled)
        {
            // Out of scope the pad belongs to the injector, not to this window: nothing is
            // selected and nothing is captured. The editor's capture baseline is still
            // tracked, so arming capture after the user comes back never fires on a button
            // they are already holding.
            Editor.CaptureGamepadSample(state);
            return;
        }

        ApplyLeader(state);

        if (IsEditing)
        {
            // Keyboard capture owns the input while it is armed. A gamepad button is not a
            // keyboard key, so it cannot set the target; and navigating away would abort the
            // capture the user just started, so the press is swallowed.
            if (Editor.IsCapturingKey)
            {
                Editor.CaptureGamepadSample(state);
                RememberNavigation(state);
                return;
            }

            // Button capture is armed: the press becomes the mapping's source.
            if (Editor.CaptureGamepadSample(state))
            {
                RememberNavigation(state);
                return;
            }
        }

        NavigateByPhysicalInput(state);
    }

    /// <summary>
    /// Handles a newly pressed button or deflected stick. While the editor is open (second
    /// level) the press switches which element is being configured, and injection stays
    /// suspended - that is the hijack, and it is scoped to the editor. Otherwise the press
    /// only selects the element in the model (first level): it never opens the editor, so the
    /// mapping keeps injecting while the user browses the pad. Only press edges count, so a
    /// held button acts once; the first sample for a controller is the baseline, so a button
    /// held since before the app looked is not a press.
    /// </summary>
    private void NavigateByPhysicalInput(IGamepadState state)
    {
        var deviceId = ActiveController?.Descriptor.DeviceId;

        if (deviceId is null)
        {
            // No pad: forget the baseline so the next connection starts from a clean slate.
            _navigationDeviceId = null;
            return;
        }

        if (!string.Equals(deviceId, _navigationDeviceId, StringComparison.Ordinal))
        {
            RememberNavigation(state, deviceId);
            return;
        }

        var buttonEdges = state.PressedButtons & ~_navigationButtons;
        var extraEdges = state.ExtraButtons.Where(code => !_navigationExtra.Contains(code)).ToList();
        var leftEdge = IsDeflected(state.LeftStickX, state.LeftStickY) && !_navigationLeftDeflected;
        var rightEdge = IsDeflected(state.RightStickX, state.RightStickY) && !_navigationRightDeflected;

        RememberNavigation(state, deviceId);

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

    /// <summary>Records the current sample as the navigation baseline.</summary>
    private void RememberNavigation(IGamepadState state, string? deviceId = null)
    {
        _navigationDeviceId = deviceId ?? ActiveController?.Descriptor.DeviceId;
        _navigationButtons = state.PressedButtons;

        _navigationExtra.Clear();
        foreach (var code in state.ExtraButtons)
        {
            _navigationExtra.Add(code);
        }

        _navigationLeftDeflected = IsDeflected(state.LeftStickX, state.LeftStickY);
        _navigationRightDeflected = IsDeflected(state.RightStickX, state.RightStickY);
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
            // A pad that was here and is gone now is not the same as one never plugged in: the
            // wireless idle timer fires silently after 15 idle minutes and no public API can
            // prevent it, so "connect a gamepad" would be misleading for a pad that is asleep.
            var wasConnected = ActiveController is not null;

            if (wasConnected)
            {
                ActiveController = null;
                ActiveProfile = null;
                Mappings.Clear();
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

        if (_daemon is null && ActiveController is not null)
        {
            _host.Reload(ActiveController.Descriptor.DeviceId, updated);
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
        MappingOwner = snapshot.Running ? MappingOwner.ResidentDaemon : MappingOwner.None;
        AdoptDaemonProfile(snapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            SetTransferError(snapshot.LastError);
        }

        RefreshStatus();
    }

    private void AdoptDaemonProfile(StateSnapshot snapshot)
    {
        if (IsEditing)
        {
            return;
        }

        var instanceId = _daemon?.DaemonInstanceId;
        if (snapshot.Revision == _profileBaselineRevision && instanceId == _profileDaemonInstanceId)
        {
            return;
        }

        GamepadProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(snapshot.ProfileJson))
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

        _profileBaselineRevision = snapshot.Revision;
        _profileDaemonInstanceId = instanceId;
        if (profile is not null)
        {
            ApplyProfileLocally(profile);
        }
    }

    private void OnDaemonDisconnected()
    {
        if (_uiContext is not null && !ReferenceEquals(SynchronizationContext.Current, _uiContext))
        {
            _uiContext.Post(_ => OnDaemonDisconnected(), null);
            return;
        }

        DaemonSnapshot = null;
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
            if (IsEditing && IsPhysicalInputInScope)
            {
                // Reconnect lease recovery: the draft survived the disconnect, so the lease
                // is taken back - but only while the pad is still in scope.
                await SetEditorInjectionSuspended(true);
            }

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
        await DisconnectDaemonAsync();
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

        if (ActiveProfile is null)
        {
            return;
        }

        foreach (var entry in ActiveProfile.Mappings)
        {
            Mappings.Add(new MappingRowViewModel(entry, row => EditMapping(row), row => { _ = DeleteMapping(row); }, ToggleMappingEnabled));
        }
    }
}
