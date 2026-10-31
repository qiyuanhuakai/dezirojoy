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
/// cannot present a stick picker next to a button picker. The target list is narrowed the
/// same way: a stick source only offers the two targets the engine honors for a stick, and
/// a button source only the three it honors for a button.
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
    private static readonly MappingTargetKind[] ButtonTargetKinds =
        [MappingTargetKind.KeyPress, MappingTargetKind.KeyCombo, MappingTargetKind.MouseButton];

    private static readonly MappingTargetKind[] StickTargetKinds =
        [MappingTargetKind.MouseMove, MappingTargetKind.WheelScroll];

    private readonly SortedSet<int> _extraButtonCodes = [];
    private readonly SortedSet<int> _previousCaptureExtra = [];
    private ButtonPickerItem? _selectedButtonOption;
    private KeyPickerItem? _selectedKeyOption;
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
    private IReadOnlyList<MappingTargetKind> _availableTargetKinds = ButtonTargetKinds;

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
        => SourceIsStick ? new StickBinding(SelectedStick) : new ButtonBinding(SelectedButton, SelectedExtraCode);

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
                break;
            case StickBinding stick:
                SourceIsStick = true;
                SelectedStick = stick.Stick;
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
        SourceIsStick = false;
        SelectedButton = GamepadButton.A;
        SelectedExtraCode = 0;
        SelectedStick = GamepadStick.Right;
        TargetKind = MappingTargetKind.KeyPress;
        SelectedKey = GamepadKey.Enter;
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
    /// capture is disarmed so arming it never fires on an already-held button.
    /// </summary>
    public void CaptureGamepadSample(IGamepadState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var pressed = state.PressedButtons;
        var edges = pressed & ~_previousCaptureButtons;

        if (IsCapturingButton)
        {
            var edge = FirstPressed(edges);
            if (edge is not null)
            {
                SelectButton(edge.Value, 0);
                IsCapturingButton = false;
            }
            else
            {
                foreach (var code in state.ExtraButtons)
                {
                    if (!_previousCaptureExtra.Contains(code))
                    {
                        SelectButton(GamepadButton.Extra, code);
                        IsCapturingButton = false;
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

        var kinds = value ? StickTargetKinds : ButtonTargetKinds;
        AvailableTargetKinds = kinds;

        if (!kinds.Contains(TargetKind))
        {
            TargetKind = kinds[0];
        }
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
