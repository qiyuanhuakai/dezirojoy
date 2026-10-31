using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Linux.Evdev;

/// <summary>Maps evdev BTN_* codes onto the portable logical buttons (physical positions).</summary>
public static class EvdevButtonMap
{
    public static GamepadButton? ToButton(ushort code) => code switch
    {
        EvdevCodes.BtnSouth => GamepadButton.A,
        EvdevCodes.BtnEast => GamepadButton.B,
        EvdevCodes.BtnWest => GamepadButton.X,
        EvdevCodes.BtnNorth => GamepadButton.Y,
        EvdevCodes.BtnTl => GamepadButton.LeftBumper,
        EvdevCodes.BtnTr => GamepadButton.RightBumper,
        EvdevCodes.BtnTl2 => GamepadButton.LeftTrigger,
        EvdevCodes.BtnTr2 => GamepadButton.RightTrigger,
        EvdevCodes.BtnSelect => GamepadButton.Back,
        EvdevCodes.BtnStart => GamepadButton.Start,
        EvdevCodes.BtnMode => GamepadButton.Guide,
        EvdevCodes.BtnThumbL => GamepadButton.LeftStick,
        EvdevCodes.BtnThumbR => GamepadButton.RightStick,
        _ => null,
    };
}
