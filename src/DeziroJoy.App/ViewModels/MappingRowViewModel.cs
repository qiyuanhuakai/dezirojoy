using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>One row of the mapping table.</summary>
/// <remarks>
/// A row also reports the collisions its button carries: the 精准键 and Leader 键 are
/// device-level configuration, so when one of them shares this row's button with a setting
/// that fights it, the row says so - the same verdict the 全局特殊键 area and the editor
/// show, read from the same analysis.
/// </remarks>
public partial class MappingRowViewModel : ObservableObject
{
    private readonly Action<MappingRowViewModel> _edit;

    private readonly Action<MappingRowViewModel> _delete;

    private readonly Action<MappingRowViewModel, bool> _toggleEnabled;

    public MappingRowViewModel(
        MappingEntry entry,
        Action<MappingRowViewModel> edit,
        Action<MappingRowViewModel> delete,
        Action<MappingRowViewModel, bool> toggleEnabled,
        IReadOnlyList<SpecialKeyConflict> conflicts)
    {
        Entry = entry;
        _edit = edit;
        _delete = delete;
        _toggleEnabled = toggleEnabled;
        _isEnabled = entry.Enabled;
        Conflicts = conflicts;
    }

    public MappingEntry Entry { get; }

    /// <summary>
    /// Every collision this row's button carries that involves this row - a global special
    /// key its gestures would delay, another mapping that fires beside it. Empty for the
    /// overwhelming majority of rows, which is why the hint line stays collapsed.
    /// </summary>
    public IReadOnlyList<SpecialKeyConflict> Conflicts { get; }

    /// <summary>True while this row is involved in a special-key collision.</summary>
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>
    /// The collisions as one wrapped paragraph, each sentence naming the setting, the global
    /// key it collides with and what the collision costs. Hints only - the row never edits
    /// the configuration on the user's behalf.
    /// </summary>
    public string? ConflictHint => Conflicts.Count > 0
        ? string.Join(" ", Conflicts.Select(conflict => conflict.Message))
        : null;

    public string SourceText => MappingDescriptions.Source(Entry.Source);

    public string TargetText => MappingDescriptions.Target(Entry.Target);

    /// <summary>
    /// The 单击 line of the row's summary. The click is the mapping's own target, so it has
    /// no off state of its own: only the mapping's enable flag and an explicit 无功能
    /// target change what this line says. A v1 profile that still carries a complex shortcut
    /// as the mapping's own target reads as the program here, because that is what the
    /// mapping's target is.
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
    /// long-press that never fires. A complex shortcut is the long press's own action, so
    /// this line names the program; a v1 profile that still carries it as the mapping's own
    /// target names it here too, because the long press is the only place the program ever
    /// ran.
    /// </summary>
    public string LongPressText => Gesture is { HasEffectiveLongPress: true } gesture
        ? Entry.Target is ComplexShortcutTarget legacy
            ? MappingDescriptions.ComplexShortcut(legacy)
            : Describe(gesture.LongPressAction)
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
