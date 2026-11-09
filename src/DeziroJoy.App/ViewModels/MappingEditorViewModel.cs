using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// A row in a gesture-action picker: the label to show and the target it stands for. A gesture
/// action is an ordinary <see cref="MappingTarget"/> - the same type a mapping's target uses -
/// so a double-click or long-press can fire a key, a combo or a mouse button exactly like the
/// mapping itself, with no second representation to keep in step. A null target marks a row
/// that is not an action at all: 已关闭 turns the gesture off, 重复单击的功能 leaves it
/// re-firing the mapping's own target.
/// </summary>
public sealed record GestureActionOption(string Label, MappingTarget? Target);

/// <summary>A special-key sub-kind as the picker shows it: 精准键 or Leader 键.</summary>
public sealed record SpecialKeyKindOption(SpecialKeyKind Value, string Label);

/// <summary>A leader switch mode as the picker shows it: 按下切换 or 按住切换.</summary>
public sealed record LeaderSwitchModeOption(LeaderSwitchMode Value, string Label);

/// <summary>
/// Editing surface for one mapping: pick the source (button or stick) and the target
/// (single key, combo, mouse button, stick-to-mouse, stick-to-wheel).
/// </summary>
/// <remarks>
/// <para>
/// The source type is chosen first and only the matching picker is offered, so the editor
/// cannot present a stick picker next to a button picker. Every target kind is offered for
/// both source kinds: the engine honors all ten combinations, and the two that need extra
/// information - a stick driving a digital target, a button driving a continuous one - ask
/// for a <see cref="InputDirection"/> through <see cref="ShowDirectionPicker"/>.
/// </para>
/// <para>
/// Live capture is driven from the view (see <c>EditorCapture</c>): gamepad samples arrive
/// through <see cref="CaptureGamepadSample"/> and keyboard keys through
/// <see cref="CaptureKeyboardKey"/>/<see cref="EndKeyboardCapture"/>. This type stays
/// Avalonia-free, so all of it is unit-testable without a window.
/// </para>
/// </remarks>
public partial class MappingEditorViewModel : ObservableObject
{
    /// <summary>
    /// Every target kind, for both source kinds. The engine honors all source x target
    /// combinations, so narrowing the list per source would only hide mappings that work.
    /// </summary>
    private static readonly MappingTargetKind[] AllTargetKinds =
    [
        MappingTargetKind.KeyPress,
        MappingTargetKind.KeyCombo,
        MappingTargetKind.MouseButton,
        MappingTargetKind.MouseMove,
        MappingTargetKind.WheelScroll,
        MappingTargetKind.SpecialKey,
    ];

    private readonly SortedSet<int> _extraButtonCodes = [];
    private readonly SortedSet<int> _previousCaptureExtra = [];
    private ButtonPickerItem? _selectedButtonOption;
    private KeyPickerItem? _selectedKeyOption;
    private KeyPickerItem? _activeSelectedKeyOption;
    private GestureActionOption? _selectedDoubleClickOption;
    private GestureActionOption? _selectedLongPressOption;
    private InputDirection? _loadedDirection;
    private GamepadButtonMask _previousCaptureButtons;

    /// <summary>The row that turns the gesture off: it never resolves, so the press fires at once.</summary>
    public static readonly GestureActionOption GestureOffOption = new("已关闭", null);

    /// <summary>The row that makes the gesture resolve and emit nothing (无功能).</summary>
    public static readonly GestureActionOption GestureNoneOption = new("无功能", new NoneTarget());

    /// <summary>
    /// The row that leaves the gesture without a function of its own: it re-fires the mapping's
    /// own target, the behaviour every profile written before gesture actions existed has.
    /// </summary>
    public static readonly GestureActionOption GestureRepeatOption = new("重复单击的功能", null);

    private static readonly GestureActionOption[] GestureActionRows = BuildGestureActionRows();

    /// <summary>Every target a gesture action can be set to, for the picker.</summary>
    public IReadOnlyList<GestureActionOption> GestureActionOptions { get; } = GestureActionRows;

    private static GestureActionOption[] BuildGestureActionRows()
    {
        var keys = Enum.GetValues<GamepadKey>().Select(
            key =>
            {
                var target = new KeyPressTarget(key);
                return new GestureActionOption(MappingDescriptions.Target(target), target);
            });

        var buttons = Enum.GetValues<MouseButton>().Select(
            button =>
            {
                var target = new MouseButtonTarget(button);
                return new GestureActionOption(MappingDescriptions.Target(target), target);
            });

        // The three non-action rows lead: off, 无功能 and re-fire are the states a gesture
        // itself can be in, and every real target follows.
        return [GestureOffOption, GestureNoneOption, GestureRepeatOption, .. keys, .. buttons];
    }

    public MappingEditorViewModel()
    {
        RebuildButtonOptions();
        RebuildKeyOptions();

        // Every draft is its own object, so the switcher moves between the three gestures
        // without any of them losing an edit; the editor keeps the pickers and the readouts
        // in step with whatever the drafts hold.
        ClickSlot.PropertyChanged += (_, e) => OnClickSlotChanged(e.PropertyName);
        DoubleClickSlot.PropertyChanged += (_, _) => OnDoubleClickSlotChanged();
        LongPressSlot.PropertyChanged += (_, _) => OnLongPressSlotChanged();

        // A fresh editor has both gestures off, so both pickers open on the 已关闭 row rather
        // than on an empty selection.
        SyncDoubleClickOption();
        SyncLongPressOption();
    }

    [ObservableProperty]
    private bool _sourceIsStick;

    [ObservableProperty]
    private GamepadButton _selectedButton = GamepadButton.A;

    [ObservableProperty]
    private int _selectedExtraCode;

    [ObservableProperty]
    private GamepadStick _selectedStick = GamepadStick.Right;

    /// <summary>
    /// The draft for the click: the mapping's own target. The double-click and the long-press
    /// drafts are edited through the same fields, so the switcher moves between three equal
    /// peers without any of them losing an edit.
    /// </summary>
    public GestureSlotEditor ClickSlot { get; } = new();

    /// <summary>The draft for the double-click gesture.</summary>
    public GestureSlotEditor DoubleClickSlot { get; } = new();

    /// <summary>The draft for the long-press gesture.</summary>
    public GestureSlotEditor LongPressSlot { get; } = new();

    /// <summary>The draft the switcher has selected, for a view that binds one set of fields.</summary>
    public GestureSlotEditor ActiveSlot => SelectedGestureKind switch
    {
        GestureKind.DoubleClick => DoubleClickSlot,
        GestureKind.LongPress => LongPressSlot,
        _ => ClickSlot,
    };

    /// <summary>The active slot's target kind.</summary>
    public MappingTargetKind ActiveTargetKind
    {
        get => ActiveSlot.TargetKind;
        set => ActiveSlot.TargetKind = value;
    }

    /// <summary>The active slot's target key.</summary>
    public GamepadKey ActiveSelectedKey
    {
        get => ActiveSlot.SelectedKey;
        set => ActiveSlot.SelectedKey = value;
    }

    /// <summary>The active slot's Ctrl modifier.</summary>
    public bool ActiveModifierCtrl
    {
        get => ActiveSlot.ModifierCtrl;
        set => ActiveSlot.ModifierCtrl = value;
    }

    /// <summary>The active slot's Shift modifier.</summary>
    public bool ActiveModifierShift
    {
        get => ActiveSlot.ModifierShift;
        set => ActiveSlot.ModifierShift = value;
    }

    /// <summary>The active slot's Alt modifier.</summary>
    public bool ActiveModifierAlt
    {
        get => ActiveSlot.ModifierAlt;
        set => ActiveSlot.ModifierAlt = value;
    }

    /// <summary>The active slot's Win modifier.</summary>
    public bool ActiveModifierWin
    {
        get => ActiveSlot.ModifierWin;
        set => ActiveSlot.ModifierWin = value;
    }

    /// <summary>The active slot's mouse button.</summary>
    public MouseButton ActiveSelectedMouseButton
    {
        get => ActiveSlot.SelectedMouseButton;
        set => ActiveSlot.SelectedMouseButton = value;
    }

