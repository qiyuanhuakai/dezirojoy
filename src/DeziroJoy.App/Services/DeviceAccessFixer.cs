using System.ComponentModel;
using System.Diagnostics;
using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Linux.Evdev;
using DeziroJoy.Platform.Linux.Output;

namespace DeziroJoy.App.Services;

/// <summary>One process run: its exit code and what it printed.</summary>
internal readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// The process seam behind <see cref="DeviceAccessFixer"/>, mirroring
/// <see cref="LinuxDaemonService"/>'s systemctl seam: production runs a real process, tests
/// inject a fake, so pkexec's polkit dialog never actually opens in a test.
/// </summary>
internal interface IProcessRunner
{
    ProcessResult Run(string fileName, string arguments);
}

/// <summary>The production runner: a real process with its output captured.</summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public ProcessResult Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return new ProcessResult(-1, string.Empty, $"{fileName} 无法启动");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // No such executable on PATH (pkexec missing, no polkit installed): the negative
            // exit code marks "never ran" and the message carries the reason.
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
    }
}

/// <summary>
/// The one-click device-access fix. The flow is deliberately ordered so that every failure
/// mode lands on an honest state:
/// <list type="number">
/// <item>Probe first - and gate on <em>permission</em> signals only: an event node this user
/// may not open (the evdev scan's denied count) or an unwritable /dev/uinput. The evdev
/// scan's display-oriented hint is deliberately NOT the gate: "no gamepad found", "no event
/// nodes" and "no input subsystem" are not permission gaps - the udev rules grant permissions
/// and cannot create a device - so a machine whose permissions are in order is told "nothing
/// to fix" and no pkexec runs, even with no pad plugged in. A denied node next to an already
/// working pad still counts: permissions are per-node, and the user asked for device access
/// to be repaired.</item>
/// <item>Stage the rules: the udev text is generated from
/// <see cref="LinuxDeviceAccess.UdevRulesText"/> - the same single source every other surface
/// quotes - into a temporary file, so the helper installs the app's rules verbatim instead of
/// carrying a second copy that can drift.</item>
/// <item>Run the packaged helper as root through pkexec. It writes the rules, reloads udev and
/// adds the invoking user to the input group; the group half only takes effect at the next
/// login, which the result says.</item>
/// <item>Degrade wherever the elevated path is unavailable - no pkexec, no packaged helper
/// (a build that was never installed as a .deb), or a pkexec that failed because the desktop
/// has no polkit agent, the dialog was dismissed, or the helper could not finish its promised
/// steps - and hand the user the copyable manual commands instead, with the reason on the
/// button's tooltip.</item>
/// </list>
/// </summary>
public sealed class DeviceAccessFixer : IDeviceAccessFixer
{
    private readonly IProcessRunner _process;
    private readonly Func<EvdevScanDiagnostic> _evdevProbe;
    private readonly Func<string?> _uinputProbe;
    private readonly Func<string> _helperPath;

    /// <summary>
    /// Internal so the seams stay an implementation detail: production and the settings card
    /// use the defaults, tests inject a fake runner and fake probes.
    /// </summary>
    internal DeviceAccessFixer(
        IProcessRunner? process = null,
        Func<EvdevScanDiagnostic>? evdevProbe = null,
        Func<string?>? uinputProbe = null,
        Func<string>? helperPath = null)
    {
        _process = process ?? new ProcessRunner();
        _evdevProbe = evdevProbe ?? (static () => EvdevGamepadProvider.ProbeOnce());
        _uinputProbe = uinputProbe ?? UInputOutputSink.ProbeUnavailableReason;
        _helperPath = helperPath ?? (static () => LinuxDeviceAccess.FixHelperPath);
    }

