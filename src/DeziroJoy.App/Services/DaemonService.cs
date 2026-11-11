using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DeziroJoy.Platform.Linux;
using DeziroJoy.Platform.Windows.Elevation;

namespace DeziroJoy.App.Services;

/// <summary>Outcome kind of a daemon/autostart operation.</summary>
public enum DaemonOp
{
    /// <summary>The operation finished synchronously; the queried state is now current.</summary>
    Completed,

    /// <summary>An elevated relaunch was started; the state will change once that copy finishes.</summary>
    Pending,

    /// <summary>The operation was attempted and failed.</summary>
    Failed,

    /// <summary>The platform does not support the feature (neither Windows nor Linux).</summary>
    Unsupported,
}

/// <summary>Result of a daemon/autostart operation: what happened and a ready-to-show message.</summary>
public readonly record struct DaemonActionResult(DaemonOp Result, string Message)
{
    public static DaemonActionResult Completed(string message) => new(DaemonOp.Completed, message);

    public static DaemonActionResult Pending(string message) => new(DaemonOp.Pending, message);

    public static DaemonActionResult Failed(string message) => new(DaemonOp.Failed, message);

    public static DaemonActionResult Unsupported(string message) => new(DaemonOp.Unsupported, message);
}

/// <summary>
/// Reports and changes the resident-daemon and logon-autostart state. Every value is
/// read from the real scheduler (schtasks on Windows, systemd user units on Linux) and the
/// real process token, so the settings card never shows fabricated state. Where the platform
/// does not support the feature, <see cref="IsSupported"/> is false and the operations say so
/// instead of pretending.
/// </summary>
public interface IDaemonService
{
    bool IsSupported { get; }

    bool IsElevated { get; }

    bool IsDaemonInstalled { get; }

    /// <summary>
    /// True when the resident daemon is actually running right now - systemd's "active" on
    /// Linux, the scheduled task's state on Windows. Defaults to false so a platform that
    /// cannot tell stays honest instead of guessing.
    /// </summary>
    bool IsDaemonRunning => false;

    bool IsAutostartInstalled { get; }

    /// <summary>
    /// True where the daemon runs as the current user and elevation changes nothing by design:
    /// Linux injects through uinput, a kernel interface UIPI does not gate, so there is no
    /// elevated token to seek. Windows is false.
    /// </summary>
    bool IsSameUserModel => false;

    /// <summary>
    /// The resident daemon's run level ("Highest" / "LeastPrivilege" / null when not installed).
    /// This, not the GUI's own token, decides whether injection can reach elevated targets.
    /// Meaningless where <see cref="IsSameUserModel"/> holds.
    /// </summary>
    string? DaemonRunLevel { get; }

    void Refresh();

    DaemonActionResult InstallDaemon();

    DaemonActionResult UninstallDaemon();

    DaemonActionResult SetAutostart(bool enabled);
}

