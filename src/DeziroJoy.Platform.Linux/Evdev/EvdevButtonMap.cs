using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>Maps evdev BTN_* codes onto the portable logical buttons (physical positions).</summary>
/// <remarks>
/// Codes deliberately absent from this table, and why:
/// <list type="bullet">
/// <item>BTN_C 0x132 / BTN_Z 0x135 - the Xbox layout has four face buttons, so C and Z have no position.</item>
/// <item>BTN_GEAR_DOWN 0x150 / BTN_GEAR_UP 0x151 - 0x150 is dual-named BTN_WHEEL in the kernel
/// header, so it is not reliably a menu button; collapsing it onto Back would also lose the raw code.</item>
/// <item>BTN_TRIGGER_HAPPY1..40 0x2c0..0x2e7 - no logical equivalent exists.</item>
/// </list>
/// All of them still reach the app as raw-code extra buttons via
/// <see cref="IGamepadState.ExtraButtons"/>, so nothing is silently dropped.
/// </remarks>
public static class EvdevButtonMap
{
    public static GamepadButton? ToButton(ushort code) => code switch
    {
        EvdevCodes.BtnSouth => GamepadButton.A,
        EvdevCodes.BtnEast => GamepadButton.B,
        EvdevCodes.BtnNorth => GamepadButton.X,
        EvdevCodes.BtnWest => GamepadButton.Y,
        EvdevCodes.BtnTl => GamepadButton.LeftBumper,
        EvdevCodes.BtnTr => GamepadButton.RightBumper,
        EvdevCodes.BtnTl2 => GamepadButton.LeftTrigger,
        EvdevCodes.BtnTr2 => GamepadButton.RightTrigger,
        EvdevCodes.BtnSelect => GamepadButton.Back,
        EvdevCodes.BtnStart => GamepadButton.Start,
        EvdevCodes.BtnMode => GamepadButton.Guide,
        EvdevCodes.BtnThumbL => GamepadButton.LeftStick,
        EvdevCodes.BtnThumbR => GamepadButton.RightStick,
        EvdevCodes.BtnDpadUp => GamepadButton.DPadUp,
        EvdevCodes.BtnDpadDown => GamepadButton.DPadDown,
        EvdevCodes.BtnDpadLeft => GamepadButton.DPadLeft,
        EvdevCodes.BtnDpadRight => GamepadButton.DPadRight,
        _ => null,
    };
}
