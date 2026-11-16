namespace DeziroJoy.Core.Input;

/// <summary>
/// What the control panel calls a control, from the logical position alone.
/// <para>
/// Nothing here looks at a device name, a vendor id or an appearance database - the roadmap
/// forbids drawing or naming a pad by model. A platform that can read the glyph actually printed
/// on the device (SDL's button labels) supplies that instead, and
/// <see cref="Generic"/> is what is left when it cannot: an honest "we do not know what is
/// printed here", which is a better answer than guessing a brand.
/// </para>
/// </summary>
public static class GamepadControlLabels
{
    /// <summary>The logical name of a standard button, in the Xbox-letter convention.</summary>
    public static string Button(GamepadButton button) => button switch
    {
        GamepadButton.A => "A",
        GamepadButton.B => "B",
        GamepadButton.X => "X",
        GamepadButton.Y => "Y",
        GamepadButton.LeftBumper => "左肩键",
        GamepadButton.RightBumper => "右肩键",
        GamepadButton.LeftTrigger => "左扳机",
        GamepadButton.RightTrigger => "右扳机",
        GamepadButton.Back => "Back / Select",
        GamepadButton.Start => "Start / Options",
        GamepadButton.Guide => "Guide / Home",
        GamepadButton.LeftStick => "左摇杆按下",
        GamepadButton.RightStick => "右摇杆按下",
        GamepadButton.DPadUp => "十字键 上",
        GamepadButton.DPadDown => "十字键 下",
        GamepadButton.DPadLeft => "十字键 左",
        GamepadButton.DPadRight => "十字键 右",
        _ => "未知按钮",
    };

    /// <summary>The logical name of a stick.</summary>
    public static string Stick(GamepadStick stick) => stick == GamepadStick.Left ? "左摇杆" : "右摇杆";

    /// <summary>
    /// The device-agnostic fallback for a control no platform can name, counted from the raw
    /// control's own ordinal. <paramref name="ordinal"/> is the zero-based platform code the
    /// control is addressed by, so the number a user sees is the number a mapping string
    /// carries plus one - a user reading "按钮 8" can find the same control in the mapping.
    /// </summary>
    public static string Generic(int ordinal) => $"按钮 {ordinal + 1}";
}
