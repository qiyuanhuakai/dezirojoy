using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using DeziroJoy.Core.Profiles;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Main window orchestration: connection state, profile editing, mapping table and
 /// the sensitivity panels. Avalonia-free so the whole flow is unit-testable; the view
/// only marshals host events onto the UI thread and calls <see cref="ApplySample"/>.
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly EngineHost _host;

    private readonly IProfileStore _profileStore;

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

    public MainWindowViewModel(EngineHost host, IProfileStore profileStore, IElevationService? elevationService = null)
    {
        _host = host;
        _profileStore = profileStore;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElevationStatusText))]
    [NotifyPropertyChangedFor(nameof(CanRelaunchElevated))]
    private bool _isElevationSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElevationStatusText))]
    [NotifyPropertyChangedFor(nameof(CanRelaunchElevated))]
    private bool _isElevated;

    /// <summary>
    /// True when a relaunch would actually change anything: the platform cares about
    /// integrity levels and this process is not elevated yet.
    /// </summary>
    public bool CanRelaunchElevated => IsElevationSupported && !IsElevated;

    /// <summary>
    /// Why elevated targets are or are not reachable. SendInput is subject to UIPI and
    /// fails silently when blocked, so this is the only place the user can tell
    /// "UIPI blocked me" from "it worked".
    /// </summary>
    public string ElevationStatusText => IsElevationSupported
        ? IsElevated
            ? "已以管理员身份运行：可向管理员权限的应用注入输入"
            : "未提权：无法向以管理员身份运行的应用注入输入（UIPI 会静默拦截）"
        : "当前平台不受 UIPI 限制，无需提权";

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
        _host.SetInjectionSuspended(value);
    }

    /// <summary>
    /// Relaunches the app elevated through the shell "runas" verb (one UAC prompt). The
    /// manifest stays at asInvoker, so this is the only thing that ever elevates.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRelaunchElevated))]
    private void RelaunchElevated()
    {
        if (!_elevationService.RelaunchElevated())
        {
            // The UAC prompt was declined; keep running unelevated.
            return;
        }

        ElevationRelaunchRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleEngine()
    {
        if (IsEngineRunning)
        {
            _host.Stop();
            IsEngineRunning = false;
        }
        else
        {
            _host.Start(TimeSpan.FromMilliseconds(2));
            IsEngineRunning = true;
        }

        RefreshStatus();
    }

    [RelayCommand]
    private void ToggleMapping()
    {
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
    private void CommitEditor()
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

        ApplyProfile(ActiveProfile with { Mappings = mappings });
        IsEditing = false;
        _editingEntry = null;
    }

    [RelayCommand]
    private void CancelEditor()
    {
        IsEditing = false;
        _editingEntry = null;
    }

    [RelayCommand]
    private void DeleteMapping(MappingRowViewModel row)
    {
        if (ActiveProfile is null)
        {
            return;
        }

        var mappings = ActiveProfile.Mappings.Where(m => m.Id != row.Entry.Id).ToList();
        ApplyProfile(ActiveProfile with { Mappings = mappings });
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

        ApplyProfile(ActiveProfile with { Mappings = mappings });
    }

    [RelayCommand]
    private void ApplyMouseSensitivity()
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

        ApplyProfile(ActiveProfile with { Mappings = mappings });
    }

    [RelayCommand]
    private void ApplyScrollSensitivity()
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

        ApplyProfile(ActiveProfile with { Mappings = mappings });
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
    private void ResetToDefault()
    {
        if (ActiveController is null)
        {
            return;
        }

        var fresh = DefaultProfileLibrary.CreateDefault(ActiveController.Descriptor);
        ApplyProfile(fresh);
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

        ApplyProfile(imported);
        _profileStore.Save(imported);
        Report(result);
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
        SelectedMapping = Mappings.FirstOrDefault(row => row.Entry.Source == source);
    }

    /// <summary>
    /// Opens the mapping settings for one source, exactly as clicking that element on the model
    /// does: an element that already has a mapping loads it (so confirming replaces it instead
    /// of adding a second entry for the same source), and a fresh one is armed anew. The
    /// matching row is selected so the list side of the link highlights too; an element with no
    /// mapping selects nothing.
    /// </summary>
    public void OpenEditorForSource(InputBinding source)
    {
        SelectedSource = source;
        SelectedMapping = Mappings.FirstOrDefault(row => row.Entry.Source == source);

        if (ActiveProfile?.Mappings.FirstOrDefault(m => m.Source == source) is { } existing
            && Mappings.FirstOrDefault(row => row.Entry == existing) is { } row)
        {
            EditMapping(row);
            return;
        }

        AddMapping();
        Editor.LoadSource(source);
    }

    /// <summary>
    /// Routes one gamepad sample to the editor's capture and to navigation, in that order, so
    /// the two never fight over the same press: a press consumed by capture must not also
    /// navigate.
    /// </summary>
    private void Navigate(IGamepadState state)
    {
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
            if (ActiveController is not null)
            {
                ActiveController = null;
                ActiveProfile = null;
                Mappings.Clear();
                MouseSensitivity.Reset();
                ScrollSensitivity.Reset();
            }

            HasController = false;
            RefreshStatus();
            Visualizer.SetConnected(false);
            return;
        }

        if (ActiveController is null || !controllers.Contains(ActiveController))
        {
            Activate(controllers[0]);
        }

        Visualizer.SetConnected(true);
    }

    private void RefreshStatus()
    {
        if (ActiveController is null)
        {
            StatusMessage = "未检测到手柄，请连接手柄";
            return;
        }

        StatusMessage = IsEngineRunning
            ? $"{ActiveController.Descriptor.Name} · {MappingDescriptions.Layout(ActiveController.Descriptor.Layout)}"
            : $"{ActiveController.Descriptor.Name} · 映射未启动";
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

    private void ApplyProfile(GamepadProfile updated)
    {
        ActiveProfile = updated;

        if (ActiveController is not null)
        {
            _host.Reload(ActiveController.Descriptor.DeviceId, updated);
        }

        // The engine now runs the updated profile, so the calibration panels must show the
        // settings it is actually using. Without this a reset or import leaves the sliders
        // on the old values while the preview reflects the new ones.
        ReloadSensitivityPanels();
        RefreshMappings();
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
            Mappings.Add(new MappingRowViewModel(entry, row => EditMapping(row), row => DeleteMapping(row), ToggleMappingEnabled));
        }
    }
}
