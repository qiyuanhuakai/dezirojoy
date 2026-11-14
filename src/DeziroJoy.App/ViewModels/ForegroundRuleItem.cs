using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One rule of the foreground table, as the rules panel edits it. A bindable shell over
/// <see cref="ForegroundRule"/>: the panel edits the fields, the panel's view model collects them
/// back into records on save, and the identity survives the round trip so a rule keeps its place
/// in the list and its <see cref="ForegroundRule.Priority"/> ties stay ordered.
/// <para>
/// The shell also carries the row's own view state: <see cref="Summary"/> is what a closed row
/// shows, and <see cref="IsExpanded"/> is whether the editing fields are showing.
/// </para>
/// </summary>
public partial class ForegroundRuleItem : ObservableObject
{
    public ForegroundRuleItem(ForegroundRule rule)
    {
        Id = rule.Id;
        _processName = rule.ProcessName;
        _priority = rule.Priority;
        _action = rule.Action;
        _actionIndex = rule.Action == ForegroundAction.NativeGamepad ? NativeGamepadIndex : ProfileIndex;
        _slot = rule.Slot;
        _enabled = rule.Enabled;
        _fullscreenOnly = rule.FullscreenOnly;
    }

    /// <summary>The rule's own identity, so an edit never turns into a delete plus an add.</summary>
    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Describe))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    // The row's own validation is derived from the program name, so it has to be re-raised
    // with it: a pick from the window picker fills the name without the row being told, and a
    // row that kept insisting the rule could never match would be reporting a state that is
    // no longer true.
    [NotifyPropertyChangedFor(nameof(IsValid))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsProfileAction))]
    [NotifyPropertyChangedFor(nameof(IsNativeGamepadAction))]
    private string _processName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Describe))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _priority;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Describe))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(IsProfileAction))]
    [NotifyPropertyChangedFor(nameof(IsNativeGamepadAction))]
    private ForegroundAction _action = ForegroundAction.Profile;

    /// <summary>
    /// The action as the combo box's index: 0 switches profile, 1 hands the pad to the program.
    /// The index rather than the enum itself, so the picker needs no converter and the two states
    /// stay in step whichever way either is set.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Action))]
    [NotifyPropertyChangedFor(nameof(Describe))]
    [NotifyPropertyChangedFor(nameof(IsProfileAction))]
    [NotifyPropertyChangedFor(nameof(IsNativeGamepadAction))]
    private int _actionIndex;

    partial void OnActionIndexChanged(int value)
        => Action = value == NativeGamepadIndex ? ForegroundAction.NativeGamepad : ForegroundAction.Profile;

    partial void OnActionChanged(ForegroundAction value)
        => ActionIndex = value == ForegroundAction.NativeGamepad ? NativeGamepadIndex : ProfileIndex;

    /// <summary>The combo index of <see cref="ForegroundAction.Profile"/>.</summary>
    public const int ProfileIndex = 0;

    /// <summary>The combo index of <see cref="ForegroundAction.NativeGamepad"/>.</summary>
    public const int NativeGamepadIndex = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Describe))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _slot = ForegroundRule.DefaultSlot;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Describe))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _fullscreenOnly;

    /// <summary>
    /// Whether the row is open for editing. A rule that comes from the saved table starts
    /// collapsed: the table is read far more often than it is written, and a row that always
    /// showed every field made a handful of rules taller than the window. The row's summary
    /// line carries the identity and the two controls that need no editing - enable and
    /// delete - so a collapsed row is never a dead end. A rule added from the panel starts
    /// open instead, because the user is standing in it with the intent to fill it in.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>
    /// The window the picker has just chosen. Held only for the moment of the choice: the
    /// process name is written into the rule and the selection cleared, so the placeholder
    /// returns and the same window can be picked again for another rule - and so the box never
    /// shows a window the user has since typed over by hand.
    /// </summary>
    [ObservableProperty]
    private ForegroundWindow? _selectedWindow;

    partial void OnSelectedWindowChanged(ForegroundWindow? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.ProcessName))
        {
            return;
        }

        ProcessName = value.ProcessName;
        SelectedWindow = null;
    }

    /// <summary>Whether this rule switches profile, which is the only shape that names a slot.</summary>
    public bool IsProfileAction => Action == ForegroundAction.Profile;

    public bool IsNativeGamepadAction => Action == ForegroundAction.NativeGamepad;

    /// <summary>The rule as the list row and the reason line name it.</summary>
    public string Describe => ToRule().Describe();

    /// <summary>
    /// The rule as one line: what it does and what it costs to be considered. The priority
    /// rides along because it is the field that decides which rule wins, and a table of
    /// priorities readable only from inside an open row is a table nobody can scan. A row with
    /// no program yet says so rather than showing an empty arrow.
    /// </summary>
    public string Summary => string.IsNullOrWhiteSpace(ProcessName)
        ? $"未命名规则 · 优先级 {Priority}"
        : $"{Describe} · 优先级 {Priority}";

    /// <summary>Whether the row can be saved at all: a rule with no program never matches.</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(ProcessName);

    public string? ValidationMessage => IsValid ? null : "程序名不能为空；该规则永远不会命中。";

    /// <summary>The record this row currently edits.</summary>
    public ForegroundRule ToRule() => new()
    {
        Id = Id,
        ProcessName = ProcessName.Trim(),
        Priority = Priority,
        Action = Action,
        Slot = Slot,
        Enabled = Enabled,
        FullscreenOnly = FullscreenOnly,
    };

    public void LoadFrom(ForegroundRule rule)
    {
        ProcessName = rule.ProcessName;
        Priority = rule.Priority;
        Action = rule.Action;
        Slot = rule.Slot;
        Enabled = rule.Enabled;
        FullscreenOnly = rule.FullscreenOnly;
    }}