/// <summary>
/// Default <see cref="IDaemonService"/> backed by schtasks and the Windows token. Install and
/// uninstall need an administrator token even without <c>/RL HIGHEST</c>, so an unelevated caller
/// is handed to a UAC-elevated relaunch of itself (the same path the CLI flags use); the result is
/// then <see cref="DaemonOp.Pending"/> until the elevated copy finishes and the card is refreshed.
/// </summary>
public sealed class WindowsDaemonService : IDaemonService
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsElevated => IsSupported && WindowsProcessElevation.IsElevated;

    public bool IsDaemonInstalled { get; private set; }

    public bool IsAutostartInstalled { get; private set; }

    public string? DaemonRunLevel { get; private set; }

    public void Refresh()
    {
        if (!IsSupported)
        {
            IsDaemonInstalled = false;
            IsAutostartInstalled = false;
            DaemonRunLevel = null;
            return;
        }

        IsDaemonInstalled = QueryTask(DaemonInstaller.TaskName);
        IsAutostartInstalled = QueryTask(AutostartInstaller.TaskName);
        DaemonRunLevel = IsDaemonInstalled ? QueryRunLevel(DaemonInstaller.TaskName) : null;
    }

    public DaemonActionResult InstallDaemon()
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持常驻服务（仅 Windows）。");
        }

        if (!TryResolveExecutable(out var executable, out var error))
        {
            return DaemonActionResult.Failed(error);
        }

        if (!IsElevated)
        {
            return Relaunch("--install-daemon", "注册常驻服务");
        }

        return DaemonInstaller.Install(executable)
            ? DaemonActionResult.Completed("已注册常驻服务计划任务，登录时将自动运行后台映射。")
            : DaemonActionResult.Failed("注册常驻服务失败，请查看 schtasks 输出。");
    }

    public DaemonActionResult UninstallDaemon()
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持常驻服务（仅 Windows）。");
        }

        if (!IsElevated)
        {
            return Relaunch("--uninstall-daemon", "移除常驻服务");
        }

        return DaemonInstaller.Uninstall()
            ? DaemonActionResult.Completed("已移除常驻服务计划任务。")
            : DaemonActionResult.Failed("移除常驻服务失败（可能本来就不存在）。");
    }

    public DaemonActionResult SetAutostart(bool enabled)
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持登录自启动（仅 Windows）。");
        }

        if (!enabled)
        {
            if (!IsElevated)
            {
                return Relaunch("--uninstall-autostart", "关闭登录自启动");
            }

            return AutostartInstaller.Uninstall()
                ? DaemonActionResult.Completed("已关闭登录自启动。")
                : DaemonActionResult.Failed("关闭登录自启动失败（可能本来就不存在）。");
        }

        if (!TryResolveExecutable(out var executable, out var error))
        {
            return DaemonActionResult.Failed(error);
        }

        if (!IsElevated)
        {
            return Relaunch("--install-autostart", "启用登录自启动");
        }

        return AutostartInstaller.Install(executable)
            ? DaemonActionResult.Completed("已启用登录自启动，下次登录时将自动打开应用。")
            : DaemonActionResult.Failed("启用登录自启动失败，请查看 schtasks 输出。");
    }

    private static DaemonActionResult Relaunch(string flag, string action)
    {
        return WindowsProcessElevation.RelaunchElevated(flag)
            ? DaemonActionResult.Pending($"已请求管理员权限以{action}，请在提权窗口中确认，完成后点击“刷新”。")
            : DaemonActionResult.Failed($"未获得管理员权限（可能取消了 UAC 提示），无法{action}。");
    }

    private static bool TryResolveExecutable(out string executable, out string error)
    {
        var path = Environment.ProcessPath;

        if (string.IsNullOrEmpty(path) || IsDotNetHost(path))
        {
            executable = string.Empty;
            error = "请直接运行构建产物 DeziroJoy.exe，不要通过 dotnet run。";
            return false;
        }

        executable = path;
        error = string.Empty;
        return true;
    }

    private static bool IsDotNetHost(string executable)
        => IsDotNetHostExecutable(executable);

    /// <summary>
    /// True when the path is the .NET muxer rather than a published product binary. Registering
    /// the muxer would create a task/unit that never starts the daemon.
    /// </summary>
    internal static bool IsDotNetHostExecutable(string executable)
        => string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static bool QueryTask(string taskName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c schtasks /Query /TN \"{taskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit();

            return process is not null && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the task's <c>&lt;RunLevel&gt;</c> from its XML. XML is used rather than the
    /// CSV/table listing because every column name in those is localised, while the XML
    /// element name is not. Returns null when the run level cannot be read (a non-admin
    /// caller may be denied XML access to an admin-created task), which the caller must
    /// treat as "unknown", never as "elevated".
    /// </summary>
    private static string? QueryRunLevel(string taskName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c schtasks /Query /TN \"{taskName}\" /XML")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            var xml = process?.StandardOutput.ReadToEnd();
            process?.WaitForExit();

            if (string.IsNullOrEmpty(xml) || process is null || process.ExitCode != 0)
            {
                return null;
            }

            var match = Regex.Match(xml, "<RunLevel>([^<]+)</RunLevel>", RegexOptions.IgnoreCase);

            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>One systemctl --user invocation: the exit code and whatever it printed.</summary>
internal readonly record struct SystemCtlResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs <c>systemctl --user</c> commands. The seam tests replace, so no test ever talks to a
/// real systemd instance - or needs one to exist.
/// </summary>
internal interface ISystemCtl
{
    SystemCtlResult Run(string arguments);
}

/// <summary>The production runner: a real systemctl process.</summary>
internal sealed class ProcessSystemCtl : ISystemCtl
{
    public SystemCtlResult Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("systemctl", $"--user {arguments}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return new SystemCtlResult(-1, string.Empty, "systemctl 无法启动");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return new SystemCtlResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // No systemctl on PATH (a container without systemd): the message carries the reason.
            return new SystemCtlResult(-1, string.Empty, exception.Message);
        }
    }
}

