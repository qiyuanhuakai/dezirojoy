using System.Text.RegularExpressions;
using DeziroJoy.App.Services;
using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Linux.Evdev;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The one-click device-access fix: probe first, then the packaged helper through pkexec, with
/// the copyable manual commands as the degradation path. Every process goes through the
/// injected fake runner and every kernel probe through the injected fakes, so pkexec's polkit
/// dialog never actually opens here - and the rules the fixer stages are read back off disk to
/// prove they are the single-source text. The gate is pinned against the real probe shapes:
/// a machine with no pad plugged in must never reach pkexec.
/// </summary>
public class DeviceAccessFixerTests
{
    private static DeviceAccessFixer CreateFixer(
        FakeProcessRunner process,
        EvdevScanDiagnostic? evdev = null,
        string? uinput = null,
        string? helperPath = null)
        => new(
            process,
            () => evdev ?? Diagnostic(EvdevScanOutcome.AccessDenied, deniedNodeCount: 2),
            () => uinput,
            helperPath is null ? null : () => helperPath);

    /// <summary>
    /// A scan result in the shape <see cref="EvdevGamepadProvider.ProbeOnce"/> really returns.
    /// The optional hint is irrelevant to the fixer's gate - only the permission facts are.
    /// </summary>
    private static EvdevScanDiagnostic Diagnostic(EvdevScanOutcome outcome, int deniedNodeCount = 0)
        => new()
        {
            Outcome = outcome,
            NodeCount = 4,
            DeniedNodeCount = deniedNodeCount,
            GamepadCount = outcome == EvdevScanOutcome.GamepadsFound ? 1 : 0,
            FirstDeniedPath = deniedNodeCount > 0 ? "/dev/input/event7" : null,
            Errno = deniedNodeCount > 0 ? 13 : null,
        };

    /// <summary>
    /// A stand-in for the packaged helper: the fixer only checks that the path exists, so an
    /// empty file is enough to reach the pkexec call.
    /// </summary>
    private static string CreateFakeHelper()
    {
        var path = Path.Combine(Path.GetTempPath(), "dezirojoy-fix-helper-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n");
        return path;
    }

    [Theory]
    [InlineData(EvdevScanOutcome.GamepadsFound)]
    [InlineData(EvdevScanOutcome.NoGamepad)]
    [InlineData(EvdevScanOutcome.NoEventNodes)]
    [InlineData(EvdevScanOutcome.NoInputDirectory)]
    public void Without_A_Permission_Gap_Nothing_Is_Run(EvdevScanOutcome outcome)
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the evdev/uinput probes only run on Linux");

        var process = new FakeProcessRunner();
        var fixer = CreateFixer(process, Diagnostic(outcome), uinput: null);

        var result = fixer.Fix();

        Assert.Equal(DeviceAccessFixOutcome.NoGap, result.Outcome);
        Assert.Contains("无需修复", result.Message);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void A_Missing_Gamepad_Is_Not_A_Permission_Gap()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the evdev/uinput probes only run on Linux");

        // The real shape a healthy machine with no pad plugged in produces: every node opened
        // (denied count 0), the scan's display hint says "未识别到手柄", uinput is writable.
        // The hint is not a permission signal, so this must be NoGap - elevating here would
        // run pkexec and write machine-wide rules for a problem that does not exist.
        var process = new FakeProcessRunner();
        var fixer = CreateFixer(process, Diagnostic(EvdevScanOutcome.NoGamepad), uinput: null);

        var result = fixer.Fix();

        Assert.Equal(DeviceAccessFixOutcome.NoGap, result.Outcome);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void A_Denied_Node_Next_To_A_Working_Pad_Is_Still_A_Gap()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the evdev/uinput probes only run on Linux");

        // Permissions are per-node: a pad that works does not make a denied sibling node fine,
        // and the user asked for device access to be repaired.
        var helper = CreateFakeHelper();
        var process = new FakeProcessRunner();