    /// <summary>The active slot's stick-to-mouse settings.</summary>
    public MouseStickSettings ActiveMouseSettings
    {
        get => ActiveSlot.MouseSettings;
        set => ActiveSlot.MouseSettings = value;
    }

    /// <summary>
    /// The precision key of the active slot's mouse mapping. The key belongs to the mapping,
    /// so it is chosen here rather than on the sensitivity panel, which only reports it.
    /// </summary>
    public GamepadButton ActivePrecisionButton
    {
        get => ActiveSlot.MouseSettings.PrecisionButton;
        set
        {
            ActiveSlot.MouseSettings = ActiveSlot.MouseSettings with { PrecisionButton = value };
            NotifyActiveSlotChanged();
        }
    }

    /// <summary>Every button the precision key can be bound to.</summary>
    public IReadOnlyList<GamepadButton> PrecisionButtonOptions { get; } =
        Enum.GetValues<GamepadButton>().Where(button => button != GamepadButton.Extra).ToArray();

    /// <summary>
    /// The active slot's precision key as every readout names it. The key belongs to the
    /// mapping, so the semantic label - never the raw enum name - is what the user reads.
    /// </summary>
    public string ActivePrecisionButtonLabel => MappingDescriptions.Button(ActivePrecisionButton);

    /// <summary>The special-key sub-kinds, for the picker.</summary>
    public IReadOnlyList<SpecialKeyKindOption> SpecialKindOptions { get; } =
    [
        new(SpecialKeyKind.Precision, "精准键"),
        new(SpecialKeyKind.Leader, "Leader 键"),
    ];

    /// <summary>
    /// The precision modes a 精准键 offers, labelled exactly as the existing precision UI
    /// labels them, so the two surfaces never name the same mode differently.
    /// </summary>
    public IReadOnlyList<PrecisionModeOption> SpecialPrecisionModeOptions { get; } =
    [
        new(PrecisionMode.Hold, "一直按住才触发"),
        new(PrecisionMode.Toggle, "按一下启用，再按一下取消"),
    ];

    /// <summary>The leader switch modes, for the picker.</summary>
    public IReadOnlyList<LeaderSwitchModeOption> SpecialSwitchModeOptions { get; } =
    [
        new(LeaderSwitchMode.Press, "按下切换"),
        new(LeaderSwitchMode.Hold, "按住切换"),
    ];

    /// <summary>
    /// The preset slots a leader key can flip between. Starts at the initial four and is
    /// replaced by the window view model with the device's live slots, so a slot the user
    /// added is offered here too.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<int> _specialSlotOptions = [1, 2, 3, 4];

    /// <summary>The active slot's special-key sub-kind.</summary>
    public SpecialKeyKind ActiveSpecialKind
    {
        get => ActiveSlot.SpecialKind;
        set => ActiveSlot.SpecialKind = value;
    }

    /// <summary>The active slot's special-key precision mode.</summary>
    public PrecisionMode ActiveSpecialPrecisionMode
    {
        get => ActiveSlot.SpecialPrecisionMode;
        set => ActiveSlot.SpecialPrecisionMode = value;
    }

    /// <summary>The active slot's leader switch mode.</summary>
    public LeaderSwitchMode ActiveSpecialSwitchMode
    {
        get => ActiveSlot.SpecialSwitchMode;
        set => ActiveSlot.SpecialSwitchMode = value;
    }

    /// <summary>The first of the two slots a leader key flips between.</summary>
    public int ActiveSpecialSlotA
    {
        get => ActiveSlot.SpecialSlotA;
        set => ActiveSlot.SpecialSlotA = value;
    }

    /// <summary>The second of the two slots a leader key flips between.</summary>
    public int ActiveSpecialSlotB
    {
        get => ActiveSlot.SpecialSlotB;
        set => ActiveSlot.SpecialSlotB = value;
    }

    /// <summary>The selected sub-kind picker row, kept in step with <see cref="ActiveSpecialKind"/>.</summary>
    public SpecialKeyKindOption? ActiveSpecialKindOption
    {
        get => SpecialKindOptions.First(option => option.Value == ActiveSpecialKind);
        set
        {
            if (value is not null)
            {
                ActiveSpecialKind = value.Value;
            }
        }
    }

    /// <summary>The selected precision-mode picker row, kept in step with <see cref="ActiveSpecialPrecisionMode"/>.</summary>
    public PrecisionModeOption? ActiveSpecialPrecisionModeOption
    {
        get => SpecialPrecisionModeOptions.First(option => option.Value == ActiveSpecialPrecisionMode);
        set
        {
            if (value is not null)
            {
                ActiveSpecialPrecisionMode = value.Value;
            }
        }
    }

    /// <summary>The selected switch-mode picker row, kept in step with <see cref="ActiveSpecialSwitchMode"/>.</summary>
    public LeaderSwitchModeOption? ActiveSpecialSwitchModeOption
    {
        get => SpecialSwitchModeOptions.First(option => option.Value == ActiveSpecialSwitchMode);
        set
        {
            if (value is not null)
            {
                ActiveSpecialSwitchMode = value.Value;
            }
        }
    }

    /// <summary>True when the special-key editor offers the leader-only controls.</summary>
    public bool ShowSpecialLeaderControls => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Leader;

    /// <summary>True when the special-key editor offers the precision controls.</summary>
    public bool ShowSpecialPrecisionControls => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Precision;

    /// <summary>
    /// True when a leader key names the same slot twice. Flipping between a slot and itself
    /// is not a switch, so the editor refuses the commit and says why rather than writing a
    /// mapping that can never do anything.
    /// </summary>
    public bool SpecialKeySlotConflict => ActiveTargetKind == MappingTargetKind.SpecialKey
        && ActiveSpecialKind == SpecialKeyKind.Leader
        && ActiveSpecialSlotA == ActiveSpecialSlotB;

    /// <summary>The active slot's stick-to-wheel settings.</summary>
    public ScrollStickSettings ActiveScrollSettings
    {
        get => ActiveSlot.ScrollSettings;
        set => ActiveSlot.ScrollSettings = value;
    }

    /// <summary>无功能 for the active slot: it resolves and emits nothing.</summary>
    public bool ActiveIsNone
    {
        get => ActiveSlot.IsNone;
        set => ActiveSlot.IsNone = value;
    }

    /// <summary>
    /// 重复单击的功能 for the active slot: the legacy state where the gesture has no function
    /// of its own and re-fires the click. It stays reachable without dominating the editor.
    /// </summary>
    public bool ActiveRepeatsClick
    {
        get => ActiveSlot.RepeatsClick;
        set => ActiveSlot.RepeatsClick = value;
    }

    /// <summary>
    /// True when the active gesture resolves. The click always resolves - it is the mapping's
    /// own target - so only the double-click and the long-press carry an off state, and that
    /// state is 已关闭: the gesture never resolves, which is a different thing from resolving
    /// to 无功能.
    /// </summary>
    public bool ActiveGestureEnabled
    {
        get => SelectedGestureKind switch
        {
            GestureKind.DoubleClick => DoubleClickEnabled,
            GestureKind.LongPress => LongPressEnabled,
            _ => true,
        };
        set
        {
            switch (SelectedGestureKind)
            {
                case GestureKind.DoubleClick:
                    DoubleClickEnabled = value;
                    break;
                case GestureKind.LongPress:
                    LongPressEnabled = value;
                    break;
            }
        }
    }

    /// <summary>True when the active slot can be turned off, i.e. when it is a gesture at all.</summary>
    public bool ActiveGestureCanBeDisabled => SelectedGestureKind != GestureKind.Click;

    /// <summary>The active gesture's own name, for readouts.</summary>
    public string ActiveGestureName => SelectedGestureKind switch
    {
        GestureKind.DoubleClick => "双击",
        GestureKind.LongPress => "长按",
        _ => "单击",
    };

