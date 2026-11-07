using CommunityToolkit.Mvvm.ComponentModel;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// What one gesture slot does. The three states a gesture action can be in: no function of
/// its own (the legacy null, which re-fires the click), 无功能, or a real target.
/// </summary>
public enum GestureSlotFunction
{
    /// <summary>重复单击的功能: no function of its own; the gesture re-fires the click.</summary>
    RepeatClick,

    /// <summary>无功能: the gesture resolves and emits nothing.</summary>
    None,

    /// <summary>A real target built from the shared editable fields.</summary>
    Target,
}

/// <summary>
/// The draft for one gesture slot. The click, the double-click and the long-press are equal
/// peers, so every slot is edited through the same fields: a target kind, a key, modifiers,
/// a mouse button and the two continuous settings, plus 无功能 and the legacy
/// repeat-the-click state. Three independent drafts live in the editor, so switching between
/// slots never loses an edit.
/// </summary>
/// <remarks>
/// A null action and a <see cref="NoneTarget"/> are different states and both round-trip:
/// null is the legacy shape every profile written before gesture actions existed has, and
/// 无功能 is an explicit choice that must survive a save and a reload unchanged.
/// </remarks>
public partial class GestureSlotEditor : ObservableObject
{
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

    /// <summary>
    /// Which special behavior a <see cref="MappingTargetKind.SpecialKey"/> target triggers.
    /// The defaults are the Core record's own, so a freshly chosen special key builds the
    /// same target a profile written with the defaults carries.
    /// </summary>
    [ObservableProperty]
    private SpecialKeyKind _specialKind = SpecialKeyKind.Precision;

    [ObservableProperty]
    private PrecisionMode _specialPrecisionMode = PrecisionMode.Toggle;

    [ObservableProperty]
    private LeaderSwitchMode _specialSwitchMode = LeaderSwitchMode.Press;

    [ObservableProperty]
    private int _specialSlotA = 1;

    [ObservableProperty]
    private int _specialSlotB = 2;

    /// <summary>
    /// Which of the three states the slot is in. Editing any target field gives the slot a
    /// function of its own, so a slot that was re-firing the click becomes a real target.
    /// </summary>
    [ObservableProperty]
    private GestureSlotFunction _function = GestureSlotFunction.Target;

    /// <summary>无功能: the slot resolves and emits nothing. Explicit, never a silent null.</summary>
    public bool IsNone
    {
        get => Function == GestureSlotFunction.None;
        set => Function = value ? GestureSlotFunction.None : GestureSlotFunction.Target;
    }

