using DeziroJoy.App.Services;
using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

public sealed class LinuxDeviceAccessProbeTests
{
    [Fact]
    public void Probe_Reports_Live_Access_Persistent_Configuration_And_Automatic_Fix()
    {
        var files = new Dictionary<string, string>
        {
            [LinuxDeviceAccess.UdevRulesPath] = LinuxDeviceAccess.UdevRulesText + "\n",
            ["/etc/group"] = "root:x:0:\ninput:x:42:alice,bob\n",
            ["/proc/self/status"] = "Gid:\t1000\t1000\t1000\t1000\nGroups:\t24 42 1000\n",
            ["/opt/dezirojoy/fix-device-access.sh"] = "helper",
            ["/usr/bin/pkexec"] = "pkexec",
        };
        var probe = CreateProbe(files);

        var result = probe.Probe();

        Assert.Equal(1, result.GamepadCount);
        Assert.Equal(0, result.DeniedNodeCount);
        Assert.Null(result.UInputUnavailableReason);
        Assert.True(result.UdevRulesInstalled);
        Assert.True(result.UserInInputGroup);
        Assert.True(result.SessionInInputGroup);
        Assert.True(result.CanFixAutomatically);
    }

    [Fact]
    public void Probe_Distinguishes_A_Configured_Group_From_The_Current_Session()
    {
        var files = new Dictionary<string, string>
        {
            ["/etc/group"] = "input:x:42:alice\n",
            ["/proc/self/status"] = "Gid:\t1000\t1000\t1000\t1000\nGroups:\t1000\n",
        };
        var probe = CreateProbe(files, denied: 2, uinputError: "denied");

        var result = probe.Probe();

        Assert.Equal(2, result.DeniedNodeCount);
        Assert.Equal("denied", result.UInputUnavailableReason);
        Assert.False(result.UdevRulesInstalled);
        Assert.True(result.UserInInputGroup);
        Assert.False(result.SessionInInputGroup);
        Assert.False(result.CanFixAutomatically);
    }

    private static LinuxDeviceAccessProbe CreateProbe(
        IReadOnlyDictionary<string, string> files,
        int denied = 0,
        string? uinputError = null)
        => new(
            () => new EvdevScanDiagnostic
            {
                Outcome = EvdevScanOutcome.GamepadsFound,
                NodeCount = 1,
                DeniedNodeCount = denied,
                GamepadCount = 1,
            },
            () => uinputError,
            () => "/opt/dezirojoy/fix-device-access.sh",
            path => files.GetValueOrDefault(path),
            files.ContainsKey,
            () => "/usr/bin",
            "alice");
}
