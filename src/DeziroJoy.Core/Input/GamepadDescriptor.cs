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

    public GamepadLayout Layout => GamepadLayoutDetector.Detect(Name);
}
