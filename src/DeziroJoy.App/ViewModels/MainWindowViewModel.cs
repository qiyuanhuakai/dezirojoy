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

    private MappingEntry? _editingEntry;

    public MainWindowViewModel(EngineHost host, IProfileStore profileStore)
    {
        _host = host;
        _profileStore = profileStore;
        Editor = new MappingEditorViewModel();
        MouseSensitivity = new MouseSensitivityViewModel();
        ScrollSensitivity = new ScrollSensitivityViewModel();
        Visualizer = new GamepadVisualizerViewModel();
    }

    public ObservableCollection<ControllerRuntime> Controllers { get; } = [];

    public ObservableCollection<MappingRowViewModel> Mappings { get; } = [];

    public MappingEditorViewModel Editor { get; }

    public MouseSensitivityViewModel MouseSensitivity { get; }

    public ScrollSensitivityViewModel ScrollSensitivity { get; }

    public GamepadVisualizerViewModel Visualizer { get; }

    [ObservableProperty]
    private ControllerRuntime? _activeController;

    [ObservableProperty]
    private GamepadProfile? _activeProfile;

    [ObservableProperty]
    private MappingRowViewModel? _selectedMapping;

    [ObservableProperty]
    private string _statusMessage = "未检测到手柄，请连接手柄";

    [ObservableProperty]
    private bool _isMappingEnabled = true;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _hasController;

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

    /// <summary>Called from the UI thread for every host sample.</summary>
    public void ApplySample(ControllerSampledEventArgs args)
    {
        SyncControllers();

        if (ActiveController is null)
        {
            return;
        }

        Visualizer.Update(args.State);
        Visualizer.SetConnected(true);
    }

    /// <summary>Called from the UI thread when controllers connect or disconnect.</summary>
    public void SyncControllers()
    {
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
            StatusMessage = "未检测到手柄，请连接手柄";
            Visualizer.SetConnected(false);
            return;
        }

        if (ActiveController is null || !controllers.Contains(ActiveController))
        {
            Activate(controllers[0]);
        }
    }

    private void Activate(ControllerRuntime runtime)
    {
        ActiveController = runtime;
        ActiveProfile = runtime.Profile;
        HasController = true;
        StatusMessage = $"{runtime.Descriptor.Name} · {MappingDescriptions.Layout(runtime.Descriptor.Layout)}";

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

        RefreshMappings();
    }

    private void ApplyProfile(GamepadProfile updated)
    {
        ActiveProfile = updated;

        if (ActiveController is not null)
        {
            _host.Reload(ActiveController.Descriptor.DeviceId, updated);
        }

        RefreshMappings();
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
