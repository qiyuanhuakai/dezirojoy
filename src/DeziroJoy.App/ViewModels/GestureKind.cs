namespace DeziroJoy.App.ViewModels;

/// <summary>
/// Which gesture the editor is configuring. The three are equal peers reached through one
/// switcher: the click is the mapping's own target, the double-click and the long-press are
/// the two gestures a button can carry on top of it.
/// </summary>
public enum GestureKind
{
    Click,
    DoubleClick,
    LongPress,
}