        try
        {
            var fixer = CreateFixer(
                process,
                Diagnostic(EvdevScanOutcome.GamepadsFound, deniedNodeCount: 1),
                uinput: null,
                helperPath: helper);

            var result = fixer.Fix();

            Assert.Equal(DeviceAccessFixOutcome.Fixed, result.Outcome);
            Assert.Single(process.Calls);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public void An_Unwritable_UInput_Is_A_Gap_Too()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the evdev/uinput probes only run on Linux");

        var helper = CreateFakeHelper();
        var process = new FakeProcessRunner();

        try
        {
            var fixer = CreateFixer(
                process,
                Diagnostic(EvdevScanOutcome.GamepadsFound),
                uinput: "无法打开 /dev/uinput（errno 13）：请确认 uinput 模块已加载",
                helperPath: helper);

            var result = fixer.Fix();

            // The output half alone is a gap the fix aims at, even with every event node readable.
            Assert.Equal(DeviceAccessFixOutcome.Fixed, result.Outcome);
            Assert.Single(process.Calls);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public void The_Fix_Runs_The_Packaged_Helper_Through_Pkexec_With_The_Single_Source_Rules()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "pkexec and udev only exist on Linux");

        var helper = CreateFakeHelper();
        var process = new FakeProcessRunner
        {
            Next = new ProcessResult(
                0,
                "已写入 /etc/udev/rules.d/99-dezirojoy.rules\n已将 qiyuaner 加入 input 组（重新登录后生效）",
                string.Empty),
        };

        try
        {
            var fixer = CreateFixer(
                process,
                Diagnostic(EvdevScanOutcome.AccessDenied, deniedNodeCount: 2),
                uinput: null,
                helperPath: helper);

            var result = fixer.Fix();

            Assert.Equal(DeviceAccessFixOutcome.Fixed, result.Outcome);

            var call = Assert.Single(process.Calls);
            Assert.Equal("pkexec", call.FileName);
            Assert.Contains(helper, call.Arguments);

            // The staged rules are the shared text verbatim - the one source the helper
            // installs, so the app and the rules file can never disagree - and the staging
            // file is cleaned up once the helper has run.
            Assert.Equal(LinuxDeviceAccess.UdevRulesText + "\n", process.StagedRules);
            Assert.False(File.Exists(process.StagedRulesPath));

            // The helper's own report of what it did is quoted, not a fixed script.
            Assert.Contains("已将 qiyuaner 加入 input 组", result.Message);
            Assert.Contains("重新登录", result.Message);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public void A_Failed_Pkexec_Degrades_To_The_Manual_Commands()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "pkexec and udev only exist on Linux");

        var helper = CreateFakeHelper();
        var process = new FakeProcessRunner
        {
            Next = new ProcessResult(126, string.Empty, "Error executing command as another user: Not authorized"),
        };

        try
        {
            var fixer = CreateFixer(
                process,
                Diagnostic(EvdevScanOutcome.AccessDenied, deniedNodeCount: 2),
                uinput: null,
                helperPath: helper);

            var result = fixer.Fix();

            Assert.Equal(DeviceAccessFixOutcome.Degraded, result.Outcome);

            // The same fix as copyable commands, plus the reason the one-click path was not used.
            Assert.Contains(LinuxDeviceAccess.InputGroupCommand, result.Message);
            Assert.Contains(LinuxDeviceAccess.UdevRulesPath, result.Message);
            Assert.Contains("Not authorized", result.Message);
            Assert.Contains("pkexec", result.Reason);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public void A_Helper_That_Could_Not_Finish_Degrades_With_Its_Reason()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "pkexec and udev only exist on Linux");

        // The helper exits non-zero on purpose when a promised step cannot run (no udevadm,
        // no usermod), so a half-applied fix is never reported as Fixed.
        var helper = CreateFakeHelper();
        var process = new FakeProcessRunner
        {
            Next = new ProcessResult(
                1,
                "已写入 /etc/udev/rules.d/99-dezirojoy.rules",
                "未找到 udevadm，无法重载 udev 规则（规则已写入 /etc/udev/rules.d/99-dezirojoy.rules，但尚未生效）"),
        };

        try
        {
            var fixer = CreateFixer(
                process,
                Diagnostic(EvdevScanOutcome.AccessDenied, deniedNodeCount: 2),
                uinput: null,
                helperPath: helper);

            var result = fixer.Fix();

            Assert.Equal(DeviceAccessFixOutcome.Degraded, result.Outcome);

            // The helper's own stderr leads the reason, and the manual commands follow.
            Assert.Contains("未找到 udevadm", result.Message);
            Assert.Contains(LinuxDeviceAccess.InputGroupCommand, result.Message);
            Assert.Contains("udevadm", result.Reason);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public void A_Missing_Pkexec_Degrades_With_The_Reason_Named()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "pkexec and udev only exist on Linux");

        var helper = CreateFakeHelper();
        var process = new FakeProcessRunner
        {
            // A negative exit code means the process never started: no pkexec on PATH.
            Next = new ProcessResult(-1, string.Empty, "No such file or directory"),
        };

        try
        {
            var fixer = CreateFixer(
                process,
                Diagnostic(EvdevScanOutcome.AccessDenied, deniedNodeCount: 2),
                uinput: null,
                helperPath: helper);

            var result = fixer.Fix();

            Assert.Equal(DeviceAccessFixOutcome.Degraded, result.Outcome);
            Assert.Contains("未找到 pkexec", result.Message);
            Assert.Contains("未找到 pkexec", result.Reason);
            Assert.Contains(LinuxDeviceAccess.InputGroupCommand, result.Message);
        }
        finally
        {
            File.Delete(helper);
        }
    }

