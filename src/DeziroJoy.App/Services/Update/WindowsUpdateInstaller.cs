using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Installs the per-user Inno Setup update on Windows. It launches the downloaded
/// <c>setup.exe</c> with the same silent flags the release workflow's verification uses
/// (<c>/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=…</c>, tools/Packaging/win/build-and-verify.ps1)
/// and returns the moment it is running, without waiting for it.
/// </summary>
/// <remarks>
/// <para>
/// Waiting is the bug, not the caution. The process running this installer is one of the
/// applications the setup's Restart Manager closes (<c>CloseApplications=yes</c> in DeziroJoy.iss),
/// and it holds its own <c>DeziroJoy.exe</c> and the libraries beside it open for exactly as long
/// as it lives - so an updater that waited on the installer kept the very files the installer was
/// about to replace locked, and the setup's DeleteFile failed with access denied (error 5) and
/// rolled back. The launch is therefore detached: no output redirection, no wait, and success is
/// reported the instant the setup is up, which lets the orchestrator raise
/// <see cref="UpdateOrchestrator.RestartRequested"/> so the host exits and releases its files
/// before the installer needs them.
/// </para>
/// <para>
/// The relaunch is deliberately NOT this installer's to make. The setup relaunches the app from
/// its conditional <c>[Run]</c> entry, compiled only when the build passes
/// <c>/DDeziroJoyUpdateRun</c>, which is why <see cref="UpdateInstallResult.RelaunchHandled"/> is
/// true here. The exit code and the log are the setup's to report: a GUI that has already exited
/// can read neither, and the conditional relaunch is what makes that safe - a setup that finished
/// brings the new version up, and one that did not leaves the old install in place with its log
/// on disk to diagnose from.
/// </para>
/// </remarks>
public sealed class WindowsUpdateInstaller : IUpdateInstaller
{
    private readonly IUpdateProcessLauncher _launcher;
    private readonly Func<string> _logPath;

    /// <summary>
    /// Internal so the launch and log-path seams stay an implementation detail: production and
    /// the update host use the defaults, tests inject a recording launcher and a known log path.
    /// </summary>
    internal WindowsUpdateInstaller(IUpdateProcessLauncher? launcher = null, Func<string>? logPath = null)
    {
        _launcher = launcher ?? new UpdateProcessLauncher();
        _logPath = logPath ?? (static () => Path.Combine(
            Path.GetTempPath(),
            $"dezirojoy-setup-{Guid.NewGuid():N}.log"));
    }

    public bool Supports(InstallKind kind) => kind == InstallKind.WindowsInstall;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        // The same command line the blocking runner used to build - the flags are the release
        // workflow's verified form - minus the redirection that turned the launch into a wait.
        var startInfo = new ProcessStartInfo(
            request.PackagePath,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=\"{_logPath()}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Detached on purpose: the setup must outlive this process, which is about to exit so
        // the installer can replace the files it has open.
        if (!_launcher.Start(startInfo))
        {
            return Task.FromResult(UpdateInstallResult.Failed(
                $"无法启动 Windows 安装程序（{request.PackagePath}）。请前往发布页手动下载安装。"));
        }

        // The setup relaunches the app from its conditional [Run] entry once the new files are
        // in place; this process exits now so it stops holding the files setup must replace.
        return Task.FromResult(UpdateInstallResult.Ok(relaunchHandled: true));
    }
}