    /// <summary>重复单击的功能: the slot has no function of its own and re-fires the click.</summary>
    public bool RepeatsClick
    {
        get => Function == GestureSlotFunction.RepeatClick;
        set => Function = value ? GestureSlotFunction.RepeatClick : GestureSlotFunction.Target;
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
    /// The target the fields describe, 无功能 included. Never null: a slot re-firing the
    /// click has no target of its own, and its callers ask <see cref="BuildAction"/> instead.
    /// </summary>
    public MappingTarget BuildTarget() => Function == GestureSlotFunction.None
        ? new NoneTarget()
        : TargetKind switch
        {
            MappingTargetKind.KeyPress => new KeyPressTarget(SelectedKey),
            MappingTargetKind.KeyCombo => new KeyComboTarget(Modifiers, SelectedKey),
            MappingTargetKind.MouseButton => new MouseButtonTarget(SelectedMouseButton),
            MappingTargetKind.MouseMove => new MouseMoveTarget(MouseSettings),
            MappingTargetKind.WheelScroll => new WheelScrollTarget(ScrollSettings),
            MappingTargetKind.SpecialKey => new SpecialKeyTarget(
                SpecialKind,
                SpecialPrecisionMode,
                SpecialSwitchMode,
                SpecialSlotA,
                SpecialSlotB),
            _ => new KeyPressTarget(SelectedKey),
        };

    /// <summary>
    /// What the slot emits, or null when it re-fires the click - the behaviour every profile
    /// written before gesture actions existed already has.
    /// </summary>
    public MappingTarget? BuildAction() => Function == GestureSlotFunction.RepeatClick
        ? null
        : BuildTarget();

    /// <summary>
    /// Fills the draft from a loaded action. Null is the legacy repeat-the-click state; a
    /// <see cref="NoneTarget"/> is the explicit 无功能 state; every other target fills the
    /// matching fields. The kind is written before the key and the modifiers so loading a
    /// combo cannot have its modifiers cleared by the kind change.
    /// </summary>
    public void LoadAction(MappingTarget? action)
    {
        switch (action)
        {
            case null:
                Function = GestureSlotFunction.RepeatClick;
                break;
            case NoneTarget:
                // 无功能 leaves the slot with no function beyond it; the kind falls back to
                // the plain key press so no stale combo modifier can resurface when the
                // box is unchecked again.
                Function = GestureSlotFunction.None;
                TargetKind = MappingTargetKind.KeyPress;
                break;
            case KeyPressTarget key:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.KeyPress;
                SelectedKey = key.Key;
                break;
            case KeyComboTarget combo:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.KeyCombo;
                SelectedKey = combo.Key;
                ModifierCtrl = combo.Modifiers.HasFlag(KeyModifiers.Ctrl);
                ModifierShift = combo.Modifiers.HasFlag(KeyModifiers.Shift);
                ModifierAlt = combo.Modifiers.HasFlag(KeyModifiers.Alt);
                ModifierWin = combo.Modifiers.HasFlag(KeyModifiers.Win);
                break;
            case MouseButtonTarget mouseButton:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.MouseButton;
                SelectedMouseButton = mouseButton.Button;
                break;
            case MouseMoveTarget mouseMove:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.MouseMove;
                MouseSettings = mouseMove.Settings;
                break;
            case WheelScrollTarget wheelScroll:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.WheelScroll;
                ScrollSettings = wheelScroll.Settings;
                break;
            case SpecialKeyTarget special:
                Function = GestureSlotFunction.Target;
                TargetKind = MappingTargetKind.SpecialKey;
                SpecialKind = special.Kind;
                SpecialPrecisionMode = special.PrecisionMode;
                SpecialSwitchMode = special.SwitchMode;
                SpecialSlotA = special.SlotA;
                SpecialSlotB = special.SlotB;
                break;
        }
    }

    /// <summary>Back to a pristine draft: every field at its default, a real target.</summary>
    public void Reset()
    {
        Function = GestureSlotFunction.Target;
        TargetKind = MappingTargetKind.KeyPress;
        SelectedKey = GamepadKey.Enter;
        ModifierCtrl = false;
        ModifierShift = false;
        ModifierAlt = false;
        ModifierWin = false;
        SelectedMouseButton = MouseButton.Left;
        MouseSettings = new MouseStickSettings();
        ScrollSettings = new ScrollStickSettings();
        SpecialKind = SpecialKeyKind.Precision;
        SpecialPrecisionMode = PrecisionMode.Toggle;
        SpecialSwitchMode = LeaderSwitchMode.Press;
        SpecialSlotA = 1;
        SpecialSlotB = 2;
    }

    partial void OnTargetKindChanged(MappingTargetKind value)
    {
        ClaimFunction();

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

    partial void OnSelectedKeyChanged(GamepadKey value) => ClaimFunction();

    partial void OnModifierCtrlChanged(bool value) => ClaimFunction();

    partial void OnModifierShiftChanged(bool value) => ClaimFunction();

    partial void OnModifierAltChanged(bool value) => ClaimFunction();

    partial void OnModifierWinChanged(bool value) => ClaimFunction();

    partial void OnSelectedMouseButtonChanged(MouseButton value) => ClaimFunction();

    partial void OnMouseSettingsChanged(MouseStickSettings value) => ClaimFunction();

    partial void OnScrollSettingsChanged(ScrollStickSettings value) => ClaimFunction();

    partial void OnSpecialKindChanged(SpecialKeyKind value) => ClaimFunction();

    partial void OnSpecialPrecisionModeChanged(PrecisionMode value) => ClaimFunction();

    partial void OnSpecialSwitchModeChanged(LeaderSwitchMode value) => ClaimFunction();

    partial void OnSpecialSlotAChanged(int value) => ClaimFunction();

    partial void OnSpecialSlotBChanged(int value) => ClaimFunction();

    /// <summary>
    /// Editing any field is a choice of target, so a slot that was re-firing the click takes
    /// on a function of its own. 无功能 is never overridden this way: it is explicit.
    /// </summary>
    private void ClaimFunction()
    {
        if (Function == GestureSlotFunction.RepeatClick)
        {
            Function = GestureSlotFunction.Target;
        }
    }
}
