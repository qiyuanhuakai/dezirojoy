using System.Globalization;
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
        ButtonBinding button => $"{Button(button.Button, button.Code)}{DirectionSuffix(button.Direction)}",
        StickBinding stick => $"{Stick(stick.Stick)}{DirectionSuffix(stick.Direction)}",
        _ => binding.ToString() ?? string.Empty,
    };

    /// <summary>
    /// The direction a source carries, named exactly as the direction picker names it, so the
    /// editor's header and the mapping rows can never disagree with the picker about which
    /// deflection a mapping uses. Only the two cross combinations carry one - a stick driving
    /// a digital target, a button driving a continuous one - so a native pairing reads as the
    /// source alone.
    /// </summary>
    private static string DirectionSuffix(InputDirection? direction)
        => direction is { } value ? $"（{DirectionCategories.Label(value)}）" : string.Empty;

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
        ComplexShortcutTarget complex => ComplexShortcut(complex),
        _ => target.ToString() ?? string.Empty,
    };

    /// <summary>
    /// What a complex shortcut does, in plain language: the kind plus the steps the script
    /// compiles to. The script is DSL text, so its raw form would read as code rather than
    /// as behaviour; the step list is what the user actually gets. A script that does not
    /// parse - a hand-edited profile the engine refuses to build - reads as the kind alone
    /// rather than leaking the parse failure into a mapping row.
    /// </summary>
    public static string ComplexShortcut(ComplexShortcutTarget target)
    {
        IReadOnlyList<ComplexShortcutStep> steps;
        try
        {
            steps = ComplexShortcutDsl.Parse(target.Script ?? string.Empty);
        }
        catch (ComplexShortcutParseException)
        {
            return "复杂快捷键";
        }

        return $"复杂快捷键：{ComplexShortcutSteps(steps)}";
    }

    /// <summary>The compiled steps as one plain-language sentence, joined in execution order.</summary>
    public static string ComplexShortcutSteps(IReadOnlyList<ComplexShortcutStep> steps)
        => string.Join("；", steps.Select(DescribeStep));

    private static string DescribeStep(ComplexShortcutStep step) => step switch
    {
        ComplexShortcutTapStep tap => $"点按 {Key(tap.Key)}",
        ComplexShortcutHoldStep hold => $"持续按住 {Key(hold.Key)}",
        ComplexShortcutReleaseStep release => $"松开 {Key(release.Key)}",
        ComplexShortcutWaitStep wait => $"等待 {Seconds(wait.Seconds)}",
        ComplexShortcutEveryStep every => $"每 {Seconds(every.Interval)}点按 {Key(every.Key)}",
        _ => step.ToString() ?? string.Empty,
    };

    /// <summary>A duration in seconds, trimmed to at most three decimals so 1s reads 1 秒 and 500ms reads 0.5 秒.</summary>
    private static string Seconds(double seconds)
        => $"{seconds.ToString("0.###", CultureInfo.InvariantCulture)} 秒";

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

    /// <summary>
    /// The project's headline differentiator, stated where the user can see it: speed is a
    /// physical quantity, so the same number feels the same on every mouse and every display.
    /// </summary>
    public static string PhysicalSpeedUnit()
        => "速度以物理单位「像素/秒 @ 满幅」表示，与鼠标 DPI、显示器分辨率完全解耦：换鼠标、换屏幕，同一数值手感不变。";

    public static string MouseMaxSpeed()
        => "摇杆推到底（满幅）时的指针速度，单位 像素/秒。这是物理速度而非百分比，因此与鼠标 DPI 和屏幕分辨率无关。";

    public static string MouseDeadZone()
        => "以摇杆中心为圆心的圆形（径向）死区：幅度不超过设定值的输入被完全丢弃，消除静差漂移；超出部分按比例放大到满幅，所以最高速度不变，只损失中心附近的微调精度。";

    public static string MouseCurve(ResponseCurve curve) => curve switch
    {
        ResponseCurve.Linear => "线性：输出 = 输入，1:1 响应。中心最灵敏，但精细瞄准最难。",
        ResponseCurve.Precise => "精准（平滑曲线 3m²−2m³）：中心附近细腻，中段迅速提速，默认选项。",
        ResponseCurve.Aggressive => "激进（二次曲线 m²）：中心极细腻，要推得很大才到最高速。",
        ResponseCurve.Responsive => "灵敏（平方根曲线 √m）：一推就有反应，中心附近较粗。",
        _ => curve.ToString(),
    };

    public static string MouseCurveStrength()
        => "曲线与线性的混合比例：0 = 纯线性（等同「线性」曲线），1 = 完整曲线，大于 1 = 曲线效果加倍、更极端。";

    public static string MouseResponseTime()
        => "指数平滑时间常数（秒）：速度按 1−e^(−Δt/τ) 逼近目标，τ 越大越平稳但越滞后；0 = 立即响应（原始摇杆）。与帧率无关。";

    public static string MousePrecisionScale()
        => "按住精准键（默认左摇杆按下）期间的速度倍率：0.35 即降到 35%，用于临时精细瞄准。";

    public static string MousePrecisionMode()
        => "精准键触发方式：一直按住才降速，或按一下启用、再按一下取消；方式随当前映射一起保存。";

    public static string MouseScaleX()
        => "水平速度倍率：只改变左右移动的增益，不改变垂直速度；1 为原始速度，0 为关闭水平移动。";

    public static string MouseScaleY()
        => "垂直速度倍率：只改变上下移动的增益，在“垂直取反”之前应用；1 为原始速度，0 为关闭垂直移动。";

    public static string MouseInvertY()
        => "垂直位移取反：默认勾选，推摇杆向上 = 光标向上；取消勾选则上下颠倒。";

    public static string ScrollRate()
        => "摇杆推到底（满幅）时的滚动速率，单位 格/秒（1 格 = 120 个单位）。不足一格的量会累积，慢速推杆也能滚动。";

    public static string ScrollDeadZone()
        => "以摇杆中心为圆心的圆形（径向）死区：幅度不超过设定值的输入被完全丢弃；超出部分按比例放大到满幅，所以最高滚动速率不变，只损失中心附近的微调精度。";

    public static string ScrollCurve(ResponseCurve curve) => curve switch
    {
        ResponseCurve.Linear => "线性：输出 = 输入，1:1 响应。中心最灵敏，但精细滚动最难。",
        ResponseCurve.Precise => "精准（平滑曲线 3m²−2m³）：中心附近细腻，中段迅速提速，默认选项。",
        ResponseCurve.Aggressive => "激进（二次曲线 m²）：中心极细腻，要推得很大才到最高速。",
        ResponseCurve.Responsive => "灵敏（平方根曲线 √m）：一推就有反应，中心附近较粗。",
        _ => curve.ToString(),
    };

    public static string ScrollResponseTime()
        => "指数平滑时间常数（秒）：滚动速率按 1−e^(−Δt/τ) 逼近目标，τ 越大越平稳但越滞后；0 = 立即响应（原始摇杆）。与帧率无关。";

    public static string ScrollInvertY()
        => "垂直滚动方向取反：默认不勾选，推摇杆向上 = 页面向上滚；勾选后变为向下滚。";

    public static string ScrollEnableHorizontal()
        => "勾选时摇杆的水平分量产生水平滚动事件；取消则完全忽略水平输入，只保留垂直滚动。";
}
