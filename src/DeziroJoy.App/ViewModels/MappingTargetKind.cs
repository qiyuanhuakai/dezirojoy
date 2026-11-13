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
    /// 输入文本: types a short Unicode string on the press edge, the shape a search box, a
    /// file rename or a short URL needs. It is a tap by nature, so it holds nothing and
    /// repeats nothing.
    /// </summary>
    Text,

    /// <summary>
    /// 复杂快捷键: a hand-written DSL program the engine runs. Any of the three positions -
    /// the click, the double-click and the long press - can carry one; the position is the
    /// program's trigger, which is why the editor offers the kind on every tab of a button
    /// source and on none of a stick's (a stick has no gestures to start it).
    /// </summary>
    ComplexShortcut,
}