/// <summary>
/// Linux <see cref="IDaemonService"/> backed by a systemd <em>user</em> unit at
/// <c>~/.config/systemd/user/dezirojoy-daemon.service</c>.
/// <para>
/// No elevation is involved anywhere on this path, and that is deliberate: uinput is a kernel
/// interface UIPI does not gate, so the daemon runs as the current user exactly like the GUI
/// (DESIGN.md 常驻架构). What the daemon does need is device access - readable evdev nodes and
/// a writable /dev/uinput - which the settings card and the scan diagnostics explain.
/// </para>
/// <para>
/// Installing writes the unit, reloads the user manager, enables it for every login and starts
/// it now; uninstalling stops, disables and removes it. The unit deliberately hangs off
/// <c>default.target</c> and never <c>graphical-session.target</c>: uinput works headless, and
/// binding the daemon to a graphical login would break exactly the SSH and console sessions
/// that need mapping most. Surviving a logout needs linger, which the install result says.
/// </para>
/// </summary>
public sealed class LinuxDaemonService : IDaemonService
{
    /// <summary>The unit file name inside <c>~/.config/systemd/user</c>.</summary>
    public const string UnitName = "dezirojoy-daemon.service";

    private readonly ISystemCtl _systemCtl;
    private readonly Func<string> _unitDirectory;
    private readonly Func<string?> _executable;

