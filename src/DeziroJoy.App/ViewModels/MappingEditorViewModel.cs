using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

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
    private InputDirection? _loadedDirection;
    private GamepadButtonMask _previousCaptureButtons;

    public MappingEditorViewModel()
    {
        RebuildButtonOptions();
        RebuildKeyOptions();
    }

    [ObservableProperty]
    private bool _sourceIsStick;

    [ObservableProperty]
    private GamepadButton _selectedButton = GamepadButton.A;

    [ObservableProperty]
    private int _selectedExtraCode;

    [ObservableProperty]
    private GamepadStick _selectedStick = GamepadStick.Right;

    [ObservableProperty]
    private MappingTargetKind _targetKind = MappingTargetKind.KeyPress;

    [ObservableProperty]
    private IReadOnlyList<MappingTargetKind> _availableTargetKinds = AllTargetKinds;

    /// <summary>
    /// Direction for the two cross combinations. Ignored elsewhere, where a direction is
    /// meaningless: a stick driving mouse move / wheel consumes both axes, and a button
    /// driving a key / combo / mouse button has no axis to deflect.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDirectionOption))]
    private InputDirection _selectedDirection = InputDirection.Up;

    [ObservableProperty]
    private float _doubleClickWindow;

    [ObservableProperty]
    private float _longPressThreshold;

    [ObservableProperty]
    private float _longPressRepeatInterval;

    [ObservableProperty]
    private GamepadKey _selectedKey = GamepadKey.Enter;

    [ObservableProperty]
    private bool _modifierCtrl;

    [ObservableProperty]
    private bool _modifierShift;

    [ObservableProperty]
    private bool _modifierAlt;

    [ObservableProperty]
    private bool _modifierWin;

    [ObservableProperty]
    private MouseButton _selectedMouseButton = MouseButton.Left;

    [ObservableProperty]
    private MouseStickSettings _mouseSettings = new();

    [ObservableProperty]
    private ScrollStickSettings _scrollSettings = new();

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

    public bool HasDoubleClick => DoubleClickWindow > 0f;

    public bool HasLongPress => LongPressThreshold > 0f;

    /// <summary>
    /// True when any gesture is configured, i.e. when the engine must introduce a wait before
    /// the single action can fire. False keeps the zero-latency single-click path.
    /// </summary>
    public bool HasAnyGesture => HasDoubleClick || HasLongPress;

    public string CaptureButtonLabel => IsCapturingButton ? "停止捕获" : "捕获按键";

    public string CaptureKeyLabel => IsCapturingKey ? "停止捕获" : "捕获键盘";

    /// <summary>What the model element being edited is called, e.g. "左肩键 LB".</summary>
    public string SourceDescription => MappingDescriptions.Source(BuildSource());

    /// <summary>What the chosen target does, e.g. "Ctrl + C".</summary>
    public string TargetDescription => MappingDescriptions.Target(BuildTarget());

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

    public KeyModifiers Modifiers
    {
        get
        {
            var modifiers = KeyModifiers.None;
            if (ModifierCtrl)
            {
                modifiers |= KeyModifiers.Ctrl;
            }

            if (ModifierShift)
            {
                modifiers |= KeyModifiers.Shift;
            }

            if (ModifierAlt)
            {
                modifiers |= KeyModifiers.Alt;
            }

            if (ModifierWin)
            {
                modifiers |= KeyModifiers.Win;
            }

            return modifiers;
        }
    }

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
    /// no added latency and keeps ordinary profiles free of gesture JSON.
    /// </summary>
    public ButtonGesture? BuildGesture() => HasAnyGesture
        ? new ButtonGesture
        {
            DoubleClickWindow = DoubleClickWindow,
            LongPressThreshold = LongPressThreshold,
            LongPressRepeatInterval = LongPressRepeatInterval,
        }
        : null;

    public MappingTarget BuildTarget() => TargetKind switch
    {
        MappingTargetKind.KeyPress => new KeyPressTarget(SelectedKey),
        MappingTargetKind.KeyCombo => new KeyComboTarget(Modifiers, SelectedKey),
        MappingTargetKind.MouseButton => new MouseButtonTarget(SelectedMouseButton),
        MappingTargetKind.MouseMove => new MouseMoveTarget(MouseSettings),
        MappingTargetKind.WheelScroll => new WheelScrollTarget(ScrollSettings),
        _ => new KeyPressTarget(SelectedKey),
    };

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
                DoubleClickWindow = button.Gesture?.DoubleClickWindow ?? 0f;
                LongPressThreshold = button.Gesture?.LongPressThreshold ?? 0f;
                LongPressRepeatInterval = button.Gesture?.LongPressRepeatInterval ?? 0f;
                break;
            case StickBinding stick:
                SourceIsStick = true;
                SelectedStick = stick.Stick;
                _loadedDirection = stick.Direction;
                SelectedDirection = stick.Direction ?? InputDirection.Up;
                break;
        }

        switch (entry.Target)
        {
            case KeyPressTarget key:
                TargetKind = MappingTargetKind.KeyPress;
                SelectedKey = key.Key;
                break;
            case KeyComboTarget combo:
                TargetKind = MappingTargetKind.KeyCombo;
                SelectedKey = combo.Key;
                ModifierCtrl = combo.Modifiers.HasFlag(KeyModifiers.Ctrl);
                ModifierShift = combo.Modifiers.HasFlag(KeyModifiers.Shift);
                ModifierAlt = combo.Modifiers.HasFlag(KeyModifiers.Alt);
                ModifierWin = combo.Modifiers.HasFlag(KeyModifiers.Win);
                break;
            case MouseButtonTarget mouseButton:
                TargetKind = MappingTargetKind.MouseButton;
                SelectedMouseButton = mouseButton.Button;
                break;
            case MouseMoveTarget mouseMove:
                TargetKind = MappingTargetKind.MouseMove;
                MouseSettings = mouseMove.Settings;
                break;
            case WheelScrollTarget wheelScroll:
                TargetKind = MappingTargetKind.WheelScroll;
                ScrollSettings = wheelScroll.Settings;
                break;
        }
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
        TargetKind = MappingTargetKind.KeyPress;
        SelectedKey = GamepadKey.Enter;
        SelectedDirection = InputDirection.Up;
        DoubleClickWindow = 0f;
        LongPressThreshold = 0f;
        LongPressRepeatInterval = 0f;
        ModifierCtrl = false;
        ModifierShift = false;
        ModifierAlt = false;
        ModifierWin = false;
        SelectedMouseButton = MouseButton.Left;
        MouseSettings = new MouseStickSettings();
        ScrollSettings = new ScrollStickSettings();
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

    partial void OnTargetKindChanged(MappingTargetKind value)
    {
        OnPropertyChanged(nameof(TargetDescription));
        OnPropertyChanged(nameof(NeedsDirection));
        OnPropertyChanged(nameof(ShowDirectionPicker));

        // A combo is the only shape that reads the modifier flags, so leaving it must clear
        // them; otherwise a stale Ctrl would resurface the next time a combo is chosen.
        if (value != MappingTargetKind.KeyCombo)
        {
            ModifierCtrl = false;
            ModifierShift = false;
            ModifierAlt = false;
            ModifierWin = false;
        }
    }

    partial void OnDoubleClickWindowChanged(float value)
    {
        OnPropertyChanged(nameof(HasDoubleClick));
        OnPropertyChanged(nameof(HasAnyGesture));
    }

    partial void OnLongPressThresholdChanged(float value)
    {
        OnPropertyChanged(nameof(HasLongPress));
        OnPropertyChanged(nameof(HasAnyGesture));
    }

    partial void OnSelectedKeyChanged(GamepadKey value)
    {
        SetSelectedKeyOption(CurrentKeyOption());
        OnPropertyChanged(nameof(TargetDescription));
    }

    partial void OnModifierCtrlChanged(bool value) => OnPropertyChanged(nameof(TargetDescription));

    partial void OnModifierShiftChanged(bool value) => OnPropertyChanged(nameof(TargetDescription));

    partial void OnModifierAltChanged(bool value) => OnPropertyChanged(nameof(TargetDescription));

    partial void OnModifierWinChanged(bool value) => OnPropertyChanged(nameof(TargetDescription));

    partial void OnSelectedMouseButtonChanged(MouseButton value)
        => OnPropertyChanged(nameof(TargetDescription));

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
