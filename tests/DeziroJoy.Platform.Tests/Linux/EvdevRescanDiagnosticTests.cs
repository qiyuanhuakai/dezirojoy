using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The re-scan path of the instance scan: a device that was connected before a scan must
/// keep contributing to the diagnostic that scan produces. The first scan opens every node
/// and collects each pad's unmapped axes; a re-scan - hot-plug, or the periodic
/// GetConnected refresh - takes the "already served" branch for pads it is still serving,
/// and dropping their axes there made the diagnostic contradict itself: a counted gamepad
/// reporting no axes at all. Regular files answer every evdev ioctl with ENOTTY, so a
/// fixture tree can never open as a gamepad on its own; the registered-device seam stands
/// in for a pad that is already being served. Unlike the probe tests this path is pure
/// managed code, so it needs no Linux guard.
/// </summary>
public class EvdevRescanDiagnosticTests
{
    [Fact]
    public void A_Rescan_Keeps_The_Axis_Diagnostic_Of_An_Already_Served_Device()
    {
        var directory = TempDirectory();

        try
        {
            var node = Path.Combine(directory, "event0");
            File.WriteAllText(node, string.Empty);

            var axis = new EvdevUnhandledAxis(0x08, 0, 255, 0, node);

            using var provider = new EvdevGamepadProvider();
            provider.RegisterServedDevice(ServedDevice(node, axis));

            // The first scan takes the "already served" branch; the second proves the axis
            // diagnostic survives a re-scan instead of being a one-shot artifact of the open.
            for (var scan = 0; scan < 2; scan++)
            {
                provider.Scan(directory);

                var diagnostic = provider.LastScanDiagnostic;

                Assert.NotNull(diagnostic);
                Assert.Equal(1, diagnostic.GamepadCount);
                Assert.Equal([axis], diagnostic.UnhandledAxes);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Every_Served_Device_Contributes_Its_Own_Axes()
    {
        // The hot-plug shape from the review: two pads connected, one axis list. Each served
        // device contributes its own codes, in node order.
        var directory = TempDirectory();

        try
        {
            var first = Path.Combine(directory, "event0");
            var second = Path.Combine(directory, "event1");
            File.WriteAllText(first, string.Empty);
            File.WriteAllText(second, string.Empty);

            using var provider = new EvdevGamepadProvider();
            provider.RegisterServedDevice(ServedDevice(first, new EvdevUnhandledAxis(0x08, 0, 255, 0, first)));
            provider.RegisterServedDevice(ServedDevice(second, new EvdevUnhandledAxis(0x35, 0, 32767, 0, second)));

            provider.Scan(directory);

            var diagnostic = provider.LastScanDiagnostic;

            Assert.NotNull(diagnostic);
            Assert.Equal(2, diagnostic.GamepadCount);
            Assert.Equal([0x08, 0x35], diagnostic.UnhandledAxes.Select(axis => axis.Code));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_Served_Device_Whose_Node_Vanished_Stops_Contributing()
    {
        // The other edge of the same loop: a device removed mid-scan must not linger in the
        // diagnostic, so the removal path and the carry-over path agree on what "served" means.
        var directory = TempDirectory();

        try
        {
            var node = Path.Combine(directory, "event0");
            File.WriteAllText(node, string.Empty);

            using var provider = new EvdevGamepadProvider();
            provider.RegisterServedDevice(ServedDevice(node, new EvdevUnhandledAxis(0x08, 0, 255, 0, node)));

            File.Delete(node);
            provider.Scan(directory);

            var diagnostic = provider.LastScanDiagnostic;

            Assert.NotNull(diagnostic);
            Assert.Equal(0, diagnostic.GamepadCount);
            Assert.Empty(diagnostic.UnhandledAxes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static EvdevGamepadProvider.Device ServedDevice(string path, params EvdevUnhandledAxis[] axes)
        => new()
        {
            Path = path,
            Name = "测试手柄",
            Tracker = new EvdevGamepadStateTracker(),
            UnhandledAxes = axes,
            Fd = -1,
        };

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dezirojoy-evdev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }
}
