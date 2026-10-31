using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Editing surface for one mapping: pick the source (button or stick) and the target
/// (single key, combo, mouse button, stick-to-mouse, stick-to-wheel).
/// </summary>
public partial class MappingEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _sourceIsStick;

    [ObservableProperty]
    private GamepadButton _selectedButton = GamepadButton.A;

    [ObservableProperty]
    private GamepadStick _selectedStick = GamepadStick.Right;

    [ObservableProperty]
    private MappingTargetKind _targetKind = MappingTargetKind.KeyPress;

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

    public InputBinding BuildSource()
        => SourceIsStick ? new StickBinding(SelectedStick) : new ButtonBinding(SelectedButton);

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
                SelectedButton = button.Button;
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
        SourceIsStick = false;
        SelectedButton = GamepadButton.A;
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
}
