using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.Platform.Tests.Linux;

/// <summary>
/// The probe maps one evdev scan onto the hint triple the GUI status area quotes. The
/// unhandled-axis half is the fix for a diagnostic that used to vanish the moment a pad
/// connected: an axis no mapping reaches is indistinguishable from a broken stick or trigger,
/// so it must reach the status line - as a single line, sourced from the same
/// <see cref="EvdevScanDiagnostic.Guidance"/> wording the daemon journal prints. A healthy pad
/// and a zero-device scan both leave it null, so their status lines are untouched.
/// </summary>
public class LinuxRuntimeProbeTests
{
    [Fact]
    public void A_Scan_With_Unhandled_Axes_Surfaces_Them_As_A_Single_Line()
    {
        var hints = LinuxRuntimeProbe.FromDiagnostic(
            Diagnostic(
                EvdevScanOutcome.GamepadsFound,
                gamepadCount: 1,
                unhandledAxes: [new EvdevUnhandledAxis(0x09, 0, 255, 255, "/dev/input/event5")]),
            output: null);

        var axis = hints.UnhandledAxisGuidance;
        Assert.NotNull(axis);
        Assert.Contains("未处理的轴", axis);
        Assert.Contains("0x09", axis);
        Assert.Contains("evtest", axis);
        // Sized for the one-line status area: the outcome wording is gone once a gamepad was
        // found, so the segment carries no newline to split the status line on.
        Assert.DoesNotContain("\n", axis);
    }

    [Fact]
    public void A_Scan_With_Every_Axis_Handled_Leaves_The_Axis_Hint_Null()
    {
        var hints = LinuxRuntimeProbe.FromDiagnostic(
            Diagnostic(EvdevScanOutcome.GamepadsFound, gamepadCount: 1),
            output: null);

        Assert.Null(hints.UnhandledAxisGuidance);
    }

    [Fact]
    public void A_Zero_Device_Scan_Leaves_The_Axis_Hint_Null_And_Keeps_The_Device_Hint()
    {
        var hints = LinuxRuntimeProbe.FromDiagnostic(
            Diagnostic(EvdevScanOutcome.NoGamepad, nodeCount: 4),
            output: null);

        Assert.Null(hints.UnhandledAxisGuidance);
        var device = hints.DeviceGuidance;
        Assert.NotNull(device);
        Assert.Contains("未识别", device);
    }

    [Fact]
    public void A_Permission_Wall_Leaves_The_Axis_Hint_Null_And_Keeps_The_Device_Hint()
    {
        var hints = LinuxRuntimeProbe.FromDiagnostic(
            Diagnostic(EvdevScanOutcome.AccessDenied, firstDeniedPath: "/dev/input/event3", errno: 13, nodeCount: 2),
            output: null);

        Assert.Null(hints.UnhandledAxisGuidance);
        var device = hints.DeviceGuidance;
        Assert.NotNull(device);
        Assert.Contains("usermod -aG input", device);
    }

    [Fact]
    public void The_Output_Half_Is_Passed_Through_Unchanged()
    {
        var hints = LinuxRuntimeProbe.FromDiagnostic(
            Diagnostic(EvdevScanOutcome.GamepadsFound, gamepadCount: 1),
            output: "无法打开 /dev/uinput（errno 13）");

        Assert.Equal("无法打开 /dev/uinput（errno 13）", hints.OutputGuidance);
    }

    private static EvdevScanDiagnostic Diagnostic(
        EvdevScanOutcome outcome,
        string? firstDeniedPath = null,
        int? errno = null,
        int nodeCount = 0,
        int gamepadCount = 0,
        IReadOnlyList<EvdevUnhandledAxis>? unhandledAxes = null)
        => new()
        {
            Outcome = outcome,
            NodeCount = nodeCount,
            DeniedNodeCount = outcome == EvdevScanOutcome.AccessDenied ? nodeCount : 0,
            GamepadCount = gamepadCount,
            FirstDeniedPath = firstDeniedPath,
            Errno = errno,
            UnhandledAxes = unhandledAxes ?? [],
        };
}
