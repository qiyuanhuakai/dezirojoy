namespace DeziroJoy.Core.Input;

/// <summary>
/// Button layout family. Drives which built-in default profile is applied to a device.
/// </summary>
public enum GamepadLayout
{
    /// <summary>Xbox-style layout: A bottom, B right, X left, Y top.</summary>
    Xbox,

    /// <summary>PlayStation-style layout: cross bottom, circle right, square left, triangle top.</summary>
    DualShock,

    /// <summary>Unknown layout - falls back to Xbox-style defaults.</summary>
    Generic,
}
