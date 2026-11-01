using System.Collections.ObjectModel;
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
    /// Every target kind, for both source kinds. The engine honors all ten source x target
    /// combinations, so narrowing the list per source would only hide mappings that work.
    /// </summary>
    private static readonly MappingTargetKind[] AllTargetKinds =
    [
        MappingTargetKind.KeyPress,
        MappingTargetKind.KeyCombo,
        MappingTargetKind.MouseButton,
        MappingTargetKind.MouseMove,
        MappingTargetKind.WheelScroll,
    ];

    private readonly SortedSet<int> _extraButtonCodes = [];
    private readonly SortedSet<int> _previousCaptureExtra = [];
    private ButtonPickerItem? _selectedButtonOption;
    private KeyPickerItem? _selectedKeyOption;
    private DirectionPickerItem? _selectedDirectionOption;
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
    /// driving a key / combo / mouse button has no axis to deflect.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDirectionOption))]
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
    /// Seconds between long-press activations while the button is held. No longer
    /// user-editable - the timing sliders are gone - but a profile that carries one keeps it
    /// through a round trip.
    /// </summary>
    public float LongPressRepeatInterval { get; set; }

    /// <summary>
    /// Seconds between click auto-repeat activations while the button is held. No longer
    /// user-editable - the timing sliders are gone - but a profile that carries one keeps it
    /// through a round trip; 0 means the pinned
    /// <see cref="ButtonGesture.DefaultClickRepeatInterval"/>.
    /// </summary>
    public float ClickRepeatInterval { get; set; }

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
    /// from it needs to know which way it deflects.
    /// </summary>
    public bool NeedsDirection => SourceIsStick
        ? TargetKind is MappingTargetKind.KeyPress or MappingTargetKind.KeyCombo or MappingTargetKind.MouseButton
        : TargetKind is MappingTargetKind.MouseMove or MappingTargetKind.WheelScroll;

    public bool ShowDirectionPicker => NeedsDirection;

    /// <summary>The selected direction picker row, kept in step with <see cref="SelectedDirection"/>.</summary>
    public DirectionPickerItem? SelectedDirectionOption
    {
        get => _selectedDirectionOption;
        set
        {
            if (value is null || EqualityComparer<DirectionPickerItem?>.Default.Equals(value, _selectedDirectionOption))
            {
                return;
            }

            _selectedDirectionOption = value;
            SelectedDirection = value.Direction;
            OnPropertyChanged(nameof(SelectedDirectionOption));
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
    public string TargetDescription => ClickIsNone
        ? "无功能"
        : MappingDescriptions.Target(BuildTarget());

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
    /// becomes a single-key target; any modifier turns it into a combo target.
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
        TargetKind = modifiers == KeyModifiers.None ? MappingTargetKind.KeyPress : MappingTargetKind.KeyCombo;
        SelectedKey = key;
        ModifierCtrl = modifiers.HasFlag(KeyModifiers.Ctrl);
        ModifierShift = modifiers.HasFlag(KeyModifiers.Shift);
        ModifierAlt = modifiers.HasFlag(KeyModifiers.Alt);
        ModifierWin = modifiers.HasFlag(KeyModifiers.Win);
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
    }

    partial void OnDoubleClickEnabledChanged(bool value)
    {
        SyncDoubleClickOption();
        OnPropertyChanged(nameof(HasDoubleClick));
        OnPropertyChanged(nameof(HasAnyGesture));
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
                break;
            case nameof(GestureSlotEditor.ScrollSettings):
                OnPropertyChanged(nameof(ScrollSettings));
                break;
            case nameof(GestureSlotEditor.IsNone):
            case nameof(GestureSlotEditor.Function):
                OnPropertyChanged(nameof(ClickIsNone));
                OnPropertyChanged(nameof(TargetDescription));
                break;
        }
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

        SetSelectedKeyOption(CurrentKeyOption());
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
