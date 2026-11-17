using DeziroJoy.Core.Mapping;

namespace DeziroJoy.Core.Input;

/// <summary>
/// Identity of a connected gamepad. DeviceId is platform-stable (XInput user index +
/// product id, evdev device path + vendor/product) so saved profiles reattach to the same pad.
/// </summary>
public sealed record GamepadDescriptor(string DeviceId, string Name)
{
    /// <summary>
    /// Number of buttons the device reports, or null when unknown. XInput's 14-button set is
    /// fixed and closed, so the Windows layer always reports null; the Linux evdev layer fills
    /// it in from EVIOCGBIT(EV_KEY). The UI shows "unknown" rather than a guess when null.
    /// </summary>
    public int? ButtonCount { get; init; }

    /// <summary>
    /// Generic descriptions of the controls this device declares, in the fixed order
    /// <see cref="GamepadControlSet.Build"/> produces. Empty by default, and empty means "this
    /// platform has not written a description yet" - never "this device has no controls".
    /// <para>
    /// A description is a declaration, not an observation: it says what the device's mapping
    /// claims exists, not what has been pressed. Live state belongs to
    /// <see cref="IGamepadState"/>, and mixing the two is exactly the mistake the roadmap's U2
    /// policy exists to prevent - a control nobody has touched yet must stay visible and
    /// configurable rather than disappearing because it was never detected.
    /// </para>
    /// </summary>
    public IReadOnlyList<GamepadControlDescriptor> Controls { get; init; } = [];

    /// <summary>
    /// The label this device prints at one logical button position - for surfaces that draw a
    /// fixed layout and only need to relabel it.
    /// <para>
    /// This exists because a hand-drawn pad is drawn once, with the Xbox letters baked into the
    /// artwork, and reads only <see cref="IGamepadState"/>: it has no channel for a description.
    /// On a Nintendo Switch pad that is not a cosmetic problem - the physical X sits in the
    /// north position, which is logical <see cref="GamepadButton.Y"/>, so an Xbox-letter drawing
    /// lights its "Y" when the user presses X. Relabelling from the device's own glyphs fixes
    /// the display without touching the input: the logical position a mapping binds to is
    /// unchanged, so no profile means something different on a different pad.
    /// </para>
    /// <para>
    /// A surface that wants the full description should read <see cref="Controls"/> instead;
    /// this is the one-line bridge for the ones that cannot yet.
    /// </para>
    /// </summary>
    /// <param name="button">The logical position to name.</param>
    /// <returns>
    /// The glyph the device prints at that position when the platform could read one, and the
    /// logical name otherwise. Never empty: a caller that gets nothing useful is showing a
    /// device with no description, which is a different problem from an unlabelled button.
    /// </returns>
    public string ControlLabel(GamepadButton button)
        => Controls.FirstOrDefault(control =>
                control.Source is ButtonBinding binding && binding.Button == button)?.Label
            ?? GamepadControlLabels.Button(button);

    public GamepadLayout Layout => GamepadLayoutDetector.Detect(Name);
}