    /// <summary>
    /// True when the shared target editor's controls are live: the gesture resolves and the
    /// slot is not 无功能. A disabled gesture has nothing to configure until it is enabled.
    /// </summary>
    public bool ActiveTargetEditable => ActiveGestureEnabled && !ActiveSlot.IsNone;

    /// <summary>
    /// What the active slot does, for the header readout. The click is the mapping's own
    /// target, so it reads as the target alone; a gesture slot is prefixed with its name, and
    /// a disabled one reads 已关闭 rather than naming a target that never fires.
    /// </summary>
    public string ActiveTargetDescription
    {
        get
        {
            if (ActiveGestureCanBeDisabled && !ActiveGestureEnabled)
            {
                return $"{ActiveGestureName}：已关闭";
            }

            var target = ActiveSlot.BuildTarget();
            var what = ActiveSlot.Function switch
            {
                GestureSlotFunction.None => "无功能",
                GestureSlotFunction.RepeatClick => "重复单击的功能",
                _ => target is SpecialKeyTarget special
                    ? SpecialKeyDescription(special)
                    : MappingDescriptions.Target(target),
            };

            return ActiveGestureCanBeDisabled ? $"{ActiveGestureName}：{what}" : what;
        }
    }

    /// <summary>
    /// What a special key does, named the way its own picker names it. The Core record has
    /// no description of its own, and its raw <c>ToString</c> would read as debug output in
    /// the editor's header.
    /// </summary>
    private static string SpecialKeyDescription(SpecialKeyTarget target)
        => target.Kind == SpecialKeyKind.Leader ? "Leader 键" : "精准键";

    /// <summary>
    /// The selected row of the categorized key list for the active slot. A category header is
    /// a label, not a choice, so assigning one snaps the selection back to the key that is
    /// actually selected - the rule <see cref="SelectedKeyOption"/> applies to the click alone.
    /// </summary>
    public KeyPickerItem? ActiveSelectedKeyOption
    {
        get => _activeSelectedKeyOption;
        set
        {
            if (EqualityComparer<KeyPickerItem?>.Default.Equals(value, _activeSelectedKeyOption))
            {
                return;
            }

            if (value is { IsHeader: true })
            {
                SetActiveSelectedKeyOption(CurrentActiveKeyOption());
                return;
            }

            SetActiveSelectedKeyOption(value);

            if (value is not null)
            {
                ActiveSlot.SelectedKey = value.Key;

                // Picking a key implies a key target; a mouse-button target would silently
                // drop it, so the kind follows the choice.
                if (ActiveSlot.TargetKind == MappingTargetKind.MouseButton)
                {
                    ActiveSlot.TargetKind = MappingTargetKind.KeyPress;
                }
            }
        }
    }

    /// <summary>
    /// Whether the categorized key list is expanded. It starts collapsed on every editor - the
    /// list is the long way to a key, so it stays out of the way until it is asked for - and
    /// the choice lives here so a new editor opens collapsed rather than wherever the last one
    /// left it.
    /// </summary>
    [ObservableProperty]
    private bool _keyPickerExpanded;

    [ObservableProperty]
    private IReadOnlyList<MappingTargetKind> _availableTargetKinds = AllTargetKinds;

    /// <summary>The click's target kind, edited on <see cref="ClickSlot"/>.</summary>
    public MappingTargetKind TargetKind
    {
        get => ClickSlot.TargetKind;
        set => ClickSlot.TargetKind = value;
    }

    /// <summary>
    /// Direction for the two cross combinations. Ignored elsewhere, where a direction is
    /// meaningless: a stick driving mouse move / wheel consumes both axes, and a button
    /// driving a key / combo / mouse button has no axis to deflect. The source description
    /// names the direction, so it is notified here too: the editor header must follow the
    /// choice the moment it is made, not at the next unrelated notification.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDirectionOption))]
    [NotifyPropertyChangedFor(nameof(SourceDescription))]
    private InputDirection _selectedDirection = InputDirection.Up;

    /// <summary>
    /// Which gesture the switcher has selected. The click leads; the double-click and the
    /// long-press are reached through the same switcher rather than buried as extras.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveSlot))]
    [NotifyPropertyChangedFor(nameof(IsClickSelected))]
    [NotifyPropertyChangedFor(nameof(IsDoubleClickSelected))]
    [NotifyPropertyChangedFor(nameof(IsLongPressSelected))]
    private GestureKind _selectedGestureKind = GestureKind.Click;

    /// <summary>True when the double-click gesture resolves. Its timing is the pinned default.</summary>
    [ObservableProperty]
    private bool _doubleClickEnabled;

    /// <summary>True when the long-press gesture resolves. Its timing is the pinned default.</summary>
    [ObservableProperty]
    private bool _longPressEnabled;

    /// <summary>
    /// True when holding the button repeats the click ("长按重复触发"). Mutually exclusive
    /// with the long-press: enabling it drops the long-press, and configuring the long-press
    /// switches it back off.
    /// </summary>
    [ObservableProperty]
    private bool _clickRepeatEnabled;

    /// <summary>
    /// True when the click itself is set to 无功能: the mapping's own target becomes a
    /// <see cref="NoneTarget"/>, so a plain press emits nothing.
    /// </summary>
    public bool ClickIsNone
    {
        get => ClickSlot.IsNone;
        set => ClickSlot.IsNone = value;
    }

    /// <summary>
    /// Seconds between long-press activations while the button is held. Not user-editable -
    /// the timing sliders are gone - but a profile that carries one keeps it through a
    /// round trip.
    /// </summary>
    public float LongPressRepeatInterval { get; set; }

    /// <summary>The lowest click-repeat interval the editor offers, in seconds.</summary>
    public const double ClickRepeatIntervalMin = 0.05;

    /// <summary>The highest click-repeat interval the editor offers, in seconds.</summary>
    public const double ClickRepeatIntervalMax = 2.00;

    /// <summary>The click-repeat interval editor's step, in seconds.</summary>
    public const double ClickRepeatIntervalStep = 0.05;

    private float _clickRepeatInterval;

    /// <summary>
    /// Seconds between click auto-repeat activations while the button is held, as the
    /// profile carries it: 0 means the pinned
    /// <see cref="ButtonGesture.DefaultClickRepeatInterval"/>. The editor's interval field
    /// reads and writes it through <see cref="ClickRepeatIntervalSeconds"/>.
    /// </summary>
    public float ClickRepeatInterval
    {
        get => _clickRepeatInterval;
        set
        {
            _clickRepeatInterval = value;
            OnPropertyChanged(nameof(ClickRepeatIntervalSeconds));
        }
    }

    /// <summary>
    /// The click-repeat interval the editor shows, in seconds: the pinned default while no
    /// custom value is carried, otherwise the loaded one. The setter clamps to the editable
    /// range and snaps to the step, so every value the editor writes is one the profile
    /// round-trips unchanged.
    /// </summary>
    public double ClickRepeatIntervalSeconds
    {
        get => ClickRepeatInterval > 0f ? ClickRepeatInterval : ButtonGesture.DefaultClickRepeatInterval;
        set
        {
            ClickRepeatInterval = (float)SnapClickRepeatInterval(value);
            OnPropertyChanged(nameof(ClickRepeatIntervalSeconds));
        }
    }

    /// <summary>
    /// Clamps to the editable range and snaps to the step: 0.05-2.00s in 0.05s steps, so a
    /// value typed or spun into the editor lands on the grid the engine and the profile
    /// both speak.
    /// </summary>
    internal static double SnapClickRepeatInterval(double seconds)
    {
        var clamped = Math.Clamp(seconds, ClickRepeatIntervalMin, ClickRepeatIntervalMax);
        return Math.Round(clamped / ClickRepeatIntervalStep, MidpointRounding.AwayFromZero) * ClickRepeatIntervalStep;
    }

    /// <summary>
    /// What the double-click emits. Null leaves the double-click without a function of its own,
    /// so it re-fires the mapping's target - the behaviour every profile written before this
    /// field existed already has. A disabled gesture carries no action either, so an off
    /// double-click never writes a dormant target into the profile.
    /// </summary>
    public MappingTarget? DoubleClickAction
    {
        get => DoubleClickEnabled ? DoubleClickSlot.BuildAction() : null;
        set => DoubleClickSlot.LoadAction(value);
    }

