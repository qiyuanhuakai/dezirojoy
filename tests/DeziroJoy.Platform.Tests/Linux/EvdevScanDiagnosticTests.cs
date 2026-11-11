using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The scan diagnostic: what a zero-gamepad scan means, and the guidance it prints. A zero
/// count used to be indistinguishable from "no permission" and "nothing plugged in" - three
/// states that need three different fixes - so the classification and its wording are pinned.
/// </summary>
public class EvdevScanDiagnosticTests
{
    [Fact]
    public void Gamepads_Found_Wins_Over_Every_Other_Condition()
    {
        var outcome = EvdevScanDiagnostic.ClassifyOutcome(
            directoryExists: false,
            nodeCount: 0,
            deniedCount: 5,
            gamepadCount: 1);

        Assert.Equal(EvdevScanOutcome.GamepadsFound, outcome);
    }

    [Fact]
    public void A_Missing_Directory_Is_Distinct_From_An_Empty_One()
    {
        Assert.Equal(
            EvdevScanOutcome.NoInputDirectory,
            EvdevScanDiagnostic.ClassifyOutcome(directoryExists: false, nodeCount: 0, deniedCount: 0, gamepadCount: 0));

        Assert.Equal(
            EvdevScanOutcome.NoEventNodes,
            EvdevScanDiagnostic.ClassifyOutcome(directoryExists: true, nodeCount: 0, deniedCount: 0, gamepadCount: 0));
    }

    [Fact]
    public void A_Permission_Wall_Beats_Nothing_That_Looks_Like_A_Pad()
    {
        // Nodes that cannot be opened must not be reported as "no gamepad": the fix is a group
        // or a udev rule, not a different controller.
        var outcome = EvdevScanDiagnostic.ClassifyOutcome(
            directoryExists: true,
            nodeCount: 3,
            deniedCount: 3,
            gamepadCount: 0);

        Assert.Equal(EvdevScanOutcome.AccessDenied, outcome);
    }

    [Fact]
    public void Readable_Nodes_Without_A_Pad_Report_NoGamepad()
    {
        var outcome = EvdevScanDiagnostic.ClassifyOutcome(
            directoryExists: true,
            nodeCount: 4,
            deniedCount: 0,
            gamepadCount: 0);

        Assert.Equal(EvdevScanOutcome.NoGamepad, outcome);
    }

    [Fact]
    public void Access_Denied_Guidance_Names_The_Node_The_Errno_And_The_Fix()
    {
        var diagnostic = Diagnostic(EvdevScanOutcome.AccessDenied, firstDeniedPath: "/dev/input/event3", errno: 13, nodeCount: 2);

        Assert.Contains("/dev/input/event3", diagnostic.Guidance);
        Assert.Contains("errno 13", diagnostic.Guidance);
        Assert.Contains(LinuxDeviceAccess.InputGroupCommand, diagnostic.Guidance);
        Assert.Contains(LinuxDeviceAccess.UdevRulesPath, diagnostic.Guidance);
        Assert.Contains("KERNEL==\"event*\"", diagnostic.Guidance);
        Assert.Contains("usermod -aG input", diagnostic.Hint);
    }

    [Fact]
    public void No_Nodes_Guidance_Says_Check_The_Connection()
    {
        var diagnostic = Diagnostic(EvdevScanOutcome.NoEventNodes);

        Assert.Contains("没有 event 节点", diagnostic.Guidance);
        Assert.Contains("连接", diagnostic.Hint);
    }

    [Fact]
    public void No_Input_Directory_Guidance_Says_The_Environment_Has_No_Input_Subsystem()
    {
        var diagnostic = Diagnostic(EvdevScanOutcome.NoInputDirectory);

        Assert.Contains("/dev/input", diagnostic.Guidance);
        Assert.Contains("输入子系统", diagnostic.Guidance);
    }

    [Fact]
    public void NoGamepad_Guidance_Names_The_Scanned_Count_And_Excludes_Peripherals()
    {
        var diagnostic = Diagnostic(EvdevScanOutcome.NoGamepad, nodeCount: 7);

        Assert.Contains("7", diagnostic.Guidance);
        Assert.Contains("触摸板", diagnostic.Guidance);
    }

    [Fact]
    public void A_Found_Gamepad_Has_Nothing_To_Guide_The_User_Towards()
    {
        var diagnostic = Diagnostic(EvdevScanOutcome.GamepadsFound, gamepadCount: 1);

        Assert.Empty(diagnostic.Guidance);
        Assert.Empty(diagnostic.Hint);
    }

    [Fact]
    public void The_Udev_Rules_The_Guidance_Quotes_Cover_Both_Devices()
    {
        // The guidance is only actionable if the rules it prints actually cover what the app
        // opens: every event node, and a static uinput node the module would not create.
        Assert.Contains("KERNEL==\"event*\"", LinuxDeviceAccess.UdevRulesText);
        Assert.Contains("KERNEL==\"uinput\"", LinuxDeviceAccess.UdevRulesText);
        Assert.Contains("static_node=uinput", LinuxDeviceAccess.UdevRulesText);
        Assert.Contains("usermod -aG input", LinuxDeviceAccess.DeviceAccessGuidance);
        Assert.Contains("loginctl enable-linger", LinuxDeviceAccess.LingerCommand);
    }

    private static EvdevScanDiagnostic Diagnostic(
        EvdevScanOutcome outcome,
        string? firstDeniedPath = null,
        int? errno = null,
        int nodeCount = 0,
        int gamepadCount = 0)
        => new()
        {
            Outcome = outcome,
            NodeCount = nodeCount,
            DeniedNodeCount = outcome == EvdevScanOutcome.AccessDenied ? nodeCount : 0,
            GamepadCount = gamepadCount,
            FirstDeniedPath = firstDeniedPath,
            Errno = errno,
        };
}
