using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
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

    /// <summary>The platform does not support the feature (non-Windows).</summary>
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
/// Reports and changes the resident-daemon and logon-autostart scheduled tasks. Every value is
/// read from the real task scheduler (schtasks) and the real process token, so the settings card
/// never shows fabricated state. Windows-only; elsewhere <see cref="IsSupported"/> is false.
/// </summary>
public interface IDaemonService
{
    bool IsSupported { get; }

    bool IsElevated { get; }

    bool IsDaemonInstalled { get; }

    bool IsAutostartInstalled { get; }

    /// <summary>
    /// The resident daemon's run level ("Highest" / "LeastPrivilege" / null when not installed).
    /// This, not the GUI's own token, decides whether injection can reach elevated targets.
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
