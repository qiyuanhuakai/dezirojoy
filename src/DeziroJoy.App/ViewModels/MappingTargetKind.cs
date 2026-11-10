namespace DeziroJoy.App.ViewModels;

public enum MappingTargetKind
{
    KeyPress,
    KeyCombo,
    MouseButton,
    MouseMove,
    WheelScroll,
    SpecialKey,

    /// <summary>
    /// 复杂快捷键: a hand-written DSL program the long press runs. The engine binds it to
    /// the mapping's own target with a bare long press, so the editor offers this kind on
    /// the long-press tab alone and translates the draft into that shape on commit.
    /// </summary>
    ComplexShortcut,
}
