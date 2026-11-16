using System.Text;

namespace DeziroJoy.Platform.Sdl3;

/// <summary>
/// Persistent identity of an SDL gamepad, composed from the fields SDL guarantees to be
/// stable across runs. SDL's instance id is run-local and cannot be the identity; the GUID
/// alone cannot separate two identical units of the same model, so serial and path are
/// layered on top.
/// </summary>
/// <remarks>
/// <para>
/// Composition rules, in the order the roadmap's identity section fixes them:
/// </para>
/// <list type="bullet">
/// <item>GUID is always present - SDL's cross-run device identity (bus, VID/PID, ...).</item>
/// <item>Serial, when the device reports one, separates same-model units reliably.</item>
/// <item>Path, when SDL reports one, separates same-model units without a serial and
/// records where the device is attached; a port change can move it.</item>
/// <item>When neither serial nor path is available, the run-local instance id is appended as
/// a last resort and <see cref="IsStable"/> goes false: two same-model units stay
/// distinguishable for this run, but a config keyed on it will not reattach after a
/// restart. That tradeoff is surfaced in the scan diagnostic instead of silently sharing
/// one configuration between two pads.</item>
/// </list>
/// <para>
/// The SDL gamepad type is part of the identity record (and of the probe output) but is
/// deliberately not part of <see cref="DeviceId"/>: SDL reclassifying a device - a
/// "Standard" pad becoming "Xbox360" after a mapping database update - must not silently
/// orphan a user's saved profile. Different physical device classes (Joy-Con single vs
/// pair, DualShock vs DualSense) already carry different GUIDs, so nothing is lost.
/// </para>
/// </remarks>
public sealed record Sdl3GamepadIdentity
{
    /// <summary>Prefix that names the backend in a device id, like "xinput:" and evdev paths.</summary>
    public const string DeviceIdPrefix = "sdl:";

    private Sdl3GamepadIdentity()
    {
    }

    public required string GuidString { get; init; }

    public required string Serial { get; init; }

    public required string Path { get; init; }

    public required int Type { get; init; }

    public required string TypeName { get; init; }

    /// <summary>
    /// Stable, restart-persistent id used as the profile key:
    /// <c>sdl:{guid}[:{serial}][:{path}]</c>, with the instance id appended only when
    /// neither serial nor path exists (see <see cref="IsStable"/>).
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// False when the id falls back to the run-local instance id, i.e. the device reported
    /// neither serial nor path. True for every other composition.
    /// </summary>
    public required bool IsStable { get; init; }

    /// <summary>Builds the identity of one opened device from its SDL-reported fields.</summary>
    public static Sdl3GamepadIdentity Compose(Sdl3GamepadInfo info)
    {
        var hasSerial = !string.IsNullOrEmpty(info.Serial);
        var hasPath = !string.IsNullOrEmpty(info.Path);
        var stable = hasSerial || hasPath;

        var id = new StringBuilder(DeviceIdPrefix, DeviceIdPrefix.Length + info.GuidString.Length + info.Serial.Length + info.Path.Length + 8);
        id.Append(Sanitize(info.GuidString));

        if (hasSerial)
        {
            id.Append(':');
            id.Append(Sanitize(info.Serial));
        }

        if (hasPath)
        {
            id.Append(':');
            id.Append(Sanitize(info.Path));
        }

        if (!stable)
        {
            id.Append(":instance-");
            id.Append(info.InstanceId);
        }

        return new Sdl3GamepadIdentity
        {
            GuidString = info.GuidString,
            Serial = info.Serial,
            Path = info.Path,
            Type = info.Type,
            TypeName = info.TypeName,
            DeviceId = id.ToString(),
            IsStable = stable,
        };
    }

    /// <summary>
    /// Strips the id separator (and whitespace) from an identity component so a serial or
    /// path can never forge an extra segment. A component that is nothing but separators
    /// collapses to empty, which the composition treats as "not reported".
    /// </summary>
    internal static string Sanitize(string component)
    {
        if (string.IsNullOrEmpty(component))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(component.Length);
        foreach (var c in component)
        {
            builder.Append(c is ':' or '\t' or '\r' or '\n' or ' ' ? '_' : c);
        }

        return builder.ToString();
    }
}