    /// <summary>What the long-press emits, and what auto-repeat re-fires. Null behaves as above.</summary>
    public MappingTarget? LongPressAction
    {
        get => LongPressEnabled ? LongPressSlot.BuildAction() : null;
        set => LongPressSlot.LoadAction(value);
    }

    /// <summary>The click's target key, edited on <see cref="ClickSlot"/>.</summary>
    public GamepadKey SelectedKey
    {
        get => ClickSlot.SelectedKey;
        set => ClickSlot.SelectedKey = value;
    }

    /// <summary>The click's Ctrl modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierCtrl
    {
        get => ClickSlot.ModifierCtrl;
        set => ClickSlot.ModifierCtrl = value;
    }

    /// <summary>The click's Shift modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierShift
    {
        get => ClickSlot.ModifierShift;
        set => ClickSlot.ModifierShift = value;
    }

    /// <summary>The click's Alt modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierAlt
    {
        get => ClickSlot.ModifierAlt;
        set => ClickSlot.ModifierAlt = value;
    }

    /// <summary>The click's Win modifier, edited on <see cref="ClickSlot"/>.</summary>
    public bool ModifierWin
    {
        get => ClickSlot.ModifierWin;
        set => ClickSlot.ModifierWin = value;
    }

    /// <summary>The click's mouse button, edited on <see cref="ClickSlot"/>.</summary>
    public MouseButton SelectedMouseButton
    {
        get => ClickSlot.SelectedMouseButton;
        set => ClickSlot.SelectedMouseButton = value;
    }

    /// <summary>The click's stick-to-mouse settings, edited on <see cref="ClickSlot"/>.</summary>
    public MouseStickSettings MouseSettings
    {
        get => ClickSlot.MouseSettings;
        set => ClickSlot.MouseSettings = value;
    }

    /// <summary>The click's stick-to-wheel settings, edited on <see cref="ClickSlot"/>.</summary>
    public ScrollStickSettings ScrollSettings
    {
        get => ClickSlot.ScrollSettings;
        set => ClickSlot.ScrollSettings = value;
    }

    [ObservableProperty]
    private bool _isCapturingButton;

    [ObservableProperty]
    private bool _isCapturingKey;

    /// <summary>Categorized rows for the button picker, headers included.</summary>
    public ObservableCollection<ButtonPickerItem> ButtonOptions { get; } = [];

    /// <summary>Categorized rows for the target-key picker, headers included.</summary>
    public ObservableCollection<KeyPickerItem> KeyOptions { get; } = [];

    /// <summary>
    /// The categorized key picker as the navigator and the grid show it: one group per
    /// category, in the picker's own order. The groups hold the very same
    /// <see cref="KeyPickerItem"/> instances the flat <see cref="KeyOptions"/> list carries, so
    /// the grid and the lookup never disagree about what a key is.
    /// </summary>
    public ObservableCollection<KeyCategoryGroup> KeyCategoryGroups { get; } = [];

    /// <summary>
    /// The category the key grid currently shows, and the one its navigator segment has
    /// checked. It follows the selection: the picker's job is to put the selected key in
    /// view, so a gesture switch or a reload landing on another slot's key opens that key's
    /// category rather than the one the previous browse left behind. A click on a segment
    /// moves it too - the segment writes its check onto its group, and the group's change is
    /// adopted here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveKeyCategoryColumns))]
    private KeyCategoryGroup? _activeKeyCategory;

    /// <summary>
    /// How many columns the active category's grid lays its keys out in. A short category
    /// shares one row - 面部按键's four caps do not float in a half-empty grid - and a long
    /// one is capped at <see cref="KeyPickerMaxColumns"/> so the card's width is spent on the
    /// grid itself instead of on a handful of over-wide caps: a denser grid is a shorter one,
    /// and a shorter grid is a shorter scroll.
    /// </summary>
    public int ActiveKeyCategoryColumns =>
        Math.Min(Math.Max(ActiveKeyCategory?.Keys.Count ?? 1, 1), KeyPickerMaxColumns);

    /// <summary>The most columns a key-cap row may use, however many keys its category has.</summary>
    private const int KeyPickerMaxColumns = 6;

    /// <summary>Source type shown by the selector; the single place the user picks button vs stick.</summary>
    public MappingSourceKind SourceKind
    {
        get => SourceIsStick ? MappingSourceKind.Stick : MappingSourceKind.Button;
        set => SourceIsStick = value == MappingSourceKind.Stick;
    }

    public bool ShowButtonPicker => !SourceIsStick;

    public bool ShowStickPicker => SourceIsStick;

    /// <summary>Rows for the direction picker.</summary>
    public IReadOnlyList<DirectionPickerItem> DirectionOptions { get; } = DirectionCategories.All;

    /// <summary>
    /// True when the current source x target pair cannot work without a direction, so the
    /// picker has to be shown. A stick is an axis, so driving a digital target from it needs
    /// to know which deflection presses; a button is binary, so driving a continuous target
    /// from it needs to know which way it deflects. Every digital target counts for the
    /// stick - key, combo, mouse button and special key alike - because the engine rejects
    /// a directionless stick driving any of them.
    /// </summary>
    public bool NeedsDirection => SourceIsStick
        ? TargetKind is MappingTargetKind.KeyPress or MappingTargetKind.KeyCombo
            or MappingTargetKind.MouseButton or MappingTargetKind.SpecialKey
        : TargetKind is MappingTargetKind.MouseMove or MappingTargetKind.WheelScroll;

    public bool ShowDirectionPicker => NeedsDirection;

    /// <summary>
    /// The selected direction picker row, read from <see cref="SelectedDirection"/> rather
    /// than cached, so a loaded or reset direction shows its own row instead of the one the
    /// previous edit left behind: reopening a mapping always displays the direction it saved.
    /// </summary>
    public DirectionPickerItem? SelectedDirectionOption
    {
        get => DirectionOptions.FirstOrDefault(option => option.Direction == SelectedDirection);
        set
        {
            if (value is not null)
            {
                SelectedDirection = value.Direction;
            }
        }
    }

    /// <summary>True when the double-click gesture resolves. Its timing is the pinned default.</summary>
    public bool HasDoubleClick => DoubleClickEnabled;

    /// <summary>
    /// The long-press as it actually behaves: inert while click auto-repeat is enabled, exactly
    /// as <see cref="ButtonGesture.HasEffectiveLongPress"/> says.
    /// </summary>
    public bool HasLongPress => LongPressEnabled && !ClickRepeatEnabled;

    /// <summary>True when holding the button repeats the click.</summary>
    public bool HasClickRepeat => ClickRepeatEnabled;

    /// <summary>
    /// True when the click-repeat interval editor is offered: the click tab with repeat on.
    /// The interval belongs to the click's own repeat, so it waits for the same tab the
    /// checkbox lives on.
    /// </summary>
    public bool ShowClickRepeatInterval => IsClickSelected && ClickRepeatEnabled;

    /// <summary>
    /// True when any gesture is configured, i.e. when the engine must introduce a wait before
    /// the single action can fire. False keeps the zero-latency single-click path.
    /// </summary>
    public bool HasAnyGesture => HasDoubleClick || HasLongPress || HasClickRepeat;

    /// <summary>
    /// False while click auto-repeat is on: the two are mutually exclusive, so the long-press
    /// branch of the switcher is unavailable rather than configurable into silence.
    /// </summary>
    public bool LongPressAvailable => !ClickRepeatEnabled;

    /// <summary>True when the double-click has a function of its own rather than re-firing the single.</summary>
    public bool HasDoubleClickAction => DoubleClickAction is not null;

    /// <summary>True when the long-press has a function of its own rather than re-firing the single.</summary>
    public bool HasLongPressAction => LongPressAction is not null;

