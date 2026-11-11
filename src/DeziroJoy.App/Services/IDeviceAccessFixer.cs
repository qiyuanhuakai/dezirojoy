using DeziroJoy.Platform.Linux;

namespace DeziroJoy.App.Services;

/// <summary>What the one-click device-access fix ended up doing.</summary>
public enum DeviceAccessFixOutcome
{
    /// <summary>
    /// Not Linux: there is no evdev/uinput permission model to fix here, so the card offers
    /// the button nowhere else.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The probe found no gap - the event nodes are readable and /dev/uinput is writable -
    /// so there was nothing to fix and nothing was run.
    /// </summary>
    NoGap,

    /// <summary>
    /// The packaged helper ran as root through pkexec and applied the udev rules and the
    /// input-group change.
    /// </summary>
    Fixed,

    /// <summary>
    /// Elevation was unavailable - no pkexec on PATH, no packaged helper, or pkexec itself
    /// failed (a desktop without a polkit agent, a dismissed authentication dialog) - so the
    /// user gets the same fix as copyable commands instead, in the wording every other Linux
    /// surface already quotes.
    /// </summary>
    Degraded,

    /// <summary>
    /// The fix was attempted and failed for a reason the manual commands will not fix (the
    /// probe itself could not run, the rules file could not be staged).
    /// </summary>
    Failed,
}

/// <summary>
/// The one-click fix's outcome with a ready-to-show message. <see cref="Reason"/> carries the
/// degradation or failure cause for the button tooltip where there is one, and is null when
/// there is nothing to explain.
/// </summary>
public readonly record struct DeviceAccessFixResult(
    DeviceAccessFixOutcome Outcome, string Message, string? Reason = null)
{
    public static DeviceAccessFixResult Unsupported(string message)
        => new(DeviceAccessFixOutcome.Unsupported, message);

    public static DeviceAccessFixResult NoGap(string message)
        => new(DeviceAccessFixOutcome.NoGap, message);

    public static DeviceAccessFixResult Fixed(string message)
        => new(DeviceAccessFixOutcome.Fixed, message);

    public static DeviceAccessFixResult Degraded(string reason, string message)
        => new(DeviceAccessFixOutcome.Degraded, message, reason);

    public static DeviceAccessFixResult Failed(string reason, string message)
        => new(DeviceAccessFixOutcome.Failed, message, reason);
}

/// <summary>
/// Repairs the Linux device access that mapping depends on - readable <c>/dev/input/event*</c>
/// nodes and a writable <c>/dev/uinput</c> - from one button press. It first asks
/// <see cref="LinuxRuntimeProbe"/> where the gap is, then runs the packaged helper as root
/// through pkexec, and degrades to the copyable manual commands wherever the elevated path is
/// unavailable. The helper never runs unprompted: this is the user's explicit opt-in, exactly
/// the principle that keeps the installers from registering udev rules on their own
/// (docs/ELEVATION-AND-DAEMON.md 5.2).
/// </summary>
public interface IDeviceAccessFixer
{
    DeviceAccessFixResult Fix();
}