    public DeviceAccessFixResult Fix()
    {
        if (!OperatingSystem.IsLinux())
        {
            return DeviceAccessFixResult.Unsupported("设备访问修复仅支持 Linux（evdev/uinput 权限模型）。");
        }

        EvdevScanDiagnostic evdev;
        string? uinput;

        try
        {
            evdev = _evdevProbe();
            uinput = _uinputProbe();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A probe is diagnostics; when even that cannot run, the fix has nothing to aim
            // at and must not pretend it fixed something.
            return DeviceAccessFixResult.Failed("探测失败", $"无法探测设备访问状态：{exception.Message}");
        }

        if (evdev.DeniedNodeCount == 0 && uinput is null)
        {
            // Every event node opened and /dev/uinput is writable: the rules have nothing to
            // grant. No gamepad, no event nodes or no input subsystem at all are not
            // permission gaps - a rules file cannot conjure a plugged-in pad - so this is
            // where "nothing to fix" is reported instead of elevating for nothing.
            return DeviceAccessFixResult.NoGap(
                "未检测到设备访问缺口：手柄节点可读、/dev/uinput 可写，无需修复。");
        }

        var helper = _helperPath();

        if (!File.Exists(helper))
        {
            return Degrade(DescribeMissingHelper(helper));
        }

        string rulesFile;

        try
        {
            rulesFile = WriteRulesFile();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DeviceAccessFixResult.Failed("无法写临时文件", $"无法生成 udev 规则文件：{exception.Message}");
        }

        try
        {
            var result = _process.Run("pkexec", $"{helper} {Quote(rulesFile)}");

            if (result.ExitCode == 0)
            {
                return DeviceAccessFixResult.Fixed(
                    "已修复设备访问权限：" + DescribeHelperWork(result)
                    + $"。udev 规则已写入 {LinuxDeviceAccess.UdevRulesPath}；用户组变更需要重新登录后生效。");
            }

            return Degrade(DescribeElevationFailure(result));
        }
        finally
        {
            TryDelete(rulesFile);
        }
    }

    /// <summary>
    /// The rules staged for the helper: <see cref="LinuxDeviceAccess.UdevRulesText"/> verbatim,
    /// newline-terminated so the last rule is a complete line. The helper installs this file
    /// as-is, which is what keeps the app the single source of the rule text.
    /// </summary>
    private static string WriteRulesFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-udev-{Guid.NewGuid():N}.rules");
        File.WriteAllText(path, LinuxDeviceAccess.UdevRulesText + "\n");
        return path;
    }

    private static DeviceAccessFixResult Degrade(string reason)
        => DeviceAccessFixResult.Degraded(
            reason,
            $"一键修复不可用：{reason}。请手动执行：{LinuxDeviceAccess.DeviceAccessGuidance}");

    private static string DescribeMissingHelper(string helper)
        => $"找不到随包修复脚本 {helper}（只有通过 deb 安装才会随包提供一键修复）";

    /// <summary>
    /// Why the elevated path did not run. A process that never started is pkexec itself being
    /// absent (no polkit installed, or PATH without it); anything else ran and failed - a
    /// desktop without a polkit authentication agent, a dismissed dialog, or a helper that
    /// could not finish its promised steps (a missing udevadm or usermod exits non-zero on
    /// purpose, so a partial fix is never reported as Fixed) - and its own stderr is the most
    /// specific reason there is, quoted ahead of the generic causes.
    /// </summary>
    private static string DescribeElevationFailure(ProcessResult result)
    {
        if (result.ExitCode < 0)
        {
            return "未找到 pkexec（当前 PATH 中可能没有它，或未安装 polkit）";
        }

        var detail = Summarize(result);

        return string.IsNullOrEmpty(detail)
            ? $"pkexec 执行失败（退出码 {result.ExitCode}）"
            : $"pkexec 执行失败（退出码 {result.ExitCode}）：{detail}。"
                + "可能原因：桌面上没有 polkit 认证代理、认证对话框被取消，或修复脚本未能完成全部步骤";
    }

    /// <summary>
    /// What the helper reports it did, so the success line states facts rather than a fixed
    /// script: the helper prints one line per action it actually performed, and exits non-zero
    /// when a promised step could not run.
    /// </summary>
    private static string DescribeHelperWork(ProcessResult result)
    {
        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines.Length > 0 ? string.Join("；", lines) : "udev 规则与用户组已更新";
    }

    /// <summary>stderr preferred, stdout as the fallback, trimmed and truncated for a status line.</summary>
    private static string Summarize(ProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        detail = detail.Trim();

        return detail.Length > 200 ? detail[..200] + "…" : detail;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A staged rules file left in the temp directory is harmless; the fix's outcome
            // must not be rewritten because the cleanup failed.
        }
    }
}