    /// <summary>What the double-click currently does, for the readout beside its picker.</summary>
    public string DoubleClickActionDescription => DoubleClickAction switch
    {
        null => DoubleClickEnabled ? GestureRepeatOption.Label : GestureOffOption.Label,
        NoneTarget => GestureNoneOption.Label,
        var action => MappingDescriptions.Target(action),
    };

    /// <summary>What the long-press currently does, for the readout beside its picker.</summary>
    public string LongPressActionDescription => LongPressAction switch
    {
        null => LongPressEnabled ? GestureRepeatOption.Label : GestureOffOption.Label,
        NoneTarget => GestureNoneOption.Label,
        var action => MappingDescriptions.Target(action),
    };

    /// <summary>The switcher flag for the click, kept in step with <see cref="SelectedGestureKind"/>.</summary>
    public bool IsClickSelected
    {
        get => SelectedGestureKind == GestureKind.Click;
        set
        {
            if (value)
            {
                SelectedGestureKind = GestureKind.Click;
            }
        }
    }

    /// <summary>The switcher flag for the double-click, kept in step with <see cref="SelectedGestureKind"/>.</summary>
    public bool IsDoubleClickSelected
    {
        get => SelectedGestureKind == GestureKind.DoubleClick;
        set
        {
            if (value)
            {
                SelectedGestureKind = GestureKind.DoubleClick;
            }
        }
    }

    /// <summary>The switcher flag for the long-press, kept in step with <see cref="SelectedGestureKind"/>.</summary>
    public bool IsLongPressSelected
    {
        get => SelectedGestureKind == GestureKind.LongPress;
        set
        {
            if (value)
            {
                SelectedGestureKind = GestureKind.LongPress;
            }
        }
    }

    /// <summary>
    /// The selected double-click picker row, kept in step with <see cref="DoubleClickAction"/>.
    /// Picking any row but 已关闭 configures the gesture; 已关闭 turns it off. A hand-edited
    /// profile may name a target the picker does not list, which leaves this null while the
    /// action itself survives the round-trip untouched.
    /// </summary>
    public GestureActionOption? SelectedDoubleClickOption
    {
        get => _selectedDoubleClickOption;
        set
        {
            if (EqualityComparer<GestureActionOption?>.Default.Equals(value, _selectedDoubleClickOption))
            {
                return;
            }

            DoubleClickEnabled = value is not null && !ReferenceEquals(value, GestureOffOption);
            DoubleClickAction = value?.Target;
            _selectedDoubleClickOption = value;
            OnPropertyChanged(nameof(SelectedDoubleClickOption));
        }
    }

    /// <summary>The selected long-press picker row, kept in step with <see cref="LongPressAction"/>.</summary>
    public GestureActionOption? SelectedLongPressOption
    {
        get => _selectedLongPressOption;
        set
        {
            if (EqualityComparer<GestureActionOption?>.Default.Equals(value, _selectedLongPressOption))
            {
                return;
            }

            LongPressEnabled = value is not null && !ReferenceEquals(value, GestureOffOption);
            LongPressAction = value?.Target;
            _selectedLongPressOption = value;
            OnPropertyChanged(nameof(SelectedLongPressOption));
        }
    }

    private void SyncDoubleClickOption()
    {
        var option = DoubleClickAction is { } action
            ? GestureActionOptions.FirstOrDefault(
                candidate => EqualityComparer<MappingTarget>.Default.Equals(candidate.Target, action))
            : DoubleClickEnabled
                ? GestureRepeatOption
                : GestureOffOption;

        if (!EqualityComparer<GestureActionOption?>.Default.Equals(option, _selectedDoubleClickOption))
        {
            _selectedDoubleClickOption = option;
            OnPropertyChanged(nameof(SelectedDoubleClickOption));
        }
    }

    private void SyncLongPressOption()
    {
        var option = LongPressAction is { } action
            ? GestureActionOptions.FirstOrDefault(
                candidate => EqualityComparer<MappingTarget>.Default.Equals(candidate.Target, action))
            : LongPressEnabled
                ? GestureRepeatOption
                : GestureOffOption;

        if (!EqualityComparer<GestureActionOption?>.Default.Equals(option, _selectedLongPressOption))
        {
            _selectedLongPressOption = option;
            OnPropertyChanged(nameof(SelectedLongPressOption));
        }
    }

    public string CaptureButtonLabel => IsCapturingButton ? "停止捕获" : "捕获按键";

    public string CaptureKeyLabel => IsCapturingKey ? "停止捕获" : "捕获键盘";

    /// <summary>What the model element being edited is called, e.g. "左肩键 LB".</summary>
    public string SourceDescription => MappingDescriptions.Source(BuildSource());

    /// <summary>What the chosen target does, e.g. "Ctrl + C", or 无功能 when the click does nothing.</summary>
    public string TargetDescription
    {
        get
        {
            if (ClickIsNone)
            {
                return "无功能";
            }

            var target = BuildTarget();

            return target is SpecialKeyTarget special
                ? SpecialKeyDescription(special)
                : MappingDescriptions.Target(target);
        }
    }

    /// <summary>
    /// The selected picker row. A category header is a label, not a choice, so assigning one
    /// snaps the selection back to the button that is actually selected.
    /// </summary>
    public ButtonPickerItem? SelectedButtonOption
    {
        get => _selectedButtonOption;
        set
        {
            if (EqualityComparer<ButtonPickerItem?>.Default.Equals(value, _selectedButtonOption))
            {
                return;
            }

            if (value is { IsHeader: true })
            {
                SetSelectedButtonOption(CurrentOption());
                return;
            }

            SetSelectedButtonOption(value);

            if (value is not null)
            {
                SelectedButton = value.Button;
                SelectedExtraCode = value.Code;
            }
        }
    }

    /// <summary>The click's modifiers, read from <see cref="ClickSlot"/>.</summary>
    public KeyModifiers Modifiers => ClickSlot.Modifiers;

    /// <summary>
    /// The selected picker row for the target key. A category header is a label, not a choice,
    /// so assigning one snaps the selection back to the key that is actually selected.
    /// </summary>
    public KeyPickerItem? SelectedKeyOption
    {
        get => _selectedKeyOption;
        set
        {
            if (EqualityComparer<KeyPickerItem?>.Default.Equals(value, _selectedKeyOption))
            {
                return;
            }

            if (value is { IsHeader: true })
            {
                SetSelectedKeyOption(CurrentKeyOption());
                return;
            }

            SetSelectedKeyOption(value);

            if (value is not null)
            {
                SelectedKey = value.Key;

                // Picking a key implies a key target; a mouse-button target would silently
                // drop it, so the kind follows the choice.
                if (TargetKind == MappingTargetKind.MouseButton)
                {
                    TargetKind = MappingTargetKind.KeyPress;
                }
            }
        }
    }

    public InputBinding BuildSource()
    {
        // A direction is required for the two cross combinations; elsewhere the value the
        // profile carried is preserved so a round-trip never silently drops one.
        var direction = NeedsDirection ? SelectedDirection : _loadedDirection;

        return SourceIsStick
            ? new StickBinding(SelectedStick) { Direction = direction }
            : new ButtonBinding(SelectedButton, SelectedExtraCode) { Direction = direction, Gesture = BuildGesture() };
    }

    /// <summary>
    /// The gesture for the button being edited, or null when nothing is enabled. Null - rather
    /// than an all-zero <see cref="ButtonGesture"/> - is what keeps the press edge firing with
    /// no added latency and keeps ordinary profiles free of gesture JSON. The timings are the
    /// Core's pinned defaults: nothing here reads a user-facing slider.
    /// </summary>
    public ButtonGesture? BuildGesture()
    {
        var doubleClickWindow = HasDoubleClick ? ButtonGesture.DefaultDoubleClickWindow : 0f;
        var longPressThreshold = HasLongPress ? ButtonGesture.DefaultLongPressThreshold : 0f;

        // A carried interval wins; a freshly enabled repeat with nothing loaded falls back to
        // the pinned default, exactly as it did before profiles could carry their own.
        var clickRepeatInterval = ClickRepeatEnabled
            ? ClickRepeatInterval > 0f ? ClickRepeatInterval : ButtonGesture.DefaultClickRepeatInterval
            : 0f;

        if (doubleClickWindow <= 0f && longPressThreshold <= 0f && clickRepeatInterval <= 0f)
        {
            return null;
        }

        return new ButtonGesture
        {
            DoubleClickWindow = doubleClickWindow,
            LongPressThreshold = longPressThreshold,
            LongPressRepeatInterval = LongPressRepeatInterval,
            ClickRepeatInterval = clickRepeatInterval,
            DoubleClickAction = DoubleClickAction,
            LongPressAction = LongPressAction,
        };
    }

