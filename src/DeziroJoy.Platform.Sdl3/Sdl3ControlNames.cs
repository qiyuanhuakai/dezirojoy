using SDL3;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// SDL's standard gamepad controls, addressed by the key a mapping string uses for them.
/// <para>
/// The key vocabulary is shared on purpose: <see cref="Sdl3MappingStep"/> writes mappings with
/// these keys, the mapping-supplement flow's device evidence lists them, and the control
/// description reads them back. One spelling means a control a user mapped by hand and a control
/// SDL declares are recognizably the same thing, and a mapping produced by the capture flow
/// therefore describes correctly the moment SDL accepts it.
/// </para>
/// </summary>
internal static class Sdl3ControlNames
{
    // The keys are SDL's own, taken from SDL_GetGamepadStringForButton - not from the enum
    // member names. The face buttons are the trap: SDL spells them a/b/x/y in a mapping string
    // while the enum calls them South/East/West/North, and SDL's parser matches the table
    // exactly, so a "south:b0" element is not rejected - it is silently dropped, leaving a
    // mapping that loads, applies and has no face buttons at all. The shipped controller
    // database writes a:b0,b:b1,x:b2,y:b3 for the same reason.
    private static readonly Dictionary<SDL.GamepadButton, string> ButtonKeys = new()
    {
        [SDL.GamepadButton.South] = "a",
        [SDL.GamepadButton.East] = "b",
        [SDL.GamepadButton.West] = "x",
        [SDL.GamepadButton.North] = "y",
        [SDL.GamepadButton.Back] = "back",
        [SDL.GamepadButton.Guide] = "guide",
        [SDL.GamepadButton.Start] = "start",
        [SDL.GamepadButton.LeftStick] = "leftstick",
        [SDL.GamepadButton.RightStick] = "rightstick",
        [SDL.GamepadButton.LeftShoulder] = "leftshoulder",
        [SDL.GamepadButton.RightShoulder] = "rightshoulder",
        [SDL.GamepadButton.DPadUp] = "dpup",
        [SDL.GamepadButton.DPadDown] = "dpdown",
        [SDL.GamepadButton.DPadLeft] = "dpleft",
        [SDL.GamepadButton.DPadRight] = "dpright",
        [SDL.GamepadButton.Misc1] = "misc1",
        [SDL.GamepadButton.Misc2] = "misc2",
        [SDL.GamepadButton.Misc3] = "misc3",
        [SDL.GamepadButton.Misc4] = "misc4",
        [SDL.GamepadButton.Misc5] = "misc5",
        [SDL.GamepadButton.Misc6] = "misc6",
        // Paddle numbering follows SDL's table, not the physical side: RightPaddle1 is
        // "paddle1" and LeftPaddle1 is "paddle2".
        [SDL.GamepadButton.RightPaddle1] = "paddle1",
        [SDL.GamepadButton.LeftPaddle1] = "paddle2",
        [SDL.GamepadButton.RightPaddle2] = "paddle3",
        [SDL.GamepadButton.LeftPaddle2] = "paddle4",
        [SDL.GamepadButton.Touchpad] = "touchpad",
    };

    private static readonly Dictionary<string, SDL.GamepadButton> Buttons =
        ButtonKeys.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<SDL.GamepadAxis, string> AxisKeys = new()
    {
        [SDL.GamepadAxis.LeftX] = "leftx",
        [SDL.GamepadAxis.LeftY] = "lefty",
        [SDL.GamepadAxis.RightX] = "rightx",
        [SDL.GamepadAxis.RightY] = "righty",
        [SDL.GamepadAxis.LeftTrigger] = "lefttrigger",
        [SDL.GamepadAxis.RightTrigger] = "righttrigger",
    };

    private static readonly Dictionary<string, SDL.GamepadAxis> Axes =
        AxisKeys.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The mapping key of one standard button, or its enum name when it has no key.</summary>
    public static string Key(SDL.GamepadButton button)
        => ButtonKeys.TryGetValue(button, out var key) ? key : button.ToString();

    /// <summary>The mapping key of one standard axis, or its enum name when it has no key.</summary>
    public static string Key(SDL.GamepadAxis axis)
        => AxisKeys.TryGetValue(axis, out var key) ? key : axis.ToString();

    public static bool TryButton(string key, out SDL.GamepadButton button)
        => Buttons.TryGetValue(key, out button);

    public static bool TryAxis(string key, out SDL.GamepadAxis axis)
        => Axes.TryGetValue(key, out axis);
}
