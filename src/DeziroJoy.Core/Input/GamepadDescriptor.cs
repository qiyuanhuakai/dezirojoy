namespace DeziroJoy.Core.Input;

/// <summary>
/// Identity of a connected gamepad. DeviceId is platform-stable (XInput user index +
/// product id, evdev device path + vendor/product) so saved profiles reattach to the same pad.
/// </summary>
public sealed record GamepadDescriptor(string DeviceId, string Name)
{
    public GamepadLayout Layout => GamepadLayoutDetector.Detect(Name);
}
