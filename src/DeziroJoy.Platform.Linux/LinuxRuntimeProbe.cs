using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;

namespace DeziroJoy.Platform.Linux;

/// <summary>
/// What a Linux client that owns no devices can still learn about why mapping is dead: the
/// evdev side (which nodes exist, who may open them) and the uinput side (whether the virtual
/// device can be created at all). Either half may be null when it has nothing to report.
/// </summary>
/// <param name="UnhandledAxisGuidance">
/// The one-line unhandled-axis diagnostic: ABS axes a gamepad reports that the tracker does
/// not map, quoted with raw code, range and rest value for an <c>evtest</c> comparison, or
/// null when every reported axis is handled. Distinct from <see cref="DeviceGuidance"/>,
/// which explains a ZERO-device scan - this is the axis half that survives with a pad
/// connected, so a dead stick or trigger stops being indistinguishable from a broken one.
/// Sourced verbatim from <see cref="EvdevScanDiagnostic.Guidance"/>.
/// </param>
public sealed record LinuxRuntimeHints(
    string? DeviceGuidance,
    string? OutputGuidance,
    string? UnhandledAxisGuidance = null);

/// <summary>
/// One-shot answers for the GUI status area. The render process owns no devices and no sink -
/// the daemon is the only mapping authority - so instead of a silent empty status it asks the
/// kernel directly: same user, same nodes, same answer the daemon's own scan would produce.
/// Off Linux every half is null: there is nothing to diagnose and nothing to say.
/// </summary>
public static class LinuxRuntimeProbe
{
    public static LinuxRuntimeHints Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new LinuxRuntimeHints(null, null);
        }

        return FromDiagnostic(
            EvdevGamepadProvider.ProbeOnce(),
            UInputOutputSink.ProbeUnavailableReason());
    }

    /// <summary>
    /// Maps one evdev scan diagnostic (and the uinput open-check) onto the hint triple the
    /// GUI quotes. The unhandled-axis half is derived from
    /// <see cref="EvdevScanDiagnostic.Guidance"/> - the single source of that wording - and
    /// only once the scan reported an unmapped axis, which is exactly when Guidance reduces
    /// to the axis segment: an unmapped axis comes from an opened gamepad, so the outcome is
    /// GamepadsFound and there is no "nothing plugged in" wording to contradict the device.
    /// With nothing unmapped it stays null and the GUI's device line is left untouched.
    /// </summary>
    internal static LinuxRuntimeHints FromDiagnostic(EvdevScanDiagnostic device, string? output)
        => new(
            string.IsNullOrEmpty(device.Hint) ? null : device.Hint,
            output,
            device.UnhandledAxes.Count > 0 ? device.Guidance : null);
}
