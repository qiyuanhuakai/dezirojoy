using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.ViewModels;

/// <summary>Chinese display text for mappings (the UI is Chinese-first).</summary>
public static class MappingDescriptions
{
    public static string Button(GamepadButton button, int code = 0) => button switch
    {
        GamepadButton.A => "A 键",
        GamepadButton.B => "B 键",
        GamepadButton.X => "X 键",
        GamepadButton.Y => "Y 键",
        GamepadButton.LeftBumper => "左肩键 LB",
        GamepadButton.RightBumper => "右肩键 RB",
        GamepadButton.LeftTrigger => "左扳机 LT",
        GamepadButton.RightTrigger => "右扳机 RT",
        GamepadButton.Back => "Back 键",
        GamepadButton.Start => "Start 键",
        GamepadButton.Guide => "Guide 键",
        GamepadButton.LeftStick => "左摇杆按下",
        GamepadButton.RightStick => "右摇杆按下",
        GamepadButton.DPadUp => "十字键 ↑",
        GamepadButton.DPadDown => "十字键 ↓",
        GamepadButton.DPadLeft => "十字键 ←",
        GamepadButton.DPadRight => "十字键 →",
        GamepadButton.Extra => code > 0 ? $"按键 {code}" : "扩展按键",
        _ => button.ToString(),
    };

    public static string Stick(GamepadStick stick) => stick == GamepadStick.Left ? "左摇杆" : "右摇杆";

    public static string Source(InputBinding binding) => binding switch
    {
        ButtonBinding button => Button(button.Button, button.Code),
        StickBinding stick => Stick(stick.Stick),
        _ => binding.ToString() ?? string.Empty,
    };

    public static string Target(MappingTarget target) => target switch
    {
        KeyPressTarget key => Key(key.Key),
        KeyComboTarget combo => $"{Modifiers(combo.Modifiers)} + {Key(combo.Key)}",
        MouseButtonTarget mouse => $"鼠标{mouse.Button switch
        {
            MouseButton.Left => "左键",
            MouseButton.Right => "右键",
            MouseButton.Middle => "中键",
            MouseButton.XButton1 => "侧键1",
            MouseButton.XButton2 => "侧键2",
            _ => "按键",
        }}",
        MouseMoveTarget => "鼠标移动",
        WheelScrollTarget => "滚轮滚动",
        _ => target.ToString() ?? string.Empty,
    };

    public static string Key(GamepadKey key) => key switch
    {
        GamepadKey.A or GamepadKey.B or GamepadKey.C or GamepadKey.D or GamepadKey.E or GamepadKey.F
            or GamepadKey.G or GamepadKey.H or GamepadKey.I or GamepadKey.J or GamepadKey.K or GamepadKey.L
            or GamepadKey.M or GamepadKey.N or GamepadKey.O or GamepadKey.P or GamepadKey.Q or GamepadKey.R
            or GamepadKey.S or GamepadKey.T or GamepadKey.U or GamepadKey.V or GamepadKey.W or GamepadKey.X
            or GamepadKey.Y or GamepadKey.Z => key.ToString(),
        >= GamepadKey.D0 and <= GamepadKey.D9 => ((char)('0' + (key - GamepadKey.D0))).ToString(),
        GamepadKey.Enter => "Enter",
        GamepadKey.Escape => "Esc",
        GamepadKey.Space => "Space",
        GamepadKey.Tab => "Tab",
        GamepadKey.Backspace => "Backspace",
        GamepadKey.Delete => "Delete",
        GamepadKey.Insert => "Insert",
        GamepadKey.Home => "Home",
        GamepadKey.End => "End",
        GamepadKey.PageUp => "PageUp",
        GamepadKey.PageDown => "PageDown",
        GamepadKey.ArrowUp => "↑",
        GamepadKey.ArrowDown => "↓",
        GamepadKey.ArrowLeft => "←",
        GamepadKey.ArrowRight => "→",
        GamepadKey.LeftCtrl => "Ctrl",
        GamepadKey.LeftShift => "Shift",
        GamepadKey.LeftAlt => "Alt",
        GamepadKey.LeftWin => "Win",
        >= GamepadKey.F1 and <= GamepadKey.F24 => key.ToString(),
        _ => key.ToString(),
    };

    public static string Modifiers(KeyModifiers modifiers)
    {
        if (modifiers == KeyModifiers.None)
        {
            return "无修饰键";
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Win))
        {
            parts.Add("Win");
        }

        return string.Join(" + ", parts);
    }

    public static string Layout(GamepadLayout layout) => layout switch
    {
        GamepadLayout.Xbox => "Xbox 布局",
        GamepadLayout.DualShock => "DualShock 布局",
        _ => "通用布局",
    };
}