    public MappingTarget BuildTarget() => ClickSlot.BuildTarget();

    public MappingEntry BuildEntry() => new()
    {
        Source = BuildSource(),
        Target = BuildTarget(),
    };

    public void LoadFrom(MappingEntry entry)
    {
        switch (entry.Source)
        {
            case ButtonBinding button:
                SourceIsStick = false;
                if (button.Button == GamepadButton.Extra)
                {
                    RegisterExtraButton(button.Code);
                }

                SelectedButton = button.Button;
                SelectedExtraCode = button.Code;
                _loadedDirection = button.Direction;
                SelectedDirection = button.Direction ?? InputDirection.Up;

                // The timings are pinned, so a loaded gesture is expressed as enabled flags.
                // Click auto-repeat is read first: the Core makes the long-press inert while it
                // runs, so an inert long-press action is dropped rather than loaded into a
                // gesture that can never fire it.
                ClickRepeatEnabled = button.Gesture is { HasClickRepeat: true };
                ClickRepeatInterval = button.Gesture?.ClickRepeatInterval ?? 0f;
                DoubleClickEnabled = button.Gesture is { HasDoubleClick: true };
                LongPressEnabled = button.Gesture is { HasEffectiveLongPress: true };
                LongPressRepeatInterval = button.Gesture?.LongPressRepeatInterval ?? 0f;

                // Assigned after the flags so a profile carrying an action at a zero timing is
                // repaired by the same rule the picker uses, rather than silently dropped.
                DoubleClickAction = button.Gesture?.DoubleClickAction;
                LongPressAction = button.Gesture is { HasEffectiveLongPress: true }
                    ? button.Gesture.LongPressAction
                    : null;
                break;
            case StickBinding stick:
                SourceIsStick = true;
                SelectedStick = stick.Stick;
                _loadedDirection = stick.Direction;
                SelectedDirection = stick.Direction ?? InputDirection.Up;
                break;
        }

        // The click draft takes the mapping's own target; the two gesture drafts were filled
        // from the button's gesture above.
        ClickSlot.LoadAction(entry.Target);

        // The optional key list is collapsed on every editor, so a reloaded mapping opens on
        // the compact surface rather than wherever the previous edit left the list.
        KeyPickerExpanded = false;
    }

    public void Reset()
    {
        IsCapturingButton = false;
        IsCapturingKey = false;
        _previousCaptureButtons = GamepadButtonMask.None;
        _previousCaptureExtra.Clear();
        _loadedDirection = null;
        SourceIsStick = false;
        SelectedButton = GamepadButton.A;
        SelectedExtraCode = 0;
        SelectedStick = GamepadStick.Right;
        SelectedDirection = InputDirection.Up;
        SelectedGestureKind = GestureKind.Click;
        KeyPickerExpanded = false;

        // The drafts go back to pristine first; the gesture flags and actions follow, so a
        // reset gesture ends up off rather than carrying a dormant target.
        ClickSlot.Reset();
        DoubleClickSlot.Reset();
        LongPressSlot.Reset();
        DoubleClickEnabled = false;
        LongPressEnabled = false;
        ClickRepeatEnabled = false;
        LongPressRepeatInterval = 0f;
        ClickRepeatInterval = 0f;
        DoubleClickAction = null;
        LongPressAction = null;

        // The picker's category follows the selection, so a reopened editor opens on the key
        // that is selected rather than on the browse the previous edit left behind. The active
        // option is often unchanged by a reset - the default key is the default key - and the
        // follow in SetActiveSelectedKeyOption would then never run, so it is called directly.
        FollowActiveKeyCategory(CurrentActiveKeyOption());
    }

    /// <summary>
    /// Arms the editor for one source, as implied by a click on the gamepad model. The source
    /// is never chosen here: the clicked element decides it, so this only resets the target
    /// and points the editor at that element.
    /// </summary>
    public void LoadSource(InputBinding source)
    {
        Reset();

        switch (source)
        {
            case ButtonBinding button:
                if (button.Button == GamepadButton.Extra)
                {
                    RegisterExtraButton(button.Code);
                }

                SelectedButton = button.Button;
                SelectedExtraCode = button.Code;
                break;
            case StickBinding stick:
                SourceIsStick = true;
                SelectedStick = stick.Stick;

                // A freshly armed stick starts on the target a stick is normally used for;
                // every other kind stays reachable through the target picker.
                TargetKind = MappingTargetKind.MouseMove;
                break;
        }
    }

    /// <summary>
    /// Adds a raw platform code to the picker's 扩展按键 group. Codes describe the connected
    /// device rather than one mapping, so they survive <see cref="Reset"/>.
    /// </summary>
    public void RegisterExtraButton(int code)
    {
        if (code <= 0 || !_extraButtonCodes.Add(code))
        {
            return;
        }

        RebuildButtonOptions();
    }

    /// <summary>
    /// Feeds one engine sample into button capture. Only a press edge selects a button, so a
    /// button held down before capture was armed is ignored; samples are also tracked while
    /// capture is disarmed so arming it never fires on an already-held button. Returns true
    /// when this sample was consumed as the mapping's new source, so the caller knows the
    /// press must not also drive navigation.
    /// </summary>
    public bool CaptureGamepadSample(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var pressed = state.PressedButtons;
        var edges = pressed & ~_previousCaptureButtons;
        var consumed = false;

        if (IsCapturingButton)
        {
            var edge = FirstPressed(edges);
            if (edge is not null)
            {
                SelectButton(edge.Value, 0);
                IsCapturingButton = false;
                consumed = true;
            }
            else
            {
                foreach (var code in state.ExtraButtons)
                {
                    if (!_previousCaptureExtra.Contains(code))
                    {
                        SelectButton(GamepadButton.Extra, code);
                        IsCapturingButton = false;
                        consumed = true;
                        break;
                    }
                }
            }
        }

        _previousCaptureButtons = pressed;
        _previousCaptureExtra.Clear();
        foreach (var code in state.ExtraButtons)
        {
            _previousCaptureExtra.Add(code);
        }

        return consumed;
    }

    /// <summary>
    /// Records a captured keyboard key together with the modifiers held with it. A lone key
    /// becomes a single-key target; any modifier turns it into a combo target. The capture
    /// writes the slot the switcher has selected, so the same affordance configures the
    /// double-click and the long-press as peers of the click.
    /// </summary>
    public void CaptureKeyboardKey(GamepadKey key, KeyModifiers modifiers)
    {
        if (!IsCapturingKey)
        {
            return;
        }

        // A key target is only honored for a button source, so capturing one repairs a stick
        // source instead of producing a mapping the engine would silently drop.
        SourceIsStick = false;

        var slot = ActiveSlot;
        slot.TargetKind = modifiers == KeyModifiers.None ? MappingTargetKind.KeyPress : MappingTargetKind.KeyCombo;
        slot.SelectedKey = key;
        slot.ModifierCtrl = modifiers.HasFlag(KeyModifiers.Ctrl);
        slot.ModifierShift = modifiers.HasFlag(KeyModifiers.Shift);
        slot.ModifierAlt = modifiers.HasFlag(KeyModifiers.Alt);
        slot.ModifierWin = modifiers.HasFlag(KeyModifiers.Win);
    }

    /// <summary>Commits keyboard capture once every key has been released.</summary>
    public void EndKeyboardCapture()
    {
        if (!IsCapturingKey)
        {
            return;
        }

        IsCapturingKey = false;
    }

