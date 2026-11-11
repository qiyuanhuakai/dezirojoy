using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The one-shot probe over a fixture tree: real open()/ioctl()/close() calls against plain
/// files, so the enumeration, the errno capture and the classification are exercised without
/// needing a kernel that has gamepads attached. Regular files answer every evdev ioctl with
/// ENOTTY, which reads as "not a gamepad" - exactly the no-gamepad path.
/// </summary>
public class EvdevProbeTests
{
    [Fact]
    public void An_Empty_Directory_Reports_No_Nodes_And_Says_Check_The_Connection()
    {
        var directory = TempDirectory();

        try
        {
            var diagnostic = EvdevGamepadProvider.ProbeOnce(directory);

            Assert.Equal(EvdevScanOutcome.NoEventNodes, diagnostic.Outcome);
            Assert.Equal(0, diagnostic.NodeCount);
            Assert.Contains("连接", diagnostic.Hint);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_Missing_Directory_Reports_No_Input_Subsystem()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dezirojoy-evdev-missing-" + Guid.NewGuid().ToString("N"));

        var diagnostic = EvdevGamepadProvider.ProbeOnce(missing);

        Assert.Equal(EvdevScanOutcome.NoInputDirectory, diagnostic.Outcome);
        Assert.Equal(0, diagnostic.NodeCount);
        Assert.Contains("输入子系统", diagnostic.Guidance);
    }

    [Fact]
    public void Nodes_That_Cannot_Be_Opened_Report_Access_Denied_With_The_Errno()
    {
        // A plain guard, not Assert.SkipWhen: the Unix-only file-mode API below needs the
        // analyzer to see the platform check in control flow.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX file permissions only exist on Linux");
            return;
        }

        var directory = TempDirectory();

        try
        {
            // The shape a user without the input group sees: nodes exist, open() says EACCES.
            var denied = Path.Combine(directory, "event0");
            File.WriteAllText(denied, string.Empty);
            File.SetUnixFileMode(denied, UnixFileMode.None);

            // A root process ignores the mode entirely; there is no EACCES left to observe.
            Assert.SkipWhen(!IsUnreadable(denied), "root ignores file permissions");

            var diagnostic = EvdevGamepadProvider.ProbeOnce(directory);

            Assert.Equal(EvdevScanOutcome.AccessDenied, diagnostic.Outcome);
            Assert.Equal(1, diagnostic.NodeCount);
            Assert.Equal(1, diagnostic.DeniedNodeCount);
            Assert.Equal(0, diagnostic.GamepadCount);
            Assert.Equal(denied, diagnostic.FirstDeniedPath);
            Assert.Equal(13, diagnostic.Errno);
            Assert.Contains("usermod -aG input", diagnostic.Guidance);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool IsUnreadable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    [Fact]
    public void Readable_Nodes_Without_Gamepad_Capabilities_Report_NoGamepad()
    {
        // The probe walks the fixture nodes with real open()/ioctl() calls: the P/Invoke into
        // libc is exactly what does not exist off Linux.
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the probe's real open()/ioctl() P/Invoke only exists on Linux");

        var directory = TempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(directory, "event0"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "event1"), string.Empty);
            File.WriteAllText(Path.Combine(directory, "mouse0"), string.Empty);

            var diagnostic = EvdevGamepadProvider.ProbeOnce(directory);

            // mouse0 is not an event* node, so it is not even counted.
            Assert.Equal(EvdevScanOutcome.NoGamepad, diagnostic.Outcome);
            Assert.Equal(2, diagnostic.NodeCount);
            Assert.Equal(0, diagnostic.DeniedNodeCount);
            Assert.Contains("2", diagnostic.Guidance);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Nodes_Are_Enumerated_In_Device_Number_Order()
    {
        // The old probe walked event0..event31 in order; a directory listing does not
        // guarantee that, so the ordering is its own rule - and its own test.
        var ordered = EvdevNodes.Order(["/dev/input/event10", "/dev/input/event2", "/dev/input/event31", "/dev/input/event0"]);

        Assert.Equal(
            ["/dev/input/event0", "/dev/input/event2", "/dev/input/event10", "/dev/input/event31"],
            ordered);
    }

    [Fact]
    public void A_Node_Past_The_Old_Event31_Limit_Is_Seen()
    {
        // Same probe path as above: every fixture node is opened through libc.
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the probe's real open()/ioctl() P/Invoke only exists on Linux");

        // The bug being fixed: a 33rd event node was invisible to the old scan.
        var directory = TempDirectory();

        try
        {
            for (var i = 0; i <= 40; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"event{i}"), string.Empty);
            }

            var diagnostic = EvdevGamepadProvider.ProbeOnce(directory);

            Assert.Equal(41, diagnostic.NodeCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_Real_DevInput_Directory_Produces_A_Coherent_Diagnostic()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "/dev/input only exists on Linux");

        var diagnostic = EvdevGamepadProvider.ProbeOnce();

        if (EvdevNodes.DirectoryExists())
        {
            Assert.Equal(diagnostic.NodeCount, EvdevNodes.Enumerate().Count);
            Assert.InRange(diagnostic.GamepadCount, 0, diagnostic.NodeCount);
        }
        else
        {
            Assert.Equal(EvdevScanOutcome.NoInputDirectory, diagnostic.Outcome);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dezirojoy-evdev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }
}
