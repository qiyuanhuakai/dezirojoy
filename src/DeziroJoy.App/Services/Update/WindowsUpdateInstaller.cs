using System.Diagnostics;
using System.Text;

namespace DeziroJoy.App.Services;

/// <summary>
/// Installs the per-user Inno Setup update on Windows through an external watchdog
/// (<see cref="WindowsUpdateWatchdog"/>): a temporary PowerShell script that outlives this
/// process, runs the setup with a visible progress window, waits for its exit code, starts
/// the installed <c>DeziroJoy.exe</c> on success and, on every failure path, writes - and
/// opens - a diagnostic log and best-effort restarts the version still on disk. The install is
/// reported the moment the watchdog is up, and
/// <see cref="UpdateInstallResult.RelaunchHandled"/> is true because the watchdog, not the
/// host, restarts the app.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog is what makes the update reliable, and it exists for two reasons that both
/// bit alpha.2:
/// <list type="number">
/// <item>
/// The GUI holds its own <c>DeziroJoy.exe</c> and the libraries beside it open for exactly as
/// long as it lives, while the setup's Restart Manager (<c>CloseApplications=yes</c> in
/// DeziroJoy.iss) needs that same GUI closed to replace them. A GUI that waited on the setup
/// kept the files locked, and the setup's DeleteFile failed with access denied (error 5) and
/// rolled back - so the install has to outlive the GUI.
/// </item>
/// <item>
/// The relaunch used to be the setup's own conditional <c>[Run]</c> entry. That entry fires
/// the moment the new files are in place, which is not necessarily after the old GUI has
/// released the per-user instance lease - and a successor that loses that arbitration simply
/// exits again. The user's report was exactly that: the app closed for the update and never
/// came back, with no progress and no failure message anywhere, because the GUI had already
/// exited and could read neither the setup's exit code nor its log.
/// </item>
/// </list>
/// The watchdog fixes both: it waits for the old process to be gone before it starts the
/// setup, and it starts the successor only after it has seen exit code 0 itself.
/// </para>
/// <para>
/// The setup is started <c>/SILENT</c>, not <c>/VERYSILENT</c>: a silent install with no
/// progress window is indistinguishable from a hung one, and the window is the only feedback
/// left once this GUI is gone. The relaunch is the watchdog's alone - DeziroJoy.iss carries
/// no relaunch entry of its own - so an update can never start the app twice.
/// </para>
/// </remarks>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private readonly IUpdateProcessLauncher _launcher;
    private readonly Func<int> _processId;
    private readonly Func<string> _installDirectory;
    private readonly Func<string> _logDirectory;

    /// <summary>
    /// Internal so the seams stay an implementation detail: production and the update host use
    /// the defaults - the real launcher, this process, the registered install directory and
    /// the per-user log directory - while tests inject a recording launcher and known paths,
    /// so no real setup.exe, no Restart Manager and no PowerShell is ever involved.
    /// </summary>
    internal WindowsUpdateInstaller(
        IUpdateProcessLauncher? launcher = null,
        Func<int>? processId = null,
        Func<string>? installDirectory = null,
        Func<string>? logDirectory = null)
    {
        _launcher = launcher ?? new UpdateProcessLauncher();
        _processId = processId ?? (static () => Environment.ProcessId);
        _installDirectory = installDirectory
            ?? (static () => WindowsUpdateInstallLocation.ResolveInstallDirectory());
        _logDirectory = logDirectory
            ?? (static () => WindowsUpdateWatchdog.ResolveLogDirectory());
    }

    public bool Supports(InstallKind kind) => kind == InstallKind.WindowsInstall;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        var installDirectory = _installDirectory();
        var logDirectory = _logDirectory();

        // The script, the Inno log and the watchdog's own log share one stamp and one
        // directory, so a user - or a developer reading a report - finds all three together.
        // A failure here is a failure before anything was stopped or closed: the package is
        // still on disk and the user is told where to get the update by hand.
        string scriptPath;
        string setupLogPath;
        string watchdogLogPath;
        try
        {
            Directory.CreateDirectory(logDirectory);
            var stamp = WindowsUpdateWatchdog.Stamp;
            scriptPath = WindowsUpdateWatchdog.ScriptPath(logDirectory, stamp);
            setupLogPath = WindowsUpdateWatchdog.SetupLogPath(logDirectory, stamp);
            watchdogLogPath = WindowsUpdateWatchdog.WatchdogLogPath(logDirectory, stamp);

            // A UTF-8 BOM, because Windows PowerShell 5.1 reads a BOM-less .ps1 as the system
            // codepage - and the script's own messages are Chinese.
            File.WriteAllText(scriptPath, WindowsUpdateWatchdog.Script, new UTF8Encoding(true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(UpdateInstallResult.Failed(
                $"无法准备 Windows 更新进程（{exception.Message}）。更新未开始，请前往发布页手动下载安装。"));
        }

        var startInfo = WindowsUpdateWatchdog.BuildStartInfo(
            scriptPath,
            request.PackagePath,
            installDirectory,
            WindowsUpdateInstallLocation.ResolveExecutable(installDirectory),
            setupLogPath,
            watchdogLogPath,
            _processId());

        // Detached on purpose: the watchdog must outlive this process, which is about to exit
        // so the setup can replace the files it holds open.
        if (!_launcher.Start(startInfo))
        {
            return Task.FromResult(UpdateInstallResult.Failed(
                $"无法启动 Windows 安装进程（{request.PackagePath}）。更新未开始，请前往发布页手动下载安装。"));
        }

        // From here the watchdog owns the rest: it waits for this process to be gone, runs the
        // setup, reads its exit code and starts the new version - or opens the log.
        return Task.FromResult(UpdateInstallResult.Ok(relaunchHandled: true));
    }
}