    [Fact]
    public void Without_The_Packaged_Helper_The_Fix_Degrades_Without_Running_Anything()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "pkexec and udev only exist on Linux");

        var process = new FakeProcessRunner();
        var missing = Path.Combine(Path.GetTempPath(), "dezirojoy-missing-" + Guid.NewGuid().ToString("N"));
        var fixer = CreateFixer(
            process,
            Diagnostic(EvdevScanOutcome.AccessDenied, deniedNodeCount: 2),
            uinput: null,
            helperPath: missing);

        var result = fixer.Fix();

        Assert.Equal(DeviceAccessFixOutcome.Degraded, result.Outcome);

        // A build that was never installed as a .deb has no helper to run - and the result
        // says exactly that instead of firing a pkexec that cannot succeed.
        Assert.Contains("deb", result.Message);
        Assert.Contains(LinuxDeviceAccess.InputGroupCommand, result.Message);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void A_Probe_That_Throws_Is_A_Failure_Not_A_Silent_Success()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the evdev/uinput probes only run on Linux");

        var process = new FakeProcessRunner();
        var fixer = new DeviceAccessFixer(
            process,
            () => throw new IOException("boom"),
            () => null,
            () => "/nonexistent");

        var result = fixer.Fix();

        Assert.Equal(DeviceAccessFixOutcome.Failed, result.Outcome);
        Assert.Contains("boom", result.Message);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public void Off_Linux_The_Fix_Is_Unsupported()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "the unsupported branch only exists off Linux");

        var fixer = new DeviceAccessFixer(new FakeProcessRunner());

        var result = fixer.Fix();

        Assert.Equal(DeviceAccessFixOutcome.Unsupported, result.Outcome);
    }

    /// <summary>
    /// Records every invocation and answers with <see cref="Next"/>; the staged rules file is
    /// read at invocation time because the fixer deletes it once the process has run.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<(string FileName, string Arguments)> Calls { get; } = [];

        public ProcessResult Next { get; set; } = new(0, string.Empty, string.Empty);

        public string? StagedRules { get; private set; }

        public string? StagedRulesPath { get; private set; }

        public ProcessResult Run(string fileName, string arguments)
        {
            Calls.Add((fileName, arguments));

            var staged = ExtractStagedFile(arguments);

            if (staged is not null && File.Exists(staged))
            {
                StagedRules = File.ReadAllText(staged);
                StagedRulesPath = staged;
            }

            return Next;
        }

        /// <summary>The last quoted argument of <c>pkexec "helper" "rules-file"</c>.</summary>
        private static string? ExtractStagedFile(string arguments)
        {
            var matches = Regex.Matches(arguments, "\"([^\"]*)\"");

            return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
        }
    }
}
