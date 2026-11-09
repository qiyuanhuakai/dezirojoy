using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One category of the target-key picker: its name, its keys and whether the grid currently
/// shows it. The keys are the very same <see cref="KeyPickerItem"/> instances the flat
/// <see cref="MappingEditorViewModel.KeyOptions"/> list carries, so the grid and the lookup can
/// never disagree about what a key is.
/// </summary>
/// <remarks>
/// <para>
/// The picker shows one category at a time behind a category navigator, so a group carries the
/// selected state its navigator segment binds to. The flag is the group's own rather than the
/// view model's: the navigator is an <c>ItemsControl</c> over these groups, and a segment writes
/// its check straight back here - the same two-way idiom the gesture switcher's tabs use.
/// </para>
/// <para>
/// The view model owns the authoritative selection (<see cref="MappingEditorViewModel.ActiveKeyCategory"/>)
/// and mirrors it onto these flags, so a programmatic move - a gesture switch landing on a key
/// in another category - checks the matching segment exactly as a click does.
/// </para>
/// </remarks>
public sealed class KeyCategoryGroup : INotifyPropertyChanged
{
    private bool _isActive;

    public KeyCategoryGroup(string name)
    {
        Name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public ObservableCollection<KeyPickerItem> Keys { get; } = [];

    /// <summary>Whether the key grid currently shows this category's keys.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
}
