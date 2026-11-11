using System.ComponentModel;
using System.Diagnostics;
using DeziroJoy.Platform.Linux;

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
/// <item>Probe first (<see cref="LinuxRuntimeProbe.Probe"/>): the fix aims at the gap the
/// kernel actually reports - unreadable event nodes, an unwritable /dev/uinput, or no nodes
/// at all - and a machine with no gap is told so instead of running pkexec for nothing.</item>
/// <item>Stage the rules: the udev text is generated from
/// <see cref="LinuxDeviceAccess.UdevRulesText"/> - the same single source every other surface
/// quotes - into a temporary file, so the helper installs the app's rules verbatim instead of
/// carrying a second copy that can drift.</item>
/// <item>Run the packaged helper as root through pkexec. It writes the rules, reloads udev and
/// adds the invoking user to the input group; the group half only takes effect at the next
/// login, which the result says.</item>
/// <item>Degrade wherever the elevated path is unavailable - no pkexec, no packaged helper
/// (a build that was never installed as a .deb), or a pkexec that failed because the desktop
/// has no polkit agent or the dialog was dismissed - and hand the user the copyable manual
/// commands instead, with the reason on the button's tooltip.</item>
/// </list>
/// </summary>
public sealed class DeviceAccessFixer : IDeviceAccessFixer
{
    private readonly IProcessRunner _process;
    private readonly Func<LinuxRuntimeHints> _probe;
    private readonly Func<string> _helperPath;

    /// <summary>
    /// Internal so the seams stay an implementation detail: production and the settings card
    /// use the defaults, tests inject a fake runner, a fake probe and a fake helper path.
    /// </summary>
    internal DeviceAccessFixer(
        IProcessRunner? process = null,
        Func<LinuxRuntimeHints>? probe = null,
        Func<string>? helperPath = null)
    {
        _process = process ?? new ProcessRunner();
        _probe = probe ?? LinuxRuntimeProbe.Probe;
        _helperPath = helperPath ?? (static () => LinuxDeviceAccess.FixHelperPath);
    }

    public DeviceAccessFixResult Fix()
    {
        if (!OperatingSystem.IsLinux())
        {
            return DeviceAccessFixResult.Unsupported("设备访问修复仅支持 Linux（evdev/uinput 权限模型）。");
        }

        LinuxRuntimeHints hints;

        try
        {
            hints = _probe();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A probe is diagnostics; when even that cannot run, the fix has nothing to aim
            // at and must not pretend it fixed something.
            return DeviceAccessFixResult.Failed("探测失败", $"无法探测设备访问状态：{exception.Message}");
        }

        if (hints.DeviceGuidance is null && hints.OutputGuidance is null)
        {
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
    /// errored - and its own stderr is the most specific reason there is.
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
                + "可能是桌面上没有 polkit 认证代理，或认证对话框被取消";
    }

    /// <summary>
    /// What the helper reports it did, so the success line states facts rather than a fixed
    /// script: the helper prints one line per action it actually performed.
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
