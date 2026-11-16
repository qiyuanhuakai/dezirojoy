namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Everything SDL reports about one gamepad at open time: persistent identity fields,
/// classification and the extended-capability counts the probe surfaces. This is the S1
/// evidence record - device type, GUID, VID/PID, serial, path, instance id, player index,
/// connection state, mapping and touchpad/sensor capabilities - collected once when the
/// device is opened and re-read when SDL reports a remap.
/// </summary>
public sealed record Sdl3GamepadInfo
{
    /// <summary>SDL joystick instance id, valid for this run only.</summary>
    public required uint InstanceId { get; init; }

    /// <summary>Device name as SDL reports it, e.g. "Xbox Wireless Controller".</summary>
    public required string Name { get; init; }

    /// <summary>
    /// GUID as the 32-character hex string SDL produces. Stable across runs on the same
    /// platform; two identical units of the same model share it, which is why the serial
    /// and path below are needed to tell them apart.
    /// </summary>
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

    /// <summary>SDL gamepad type ordinal, e.g. 3 for <c>XboxOne</c>.</summary>
    public required int Type { get; init; }

    /// <summary>SDL gamepad type name, e.g. "XboxOne"; "Unknown" when SDL has no type.</summary>
    public required string TypeName { get; init; }

    /// <summary>The effective SDL mapping string, or empty when none is available.</summary>
    public required string Mapping { get; init; }

    /// <summary>
    /// Where the effective mapping came from: "sdl-database" when it matches what SDL's
    /// mapping database answers for this GUID, "runtime-override" when a runtime remap
    /// changed it. SDL does not distinguish built-in entries from user-database entries,
    /// so no stronger claim is made.
    /// </summary>
    public required string MappingSource { get; init; }

    /// <summary>
    /// Labels SDL reports for the four face buttons, in south/east/west/north order, e.g.
    /// ["A","B","X","Y"] on an Xbox pad or ["Cross","Circle","Square","Triangle"] on a
    /// DualSense. "Unknown" where SDL cannot say. Labels describe the glyphs; the logical
    /// mapping in <see cref="Sdl3ButtonMap"/> stays position-based.
    /// </summary>
    public required IReadOnlyList<string> FaceButtonLabels { get; init; }

    /// <summary>Player index SDL assigned (-1 when none).</summary>
    public required int PlayerIndex { get; init; }

    /// <summary>"Wired" / "Wireless" / "Unknown", from the joystick connection state.</summary>
    public required string ConnectionState { get; init; }

    /// <summary>Number of touchpads the device declares (0 on most pads).</summary>
    public required int TouchpadCount { get; init; }

    /// <summary>Supported finger count per touchpad, index-aligned with <see cref="TouchpadCount"/>.</summary>
    public required IReadOnlyList<int> TouchpadFingerCounts { get; init; }

    /// <summary>
    /// Declared sensors with their data rates, e.g. "gyro (60 Hz)". Probing sensors is S1
    /// evidence only; sensor data is not part of the normalized input (S4).
    /// </summary>
    public required IReadOnlyList<string> Sensors { get; init; }

    /// <summary>
    /// Buttons the SDL mapping declares on this device, counted over the standard set.
    /// A declared button is not proof of a physical control - that distinction belongs to
    /// the UI policy (U2), not to this count.
    /// </summary>
    public required int DeclaredButtonCount { get; init; }

    /// <summary>
    /// The standard SDL buttons this device's mapping declares, by SDL's own mapping key
    /// ("a", "dpup", "paddle1", ...) - the same vocabulary a mapping string uses, so a declared
    /// control and a mapped one are the same thing spelled the same way.
    /// <para>
    /// This is the raw declaration the generic control description is built from: the
    /// <c>HasButton</c> answer for every standard button. It is a claim about the mapping,
    /// not proof that a physical control sits behind it.
    /// </para>
    /// </summary>
    public required IReadOnlyList<string> DeclaredButtons { get; init; }

    /// <summary>
    /// The standard SDL axes this device's mapping declares, by SDL's own mapping key
    /// ("leftx", "lefttrigger", ...). A declared stick axis pair is what makes a stick
    /// describable as a two-axis control; a declared trigger axis is what makes it describable
    /// as a single-axis one.
    /// </summary>
    public required IReadOnlyList<string> DeclaredAxes { get; init; }
}
