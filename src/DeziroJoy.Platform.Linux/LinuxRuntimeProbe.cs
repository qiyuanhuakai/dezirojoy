using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;

namespace DeziroJoy.Platform.Linux;

/// <summary>
/// What a Linux client that owns no devices can still learn about why mapping is dead: the
/// evdev side (which nodes exist, who may open them) and the uinput side (whether the virtual
/// device can be created at all). Either half may be null when it has nothing to report.
/// </summary>
public sealed record LinuxRuntimeHints(string? DeviceGuidance, string? OutputGuidance);

/// <summary>
/// One-shot answers for the GUI status area. The render process owns no devices and no sink -
/// the daemon is the only mapping authority - so instead of a silent empty status it asks the
/// kernel directly: same user, same nodes, same answer the daemon's own scan would produce.
/// Off Linux both halves are null: there is nothing to diagnose and nothing to say.
/// </summary>
public static class LinuxRuntimeProbe
{
    public static LinuxRuntimeHints Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new LinuxRuntimeHints(null, null);
        }

        var device = EvdevGamepadProvider.ProbeOnce();
        var output = UInputOutputSink.ProbeUnavailableReason();

        return new LinuxRuntimeHints(
            string.IsNullOrEmpty(device.Hint) ? null : device.Hint,
            output);
    }
}
