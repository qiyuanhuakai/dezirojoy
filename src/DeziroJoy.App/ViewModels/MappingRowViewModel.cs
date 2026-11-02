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

    /// <summary>
    /// The 单击 line of the row's summary. The click is the mapping's own target, so it has
    /// no off state of its own: only the mapping's enable flag and an explicit 无功能
    /// target change what this line says.
    /// </summary>
    public string ClickText => !IsEnabled
        ? MappingEditorViewModel.GestureOffOption.Label
        : Describe(Entry.Target);

    /// <summary>
    /// The 双击 line. 已关闭 while the gesture is off; otherwise what the double-click
    /// emits - its own target, 无功能, or the legacy 重复单击的功能 (a null action re-fires
    /// the click, which is what every profile written before gesture actions existed has).
    /// </summary>
    public string DoubleClickText => Gesture is { HasDoubleClick: true } gesture
        ? Describe(gesture.DoubleClickAction)
        : MappingEditorViewModel.GestureOffOption.Label;

    /// <summary>
    /// The 长按 line. Click auto-repeat and the long-press are mutually exclusive and the
    /// repeat wins, so a repeating click closes this line rather than advertising a
    /// long-press that never fires.
    /// </summary>
    public string LongPressText => Gesture is { HasEffectiveLongPress: true } gesture
        ? Describe(gesture.LongPressAction)
        : MappingEditorViewModel.GestureOffOption.Label;

    /// <summary>Gestures live on the button binding; a stick carries none.</summary>
    private ButtonGesture? Gesture => Entry.Source is ButtonBinding button ? button.Gesture : null;

    /// <summary>
    /// What one slot does: a real target, the explicit 无功能, or the legacy repeat-click.
    /// The labels are the editor's own, so a row and the editor's picker can never disagree
    /// about what the same state is called.
    /// </summary>
    private static string Describe(MappingTarget? target) => target switch
    {
        null => MappingEditorViewModel.GestureRepeatOption.Label,
        NoneTarget => MappingEditorViewModel.GestureNoneOption.Label,
        var action => MappingDescriptions.Target(action),
    };

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value) => _toggleEnabled(this, value);

    [RelayCommand]
    private void Edit() => _edit(this);

    [RelayCommand]
    private void Delete() => _delete(this);
}
