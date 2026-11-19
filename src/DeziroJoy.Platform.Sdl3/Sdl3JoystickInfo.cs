namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Everything SDL reports about one device it only knows as a plain joystick. This is the
/// record the mapping-supplement flow works from: a device here has no reliable standard
/// Gamepad mapping, so it is listed - GUID, name, path, VID/PID and the raw button/axis/hat
/// counts a mapping has to be written against - and never presented as if it already had a
/// standard layout.
/// </summary>
public sealed record Sdl3JoystickInfo
{
    /// <summary>SDL joystick instance id, valid for this run only.</summary>
    public required uint InstanceId { get; init; }

    /// <summary>Device name as SDL reports it, e.g. "Generic USB Joystick".</summary>
    public required string Name { get; init; }

    /// <summary>GUID as the 32-character hex string SDL produces; the key a mapping is filed under.</summary>
    public required string GuidString { get; init; }

    /// <summary>USB/Bluetooth vendor id, or 0 when SDL cannot determine it.</summary>
    public required ushort Vendor { get; init; }

    /// <summary>USB/Bluetooth product id, or 0 when SDL cannot determine it.</summary>
    public required ushort Product { get; init; }

    /// <summary>Product (firmware) version, or 0 when unknown.</summary>
    public required ushort ProductVersion { get; init; }

    /// <summary>Serial number, or empty when the device reports none.</summary>
    public required string Serial { get; init; }

    /// <summary>
    /// Platform device path (a <c>/dev/input/eventN</c> node on Linux, a HID instance path
    /// on Windows), or empty when SDL cannot determine it.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Raw joystick buttons SDL sees; a mapping's <c>bN</c> tokens index into this.</summary>
    public required int ButtonCount { get; init; }

    /// <summary>Raw joystick axes SDL sees; a mapping's <c>aN</c> tokens index into this.</summary>
    public required int AxisCount { get; init; }

    /// <summary>Raw joystick hats SDL sees; a mapping's <c>hN.mask</c> tokens index into this.</summary>
    public required int HatCount { get; init; }

    /// <summary>SDL joystick type name, e.g. "Gamepad" or "Unknown".</summary>
    public required string TypeName { get; init; }

    /// <summary>
    /// True when SDL already recognizes this joystick as a standard gamepad. The CLI mapping
    /// flow offers only unmapped devices; the application also offers mapped devices for an
    /// explicit re-learn, preserving the old mapping until a replacement is saved.
    /// </summary>
    public required bool HasStandardMapping { get; init; }
}
