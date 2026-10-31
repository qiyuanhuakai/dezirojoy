using Avalonia.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views;

/// <summary>
/// Translates physical keyboard keys onto the portable <see cref="GamepadKey"/> vocabulary.
/// Lives in the view layer because <see cref="Avalonia.Input.Key"/> is an Avalonia type and the
/// editor view model is deliberately Avalonia-free.
/// </summary>
public static class KeyboardKeyMap
{
    public static bool IsModifier(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin => true,
        _ => false,
    };

    public static GamepadKey? ToGamepadKey(Key key) => key switch
    {
        Key.A => GamepadKey.A,
        Key.B => GamepadKey.B,
        Key.C => GamepadKey.C,
        Key.D => GamepadKey.D,
        Key.E => GamepadKey.E,
        Key.F => GamepadKey.F,
        Key.G => GamepadKey.G,
        Key.H => GamepadKey.H,
        Key.I => GamepadKey.I,
        Key.J => GamepadKey.J,
        Key.K => GamepadKey.K,
        Key.L => GamepadKey.L,
        Key.M => GamepadKey.M,
        Key.N => GamepadKey.N,
        Key.O => GamepadKey.O,
        Key.P => GamepadKey.P,
        Key.Q => GamepadKey.Q,
        Key.R => GamepadKey.R,
        Key.S => GamepadKey.S,
        Key.T => GamepadKey.T,
        Key.U => GamepadKey.U,
        Key.V => GamepadKey.V,
        Key.W => GamepadKey.W,
        Key.X => GamepadKey.X,
        Key.Y => GamepadKey.Y,
        Key.Z => GamepadKey.Z,
        Key.D0 => GamepadKey.D0,
        Key.D1 => GamepadKey.D1,
        Key.D2 => GamepadKey.D2,
        Key.D3 => GamepadKey.D3,
        Key.D4 => GamepadKey.D4,
        Key.D5 => GamepadKey.D5,
        Key.D6 => GamepadKey.D6,
        Key.D7 => GamepadKey.D7,
        Key.D8 => GamepadKey.D8,
        Key.D9 => GamepadKey.D9,
        >= Key.F1 and <= Key.F24 => GamepadKey.F1 + (key - Key.F1),
        >= Key.NumPad0 and <= Key.NumPad9 => GamepadKey.Numpad0 + (key - Key.NumPad0),
        Key.Escape => GamepadKey.Escape,
        Key.Enter => GamepadKey.Enter,
        Key.Space => GamepadKey.Space,
        Key.Tab => GamepadKey.Tab,
        Key.Back => GamepadKey.Backspace,
        Key.Delete => GamepadKey.Delete,
        Key.Insert => GamepadKey.Insert,
        Key.Home => GamepadKey.Home,
        Key.End => GamepadKey.End,
        Key.PageUp => GamepadKey.PageUp,
        Key.PageDown => GamepadKey.PageDown,
        Key.Up => GamepadKey.ArrowUp,
        Key.Down => GamepadKey.ArrowDown,
        Key.Left => GamepadKey.ArrowLeft,
        Key.Right => GamepadKey.ArrowRight,
        Key.LeftCtrl or Key.RightCtrl => GamepadKey.LeftCtrl,
        Key.LeftShift or Key.RightShift => GamepadKey.LeftShift,
        Key.LeftAlt or Key.RightAlt => GamepadKey.LeftAlt,
        Key.LWin or Key.RWin => GamepadKey.LeftWin,
        Key.OemMinus => GamepadKey.Minus,
        Key.OemPlus => GamepadKey.Equal,
        Key.OemComma => GamepadKey.Comma,
        Key.OemPeriod => GamepadKey.Period,
        Key.OemQuestion => GamepadKey.Slash,
        Key.OemSemicolon => GamepadKey.Semicolon,
        Key.OemQuotes => GamepadKey.Apostrophe,
        Key.OemOpenBrackets => GamepadKey.BracketLeft,
        Key.OemCloseBrackets => GamepadKey.BracketRight,
        Key.OemBackslash => GamepadKey.Backslash,
        Key.OemTilde => GamepadKey.Grave,
        _ => null,
    };
}
