using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// One entry of the preset-slot drop-downs: the slot number, and the name the profile stored in
/// that slot carries. The slot is what a rule and the desktop default store; the name is what
/// makes a drop-down readable, and it is the store's to answer - a slot holding nothing falls
/// back to its number alone rather than to a blank line.
/// </summary>
public sealed class PresetSlotOption
{
    public PresetSlotOption(int slot, string name)
    {
        Slot = slot;
        Name = name;
    }

    /// <summary>The profile slot this entry stands for.</summary>
    public int Slot { get; }

    /// <summary>The stored profile's name, or <see cref="UnnamedSlot"/> when the slot holds nothing.</summary>
    public string Name { get; }

    /// <summary>What a slot with no stored profile is called.</summary>
    public string UnnamedSlot => $"预设 {Slot}";

    /// <summary>
    /// What a drop-down shows: "2 · 布局默认" where the slot carries a name of its own, and the
    /// bare 预设 N where it does not - so a numbered list never reads "2 · 预设 2".
    /// </summary>
    public string Display => string.Equals(Name, UnnamedSlot, StringComparison.Ordinal)
        ? UnnamedSlot
        : $"{Slot} · {Name}";

    public override string ToString() => Display;
}

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
    /// <summary>The priority a rule falls back to when its field is left empty.</summary>
    public const int DefaultPriority = 0;

    /// <summary>The lowest priority the field accepts; a rule that must never win goes here.</summary>
    public const int MinPriority = -99;

    /// <summary>The highest priority the field accepts.</summary>
    public const int MaxPriority = 99;

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
        _priorityText = rule.Priority.ToString(CultureInfo.InvariantCulture);
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

    private string _priorityText = string.Empty;

    /// <summary>
    /// The priority as the panel's number field shows and takes it: a plain text field, not a
    /// spinner box. A priority is a number the user types, and the up/down pair was chrome that
    /// spent a column's width on a control nobody spins. The text is parsed on every keystroke,
    /// an unparseable entry leaves the last good value in place rather than eating the character
    /// mid-edit, and an empty field falls back to <see cref="DefaultPriority"/>.
    /// </summary>
    public string PriorityText
    {
        get => _priorityText;
        set
        {
            var typed = value?.Trim() ?? string.Empty;

            // What was typed stays in the field: the parse below decides what the rule says, and
            // a field that rewrote itself on every half-typed number is a field nobody can type in.
            _priorityText = value ?? string.Empty;

            if (typed.Length == 0)
            {
                _priorityText = DefaultPriority.ToString(CultureInfo.InvariantCulture);
                Priority = DefaultPriority;
            }
            else if (int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                // Clamped rather than refused: the range is the panel's, so the field says what it
                // took by showing the number it kept.
                Priority = Math.Clamp(parsed, MinPriority, MaxPriority);
            }

            OnPropertyChanged();
        }
    }

    partial void OnPriorityChanged(int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);

        // Only rewritten when the rule's value actually moved, so an edit in progress in the field
        // - a half-typed number the field has not accepted yet - is never overwritten under the
        // user's cursor.
        if (!string.Equals(_priorityText, text, StringComparison.Ordinal))
        {
            _priorityText = text;
            OnPropertyChanged(nameof(PriorityText));
        }
    }

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

    partial void OnSlotChanged(int value) => ResolveSlotOption();

    private PresetSlotOption? _selectedSlotOption;

    private ObservableCollection<PresetSlotOption>? _slotOptions;

    /// <summary>
    /// The drop-down entries this row offers. The panel's view model owns the one collection and
    /// hands the same instance to every row, so a rebuild - a preset added in another tab - reaches
    /// every row at once and no row is left pointing at an entry that no longer exists.
    /// </summary>
    public ObservableCollection<PresetSlotOption>? SlotOptions
    {
        get => _slotOptions;
        set
        {
            if (Equals(_slotOptions, value))
            {
                return;
            }

            if (_slotOptions is not null)
            {
                _slotOptions.CollectionChanged -= OnSlotOptionsChanged;
            }

            _slotOptions = value;

            if (_slotOptions is not null)
            {
                _slotOptions.CollectionChanged += OnSlotOptionsChanged;
            }

            ResolveSlotOption();
        }
    }

    /// <summary>
    /// The entry the row's preset drop-down has chosen. The slot is the rule's own value and the
    /// option is only how the drop-down spells it, so a pick writes <see cref="Slot"/> and the
    /// entry is resolved back from the slot - never the other way round.
    /// </summary>
    public PresetSlotOption? SelectedSlotOption
    {
        get => _selectedSlotOption;
        set
        {
            // A drop-down that cannot resolve the rule's slot to an offered entry clears its
            // selection, and that write must not take the slot away from the rule: the slot is the
            // model, and the option is only how the control reads it.
            if (value is null || Equals(_selectedSlotOption, value))
            {
                return;
            }

            _selectedSlotOption = value;
            OnPropertyChanged();
            Slot = value.Slot;
        }
    }

    private void OnSlotOptionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ResolveSlotOption();

    /// <summary>
    /// Points the drop-down at the entry that spells this rule's slot. Runs whenever the slot or
    /// the option list moves, so the two can never disagree - and it writes the entry directly
    /// rather than through the property, so resolving never re-enters the slot.
    /// </summary>
    private void ResolveSlotOption()
    {
        var match = _slotOptions?.FirstOrDefault(option => option.Slot == Slot);

        if (Equals(_selectedSlotOption, match))
        {
            return;
        }

        _selectedSlotOption = match;
        OnPropertyChanged(nameof(SelectedSlotOption));
    }

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