    /// <summary>
    /// Internal so the systemctl seam stays an implementation detail: production and the CLI
    /// use the defaults, tests inject a fake runner and a fixture directory.
    /// </summary>
    internal LinuxDaemonService(
        ISystemCtl? systemCtl = null,
        Func<string>? unitDirectory = null,
        Func<string?>? executable = null)
    {
        _systemCtl = systemCtl ?? new ProcessSystemCtl();
        _unitDirectory = unitDirectory ?? (static () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "systemd",
            "user"));
        _executable = executable ?? (static () => Environment.ProcessPath);
    }

    public bool IsSupported => OperatingSystem.IsLinux();

    /// <inheritdoc/>
    public bool IsSameUserModel => true;

    /// <inheritdoc/>
    public bool IsElevated => false;

    public bool IsDaemonInstalled { get; private set; }

    public bool IsDaemonRunning { get; private set; }

    public bool IsAutostartInstalled { get; private set; }

    /// <inheritdoc/>
    public string? DaemonRunLevel => null;

    /// <summary>Full path of the unit file this service installs and removes.</summary>
    public string UnitPath => Path.Combine(_unitDirectory(), UnitName);

    public void Refresh()
    {
        if (!IsSupported)
        {
            IsDaemonInstalled = false;
            IsDaemonRunning = false;
            IsAutostartInstalled = false;
            return;
        }

        IsDaemonInstalled = File.Exists(UnitPath);
        IsDaemonRunning = Query("is-active", "active");
        IsAutostartInstalled = Query("is-enabled", "enabled");
    }

    public DaemonActionResult InstallDaemon()
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持常驻服务（仅 Windows 与 Linux）。");
        }

        if (!TryResolveExecutable(out var executable, out var error))
        {
            return DaemonActionResult.Failed(error);
        }

        try
        {
            Directory.CreateDirectory(_unitDirectory());
            File.WriteAllText(UnitPath, BuildUnitFile(executable));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DaemonActionResult.Failed($"写入 systemd 用户单元失败：{exception.Message}");
        }

        var reload = _systemCtl.Run("daemon-reload");
        if (!reload.Succeeded)
        {
            return DaemonActionResult.Failed(DescribeFailure("重载 systemd 用户配置", reload));
        }

        var enable = _systemCtl.Run($"enable {UnitName}");
        if (!enable.Succeeded)
        {
            return DaemonActionResult.Failed(DescribeFailure("启用常驻服务", enable));
        }

        var start = _systemCtl.Run($"start {UnitName}");
        if (!start.Succeeded)
        {
            return DaemonActionResult.Failed(DescribeFailure("启动常驻服务", start));
        }

        return DaemonActionResult.Completed(
            $"已安装并启动 systemd 用户单元 {UnitName}，登录时将自动运行。"
            + $"若需退出登录后继续常驻，请执行 {LinuxDeviceAccess.LingerCommand}。");
    }

    public DaemonActionResult UninstallDaemon()
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持常驻服务（仅 Windows 与 Linux）。");
        }

        // Stopping and disabling a unit that was never enabled is not an error - systemctl
        // reports it, and removing the file below is the real operation either way.
        _ = _systemCtl.Run($"stop {UnitName}");
        _ = _systemCtl.Run($"disable {UnitName}");

        try
        {
            if (File.Exists(UnitPath))
            {
                File.Delete(UnitPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DaemonActionResult.Failed($"删除 systemd 用户单元失败：{exception.Message}");
        }

        var reload = _systemCtl.Run("daemon-reload");
        if (!reload.Succeeded)
        {
            return DaemonActionResult.Failed(DescribeFailure("重载 systemd 用户配置", reload));
        }

        return DaemonActionResult.Completed($"已停止并移除 systemd 用户单元 {UnitName}。");
    }

    public DaemonActionResult SetAutostart(bool enabled)
    {
        if (!IsSupported)
        {
            return DaemonActionResult.Unsupported("当前平台不支持登录自启动（仅 Windows 与 Linux）。");
        }

        if (!enabled)
        {
            return AutostartInstaller.Uninstall()
                ? DaemonActionResult.Completed("已关闭登录自启动（移除 ~/.config/autostart/DeziroJoy.desktop）。")
                : DaemonActionResult.Failed("关闭登录自启动失败（可能本来就不存在）。");
        }

        if (!TryResolveExecutable(out var executable, out var error))
        {
            return DaemonActionResult.Failed(error);
        }

        return AutostartInstaller.Install(executable)
            ? DaemonActionResult.Completed(
                "已启用登录自启动，下次登录时将自动打开应用（~/.config/autostart/DeziroJoy.desktop）。")
            : DaemonActionResult.Failed("启用登录自启动失败，请检查 ~/.config/autostart 是否可写。");
    }

    /// <summary>
    /// The unit file contents. Minimal and GUI-free on purpose: <c>After=default.target</c>
    /// (never graphical-session.target - uinput works headless, and tying the daemon to a
    /// graphical login would break the SSH and console sessions that need it most) and
    /// <c>Restart=on-failure</c> so a crash comes back with no GUI around to notice.
    /// </summary>
    internal static string BuildUnitFile(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return $$"""
            [Unit]
            Description=DeziroJoy 手柄映射守护进程（systemd 用户单元）
            After=default.target

            [Service]
            Type=simple
            ExecStart="{{executablePath}}" --daemon
            Restart=on-failure
            RestartSec=2

            [Install]
            WantedBy=default.target
            """;
    }

    private bool Query(string verb, string expectedOutput)
    {
        var result = _systemCtl.Run($"{verb} {UnitName}");

        // The verb's own exit code is the answer; the printed state is checked as well so a
        // localised or future output can never be mistaken for the expected one.
        return result.Succeeded
            && result.StandardOutput.Trim().Equals(expectedOutput, StringComparison.Ordinal);
    }

    private static string DescribeFailure(string action, SystemCtlResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        detail = detail.Trim();

        if (detail.Length > 200)
        {
            detail = detail[..200] + "…";
        }

        return string.IsNullOrEmpty(detail)
            ? $"{action}失败（systemctl 退出码 {result.ExitCode}）。"
            : $"{action}失败：{detail}";
    }

    private bool TryResolveExecutable(out string executable, out string error)
    {
        var path = _executable();

        if (string.IsNullOrEmpty(path) || WindowsDaemonService.IsDotNetHostExecutable(path))
        {
            executable = string.Empty;
            error = "请直接运行构建产物 DeziroJoy，不要通过 dotnet run。";
            return false;
        }

        executable = path;
        error = string.Empty;
        return true;
    }
}
