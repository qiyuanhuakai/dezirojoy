using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>One row of the mapping table.</summary>
public partial class MappingRowViewModel : ObservableObject
{
    private readonly Action<MappingRowViewModel> _edit;

    private readonly Action<MappingRowViewModel> _delete;

    private readonly Action<MappingRowViewModel, bool> _toggleEnabled;

    public MappingRowViewModel(
        MappingEntry entry,
        Action<MappingRowViewModel> edit,
        Action<MappingRowViewModel> delete,
        Action<MappingRowViewModel, bool> toggleEnabled)
    {
        Entry = entry;
        _edit = edit;
        _delete = delete;
        _toggleEnabled = toggleEnabled;
        _isEnabled = entry.Enabled;
    }

    public MappingEntry Entry { get; }

    public string SourceText => MappingDescriptions.Source(Entry.Source);

    public string TargetText => MappingDescriptions.Target(Entry.Target);

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _toggleEnabled(this, value);

    [RelayCommand]
    private void Edit() => _edit(this);

    [RelayCommand]
    private void Delete() => _delete(this);
}
