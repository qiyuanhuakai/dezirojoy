using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Views.Controls;

/// <summary>Which physical half of the gamepad a mapping source lives on.</summary>
public enum MappingSide
{
    Left,
    Right,
}

/// <summary>
/// Classifies a mapping source by the half of the gamepad it sits on, so the mapping surface
/// can flank the model with a left column and a right column the way the reference layout
/// reads: the face buttons, right bumper/trigger, right stick and Start on the right;
/// everything else on the left.
/// </summary>
public static class MappingLayout
{
    public static MappingSide SideOf(InputBinding source) => source switch
    {
        ButtonBinding button => SideOf(button.Button),
        StickBinding stick => stick.Stick == GamepadStick.Left ? MappingSide.Left : MappingSide.Right,
        _ => MappingSide.Left,
    };

    private static MappingSide SideOf(GamepadButton button) => button switch
    {
        GamepadButton.A or GamepadButton.B or GamepadButton.X or GamepadButton.Y
            or GamepadButton.RightBumper or GamepadButton.RightTrigger
            or GamepadButton.RightStick or GamepadButton.Start => MappingSide.Right,
        _ => MappingSide.Left,
    };
}
