using System.Collections.ObjectModel;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One category of the target-key picker: its name and its keys, in the order the grid
/// shows them. The keys are the very same <see cref="KeyPickerItem"/> instances the flat
/// <see cref="MappingEditorViewModel.KeyOptions"/> list carries, so the grid and the
/// lookup can never disagree about what a key is.
/// </summary>
public sealed class KeyCategoryGroup
{
    public KeyCategoryGroup(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ObservableCollection<KeyPickerItem> Keys { get; } = [];
}
