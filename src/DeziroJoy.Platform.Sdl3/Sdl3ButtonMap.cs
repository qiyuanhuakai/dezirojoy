using SDL3;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Maps SDL's standard gamepad buttons onto the logical <see cref="GamepadButton"/> set, by
/// position.
/// <para>
/// SDL's south/east/west/north names describe the physical position in the face-button diamond.
/// This table is the positional answer - the Xbox convention, and the one interpretation shared
/// by every layout SDL recognizes - and it is what a device whose glyphs SDL could not read gets.
/// </para>
/// <para>
/// It is <em>not</em> the whole answer for a device SDL can read glyphs from. A Nintendo pad
/// prints B in the south slot and X in the north slot, so the positional table would make the
/// physical X execute logical Y while the panel called it X. Those devices are mapped by
/// <see cref="Sdl3FaceButtonMap"/>, which reads the glyphs and is what the tracker and the
/// control description both use. This table remains the fallback, and the answer for every
/// non-face button, which has no glyph to read.
/// </para>
/// </summary>
internal static class Sdl3ButtonMap
{
    /// <summary>
    /// The logical position of a standard SDL button, or false for the buttons that have no
    /// position in <see cref="GamepadButton"/>: the miscellaneous and paddle cluster
    /// (Misc1..Misc6, the four paddles) and the touchpad click. Those travel as raw SDL
    /// button codes through <see cref="IGamepadState.ExtraButtons"/> instead, exactly like
    /// unmapped evdev codes do, so they stay bindable without inventing logical positions.
    /// </summary>
    public static bool TryToLogical(SDL.GamepadButton button, out GamepadButton logical)
    {
        logical = button switch
        {
            SDL.GamepadButton.South => GamepadButton.A,
            SDL.GamepadButton.East => GamepadButton.B,
            SDL.GamepadButton.West => GamepadButton.X,
            SDL.GamepadButton.North => GamepadButton.Y,
            SDL.GamepadButton.Back => GamepadButton.Back,
            SDL.GamepadButton.Guide => GamepadButton.Guide,
            SDL.GamepadButton.Start => GamepadButton.Start,
            SDL.GamepadButton.LeftStick => GamepadButton.LeftStick,
            SDL.GamepadButton.RightStick => GamepadButton.RightStick,
            SDL.GamepadButton.LeftShoulder => GamepadButton.LeftBumper,
            SDL.GamepadButton.RightShoulder => GamepadButton.RightBumper,
            SDL.GamepadButton.DPadUp => GamepadButton.DPadUp,
            SDL.GamepadButton.DPadDown => GamepadButton.DPadDown,
            SDL.GamepadButton.DPadLeft => GamepadButton.DPadLeft,
            SDL.GamepadButton.DPadRight => GamepadButton.DPadRight,
            _ => GamepadButton.Extra,
        };

        return logical != GamepadButton.Extra;
    }
}
