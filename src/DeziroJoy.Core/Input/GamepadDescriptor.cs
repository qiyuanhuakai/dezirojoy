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

    public GamepadLayout Layout => GamepadLayoutDetector.Detect(Name);
}