    [RelayCommand]
    private void ToggleCaptureButton() => IsCapturingButton = !IsCapturingButton;

    [RelayCommand]
    private void ToggleCaptureKey() => IsCapturingKey = !IsCapturingKey;

    partial void OnSourceIsStickChanged(bool value)
    {
        OnPropertyChanged(nameof(SourceKind));
        OnPropertyChanged(nameof(ShowButtonPicker));
        OnPropertyChanged(nameof(ShowStickPicker));
        OnPropertyChanged(nameof(SourceDescription));
        OnPropertyChanged(nameof(NeedsDirection));
        OnPropertyChanged(nameof(ShowDirectionPicker));

        // Every kind is valid for both source kinds, so the list never has to be narrowed and
        // the chosen target survives a source switch. Only the direction picker comes and goes.
        AvailableTargetKinds = AllTargetKinds;
    }

    partial void OnSelectedButtonChanged(GamepadButton value)
    {
        if (value != GamepadButton.Extra && SelectedExtraCode != 0)
        {
            SelectedExtraCode = 0;
        }

        SetSelectedButtonOption(CurrentOption());
        OnPropertyChanged(nameof(SourceDescription));
    }

    partial void OnSelectedExtraCodeChanged(int value)
    {
        SetSelectedButtonOption(CurrentOption());
        OnPropertyChanged(nameof(SourceDescription));
    }

    partial void OnSelectedStickChanged(GamepadStick value)
        => OnPropertyChanged(nameof(SourceDescription));

    partial void OnSelectedGestureKindChanged(GestureKind value)
    {
        // Selecting the long-press switches auto-repeat off: the two are mutually exclusive,
        // so the long-press branch is only ever reachable with auto-repeat disarmed.
        if (value == GestureKind.LongPress)
        {
            ClickRepeatEnabled = false;
        }

        OnPropertyChanged(nameof(ShowClickRepeatInterval));
        NotifyActiveSlotChanged();
    }

    partial void OnClickRepeatEnabledChanged(bool value)
    {
        if (value)
        {
            // Auto-repeat makes the long-press inert, so the long-press is dropped rather than
            // left configured: the user must never see a long-press that silently does nothing.
            LongPressEnabled = false;
            LongPressAction = null;

            if (SelectedGestureKind == GestureKind.LongPress)
            {
                SelectedGestureKind = GestureKind.Click;
            }
        }

        OnPropertyChanged(nameof(LongPressAvailable));
        OnPropertyChanged(nameof(HasLongPress));
        OnPropertyChanged(nameof(HasAnyGesture));
        OnPropertyChanged(nameof(ShowClickRepeatInterval));
    }

    partial void OnDoubleClickEnabledChanged(bool value)
    {
        SyncDoubleClickOption();
        OnPropertyChanged(nameof(HasDoubleClick));
        OnPropertyChanged(nameof(HasAnyGesture));
        NotifyActiveSlotChanged();
    }

