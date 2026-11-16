using SDL3;
using DeziroJoy.Core.Input;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Maps SDL's standard gamepad buttons onto the logical <see cref="GamepadButton"/> set.
/// <para>
/// SDL's south/east/west/north names describe the physical position in the face-button
/// diamond - Xbox A/B/X/Y, PlayStation cross/circle/square/triangle and Switch B/A/Y/X all
/// land on the same four positions. The mapping therefore follows position semantics, which
/// is the one interpretation shared by every layout SDL recognizes; per-brand label
/// rendering is a UI concern driven by <c>SDL_GetGamepadButtonLabel</c>, not something this
/// table can express. The same rule is what keeps a single generic code path valid for
/// Xbox, PlayStation, Switch and third-party pads alike.
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
