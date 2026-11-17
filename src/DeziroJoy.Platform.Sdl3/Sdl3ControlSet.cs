using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;
using SDL3;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Turns what SDL declares about one device into the Core's generic control description.
/// <para>
/// SDL is the only layer asked for facts here: <c>SDL_GamepadHasButton</c> and
/// <c>SDL_GamepadHasAxis</c>, recorded in <see cref="Sdl3GamepadInfo.DeclaredButtons"/> and
/// <see cref="Sdl3GamepadInfo.DeclaredAxes"/>, plus the face-button glyphs SDL can read off the
/// device. Everything else - the zones, the order, the kinds, the fallback naming - is the
/// Core's <see cref="GamepadControlSet"/>, so the panel contract is shared and not re-derived
/// per platform.
/// </para>
/// <para>
/// No device model is consulted. An Xbox pad, a DualSense, a Switch pad and an unknown
/// third-party pad all reach the same code, and a device the capture flow just mapped reaches it
/// too - the mapping's keys are the same keys this reads.
/// </para>
/// </summary>
internal static class Sdl3ControlSet
{
    /// <summary>
    /// The described controls of one device, in the Core's fixed order. A device SDL declares
    /// nothing for produces an empty list, which means "no declaration", not "no controls".
    /// </summary>
    public static IReadOnlyList<GamepadControlDescriptor> Build(Sdl3GamepadInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        // The same map the tracker is built from: a control the panel labels "X" has to be bound
        // to the input that pressing the physical X produces, or the description and the input
        // disagree about which button the user touched.
        var faceButtons = Sdl3FaceButtonMap.From(info);

        var buttons = GamepadButtonMask.None;
        var extras = new List<int>();
        var labels = new Dictionary<GamepadButton, string>();

        foreach (var key in info.DeclaredButtons)
        {
            if (!Sdl3ControlNames.TryButton(key, out var declared))
            {
                continue;
            }

            if (!faceButtons.TryLogical(declared, out var logical))
            {
                // Misc keys, paddles and the touchpad have no logical position; they travel as
                // raw SDL codes, so the descriptor addresses them the same way
                // IGamepadState.ExtraButtons does and a mapping always matches a description.
                extras.Add((int)declared);
                continue;
            }

            buttons = buttons.Add(logical);
            labels[logical] = LabelFor(info, declared, logical);
        }

        // A stick is described from its two axes; the stick *click* is a separate declaration.
        // Conflating them would describe a click on every pad that merely has a stick.
        if (HasAxis(info, SDL.GamepadAxis.LeftTrigger))
        {
            buttons = buttons.Add(GamepadButton.LeftTrigger);
        }

        if (HasAxis(info, SDL.GamepadAxis.RightTrigger))
        {
            buttons = buttons.Add(GamepadButton.RightTrigger);
        }

        var sticks = new List<GamepadStick>(2);
        if (HasAxis(info, SDL.GamepadAxis.LeftX) && HasAxis(info, SDL.GamepadAxis.LeftY))
        {
            sticks.Add(GamepadStick.Left);
        }

        if (HasAxis(info, SDL.GamepadAxis.RightX) && HasAxis(info, SDL.GamepadAxis.RightY))
        {
            sticks.Add(GamepadStick.Right);
        }

        return GamepadControlSet.Build(new GamepadControlDeclaration
        {
            Buttons = buttons,
            Sticks = sticks,
            ExtraButtons = extras,
            Labels = labels,
        });
    }

    /// <summary>
    /// The face-button glyph SDL can read, or the logical position when it cannot.
    /// <para>
    /// SDL answers <c>Unknown</c> for most third-party pads, and the roadmap is explicit that
    /// this must not become a guess at the brand's glyphs - a description that named an Xbox
    /// glyph on a pad that is not one would be a lie the user pays for when they look for a
    /// button that is not there. What it *can* fall back to is the logical position: A/B/X/Y is
    /// the Core's own name for the four diamond positions, the same name every other surface
    /// already uses, and the name a mapping binds to.
    /// </para>
    /// <para>
    /// It must not fall back to the raw ordinal. "按钮 3" is the Extensions zone's vocabulary -
    /// it means "the control with raw code 2" - so using it for a face key would make the same
    /// string mean two different things and leave the user unable to tell which of the four
    /// diamond keys it is.
    /// </para>
    /// </summary>
    private static string LabelFor(Sdl3GamepadInfo info, SDL.GamepadButton declared, GamepadButton logical)
    {
        if (TryFaceIndex(declared, out var index)
            && index < info.FaceButtonLabels.Count)
        {
            var glyph = info.FaceButtonLabels[index];
            if (!string.IsNullOrWhiteSpace(glyph) && !string.Equals(glyph, "Unknown", StringComparison.Ordinal))
            {
                return glyph;
            }
        }

        return GamepadControlLabels.Button(logical);
    }

    /// <summary>Index into <see cref="Sdl3GamepadInfo.FaceButtonLabels"/>, which is south/east/west/north.</summary>
    private static bool TryFaceIndex(SDL.GamepadButton button, out int index)
    {
        index = button switch
        {
            SDL.GamepadButton.South => 0,
            SDL.GamepadButton.East => 1,
            SDL.GamepadButton.West => 2,
            SDL.GamepadButton.North => 3,
            _ => -1,
        };

        return index >= 0;
    }

    private static bool HasAxis(Sdl3GamepadInfo info, SDL.GamepadAxis axis)
        => info.DeclaredAxes.Contains(Sdl3ControlNames.Key(axis), StringComparer.OrdinalIgnoreCase);
}