    partial void OnLongPressEnabledChanged(bool value)
    {
        if (value)
        {
            // Configuring the long-press switches auto-repeat off, and vice versa.
            ClickRepeatEnabled = false;
        }

        SyncLongPressOption();
        OnPropertyChanged(nameof(HasLongPress));
        OnPropertyChanged(nameof(HasAnyGesture));
        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// Keeps the click surface in step with its draft: the same notifications the old
    /// per-field handlers raised, now driven by the draft the fields live on.
    /// </summary>
    private void OnClickSlotChanged(string? name)
    {
        switch (name)
        {
            case nameof(GestureSlotEditor.TargetKind):
                OnPropertyChanged(nameof(TargetKind));
                OnPropertyChanged(nameof(TargetDescription));
                OnPropertyChanged(nameof(NeedsDirection));
                OnPropertyChanged(nameof(ShowDirectionPicker));
                break;
            case nameof(GestureSlotEditor.SelectedKey):
                SetSelectedKeyOption(CurrentKeyOption());
                OnPropertyChanged(nameof(SelectedKey));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.ModifierCtrl):
            case nameof(GestureSlotEditor.ModifierShift):
            case nameof(GestureSlotEditor.ModifierAlt):
            case nameof(GestureSlotEditor.ModifierWin):
                OnPropertyChanged(name);
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.SelectedMouseButton):
                OnPropertyChanged(nameof(SelectedMouseButton));
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.MouseSettings):
                OnPropertyChanged(nameof(MouseSettings));
                OnPropertyChanged(nameof(ActivePrecisionButton));
                break;
            case nameof(GestureSlotEditor.ScrollSettings):
                OnPropertyChanged(nameof(ScrollSettings));
                break;
            case nameof(GestureSlotEditor.SpecialKind):
            case nameof(GestureSlotEditor.SpecialPrecisionMode):
            case nameof(GestureSlotEditor.SpecialSwitchMode):
            case nameof(GestureSlotEditor.SpecialSlotA):
            case nameof(GestureSlotEditor.SpecialSlotB):
                OnPropertyChanged(nameof(TargetDescription));
                break;
            case nameof(GestureSlotEditor.IsNone):
            case nameof(GestureSlotEditor.Function):
                OnPropertyChanged(nameof(ClickIsNone));
                OnPropertyChanged(nameof(TargetDescription));
                break;
        }

        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// Keeps the double-click surface in step with its draft. An action with no gesture can
    /// never resolve, so a draft that gained a function of its own enables the gesture.
    /// </summary>
    private void OnDoubleClickSlotChanged()
    {
        if (DoubleClickSlot.BuildAction() is not null)
        {
            DoubleClickEnabled = true;
        }

        SyncDoubleClickOption();
        OnPropertyChanged(nameof(DoubleClickAction));
        OnPropertyChanged(nameof(HasDoubleClickAction));
        OnPropertyChanged(nameof(DoubleClickActionDescription));
        NotifyActiveSlotChanged();
    }

    /// <summary>Keeps the long-press surface in step with its draft, as the double-click does.</summary>
    private void OnLongPressSlotChanged()
    {
        if (LongPressSlot.BuildAction() is not null)
        {
            LongPressEnabled = true;
            ClickRepeatEnabled = false;
        }

        SyncLongPressOption();
        OnPropertyChanged(nameof(LongPressAction));
        OnPropertyChanged(nameof(HasLongPressAction));
        OnPropertyChanged(nameof(LongPressActionDescription));
        NotifyActiveSlotChanged();
    }

    /// <summary>
    /// Re-reads every active-slot adapter. The adapters are thin views over
    /// <see cref="ActiveSlot"/>, so any slot edit, any enabled-flag change and any switcher
    /// move goes through here; the ones whose value did not change cost one binding
    /// evaluation and nothing else.
    /// </summary>
    private void NotifyActiveSlotChanged()
    {
        OnPropertyChanged(nameof(ActiveTargetKind));
        OnPropertyChanged(nameof(ActiveSelectedKey));
        OnPropertyChanged(nameof(ActiveModifierCtrl));
        OnPropertyChanged(nameof(ActiveModifierShift));
        OnPropertyChanged(nameof(ActiveModifierAlt));
        OnPropertyChanged(nameof(ActiveModifierWin));
        OnPropertyChanged(nameof(ActiveSelectedMouseButton));
        OnPropertyChanged(nameof(ActiveMouseSettings));
        OnPropertyChanged(nameof(ActivePrecisionButton));
        OnPropertyChanged(nameof(ActivePrecisionButtonLabel));
        OnPropertyChanged(nameof(ActiveScrollSettings));
        OnPropertyChanged(nameof(ActiveSpecialKind));
        OnPropertyChanged(nameof(ActiveSpecialKindOption));
        OnPropertyChanged(nameof(ActiveSpecialPrecisionMode));
        OnPropertyChanged(nameof(ActiveSpecialPrecisionModeOption));
        OnPropertyChanged(nameof(ActiveSpecialSwitchMode));
        OnPropertyChanged(nameof(ActiveSpecialSwitchModeOption));
        OnPropertyChanged(nameof(ActiveSpecialSlotA));
        OnPropertyChanged(nameof(ActiveSpecialSlotB));
        OnPropertyChanged(nameof(ShowSpecialLeaderControls));
        OnPropertyChanged(nameof(ShowSpecialPrecisionControls));
        OnPropertyChanged(nameof(SpecialKeySlotConflict));
        OnPropertyChanged(nameof(ActiveIsNone));
        OnPropertyChanged(nameof(ActiveRepeatsClick));
        OnPropertyChanged(nameof(ActiveGestureEnabled));
        OnPropertyChanged(nameof(ActiveGestureCanBeDisabled));
        OnPropertyChanged(nameof(ActiveGestureName));
        OnPropertyChanged(nameof(ActiveTargetDescription));
        OnPropertyChanged(nameof(ActiveTargetEditable));
        SetActiveSelectedKeyOption(CurrentActiveKeyOption());
    }

    private KeyPickerItem? CurrentActiveKeyOption()
        => KeyOptions.FirstOrDefault(
            option => !option.IsHeader && option.Key == ActiveSlot.SelectedKey);

    private void SetActiveSelectedKeyOption(KeyPickerItem? option)
    {
        if (EqualityComparer<KeyPickerItem?>.Default.Equals(option, _activeSelectedKeyOption))
        {
            return;
        }

        _activeSelectedKeyOption = option;
        OnPropertyChanged(nameof(ActiveSelectedKeyOption));
        FollowActiveKeyCategory(option);
    }

    /// <summary>
    /// Opens the category the newly selected key lives in. Every path that moves the active
    /// selection - a cap click, a gesture switch, a load, a reset - lands here, so the
    /// selected key is always the one in view.
    /// </summary>
    private void FollowActiveKeyCategory(KeyPickerItem? option)
    {
        if (option is null)
        {
            return;
        }

        var group = KeyCategoryGroups.FirstOrDefault(
            candidate => candidate.Keys.Any(item => item.Key == option.Key));

        if (group is not null)
        {
            ActiveKeyCategory = group;
        }
    }

    partial void OnActiveKeyCategoryChanged(KeyCategoryGroup? value)
    {
        // The navigator mirrors the selection: the newly active category's segment is checked
        // and every other one is unchecked, so a programmatic move - a gesture switch landing
        // on a key in another category - marks the same segment a click would.
        foreach (var group in KeyCategoryGroups)
        {
            if (!ReferenceEquals(group, value))
            {
                group.IsActive = false;
            }
        }

        if (value is not null)
        {
            value.IsActive = true;
        }
    }

    /// <summary>
    /// A navigator segment was checked by pointer or by keyboard: its category becomes the one
    /// the grid shows. Unchecking is ignored - exactly one category is shown at a time, and
    /// <see cref="OnActiveKeyCategoryChanged"/> is what clears the rest.
    /// </summary>
    private void OnKeyCategoryGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyCategoryGroup.IsActive)
            && sender is KeyCategoryGroup { IsActive: true } group)
        {
            ActiveKeyCategory = group;
        }
    }

    [RelayCommand]
    private void ClearDoubleClickAction()
    {
        DoubleClickEnabled = false;
        DoubleClickAction = null;
    }

    [RelayCommand]
    private void ClearLongPressAction()
    {
        LongPressEnabled = false;
        LongPressAction = null;
    }

    partial void OnIsCapturingButtonChanged(bool value)
        => OnPropertyChanged(nameof(CaptureButtonLabel));

    partial void OnIsCapturingKeyChanged(bool value)
        => OnPropertyChanged(nameof(CaptureKeyLabel));

    private void SelectButton(GamepadButton button, int code)
    {
        if (button == GamepadButton.Extra)
        {
            RegisterExtraButton(code);
        }

        SelectedButton = button;
        SelectedExtraCode = code;
    }

    private void RebuildButtonOptions()
    {
        ButtonOptions.Clear();

        foreach (var (name, buttons) in ButtonCategories.Groups)
        {
            ButtonOptions.Add(new ButtonPickerItem { Label = name, IsHeader = true });
            foreach (var button in buttons)
            {
                ButtonOptions.Add(OptionFor(button, 0));
            }
        }

        if (_extraButtonCodes.Count > 0)
        {
            ButtonOptions.Add(new ButtonPickerItem { Label = ButtonCategories.ExtensionGroupName, IsHeader = true });
            foreach (var code in _extraButtonCodes)
            {
                ButtonOptions.Add(OptionFor(GamepadButton.Extra, code));
            }
        }

        SetSelectedButtonOption(CurrentOption());
    }

    private void RebuildKeyOptions()
    {
        KeyOptions.Clear();

        foreach (var (name, keys) in KeyCategories.Groups)
        {
            KeyOptions.Add(new KeyPickerItem { Label = name, IsHeader = true });
            foreach (var key in keys)
            {
                KeyOptions.Add(KeyOptionFor(key));
            }
        }

        var grouped = KeyCategories.Grouped;
        var extended = Enum.GetValues<GamepadKey>().Where(key => !grouped.Contains(key)).ToArray();

        if (extended.Length > 0)
        {
            KeyOptions.Add(new KeyPickerItem { Label = KeyCategories.ExtensionGroupName, IsHeader = true });
            foreach (var key in extended)
            {
                KeyOptions.Add(KeyOptionFor(key));
            }
        }

        RebuildKeyCategoryGroups();
        SetSelectedKeyOption(CurrentKeyOption());
        SetActiveSelectedKeyOption(CurrentActiveKeyOption());
    }

    /// <summary>
    /// Rebuilds the category groups from the flat <see cref="KeyOptions"/> rows. The groups
    /// hold the same item instances the flat list carries, so a selection made through one
    /// is the selection the other reports. The navigator subscribes to each new group, so a
    /// segment's check reaches the editor.
    /// </summary>
    private void RebuildKeyCategoryGroups()
    {
        foreach (var stale in KeyCategoryGroups)
        {
            stale.PropertyChanged -= OnKeyCategoryGroupPropertyChanged;
        }

        KeyCategoryGroups.Clear();

        KeyCategoryGroup? group = null;

        foreach (var item in KeyOptions)
        {
            if (item.IsHeader)
            {
                group = new KeyCategoryGroup(item.Label);
                group.PropertyChanged += OnKeyCategoryGroupPropertyChanged;
                KeyCategoryGroups.Add(group);
            }
            else
            {
                group?.Keys.Add(item);
            }
        }

        // The first category leads until a selection says otherwise; the active option is
        // synced right after this and follows its key into the right group.
        ActiveKeyCategory = KeyCategoryGroups.FirstOrDefault();
    }

    private static KeyPickerItem KeyOptionFor(GamepadKey key)
        => new() { Label = MappingDescriptions.Key(key), Key = key };

    private KeyPickerItem? CurrentKeyOption()
        => KeyOptions.FirstOrDefault(
            option => !option.IsHeader && option.Key == SelectedKey);

    private static ButtonPickerItem OptionFor(GamepadButton button, int code)
        => new() { Label = MappingDescriptions.Button(button, code), Button = button, Code = code };

    private ButtonPickerItem? CurrentOption()
        => ButtonOptions.FirstOrDefault(
            option => !option.IsHeader && option.Button == SelectedButton && option.Code == SelectedExtraCode);

    private void SetSelectedButtonOption(ButtonPickerItem? option)
    {
        if (EqualityComparer<ButtonPickerItem?>.Default.Equals(option, _selectedButtonOption))
        {
            return;
        }

        _selectedButtonOption = option;
        OnPropertyChanged(nameof(SelectedButtonOption));
    }

    private void SetSelectedKeyOption(KeyPickerItem? option)
    {
        if (EqualityComparer<KeyPickerItem?>.Default.Equals(option, _selectedKeyOption))
        {
            return;
        }

        _selectedKeyOption = option;
        OnPropertyChanged(nameof(SelectedKeyOption));
    }

    private static GamepadButton? FirstPressed(GamepadButtonMask mask)
    {
        foreach (var button in ButtonCategories.All)
        {
            if ((mask & button.ToMask()) != 0)
            {
                return button;
            }
        }

        return null;
    }
}
