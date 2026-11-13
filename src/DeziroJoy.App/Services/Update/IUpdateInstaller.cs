namespace DeziroJoy.App.Services;

/// <summary>
/// What one install attempt needs: the verified package on disk, the form it must be
/// installed as, and the release it came from (for messages and for the platform installers
/// that have to name a file on a command line).
/// </summary>
public sealed record UpdateInstallRequest(
    InstallKind Kind,
    string Rid,
    SemanticVersion Version,
    string TagName,
    string PackagePath,
    string PackageName);

/// <summary>
/// Outcome of one install attempt. A failure carries a Chinese, user-presentable reason; a
/// half-finished install is never reported as a success, exactly as in
/// <see cref="DeviceAccessFixer"/>.
/// </summary>
public sealed record UpdateInstallResult
{
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Whether this installer arranged the relaunch itself. Windows does it from the setup
    /// program's conditional [Run] entry (Restart Manager closes the updating process mid
    /// install), the Linux packages from a detached chain that survives this process, and an
    /// AppImage by starting the renamed file - so the orchestrator never starts a successor
    /// on its own and never races one.
    /// </summary>
    public bool RelaunchHandled { get; init; }

    /// <summary>A Chinese, user-presentable failure reason; set only on failure.</summary>
    public string? ErrorMessage { get; init; }

    public static UpdateInstallResult Ok(bool relaunchHandled = true)
        => new() { Succeeded = true, RelaunchHandled = relaunchHandled };

    public static UpdateInstallResult Failed(string errorMessage)
        => new() { Succeeded = false, ErrorMessage = errorMessage };
}

/// <summary>
/// Installs a verified package for one install form. The orchestrator owns everything around
/// the install - stop the daemon, ask the tray to exit, verify the package, report progress -
/// and delegates only the part that genuinely differs per platform to an implementation of
/// this interface. Adding a platform therefore means adding one class and registering it,
/// never touching the orchestration again.
/// </summary>
/// <remarks>
/// <para>
/// An implementation must not return before the new files are in place: the orchestrator has
/// already stopped the daemon and the tray, so anything still running from the old install
/// directory is the installer's own doing. Where the install can only finish after this
/// process is gone - dpkg and rpm cannot replace a running binary (ETXTBSY), and on Windows the
/// setup's Restart Manager has to close this GUI to replace the files it holds open - the
/// implementation hands the work to a detached process and reports success, with
/// <see cref="UpdateInstallResult.RelaunchHandled"/> describing who restarts the app.
/// </para>
/// <para>
/// <see cref="Supports"/> is the capability gate and it is consulted before anything is
/// stopped, so a machine this installer cannot handle is told so while its daemon is still
/// running.
/// </para>
/// </remarks>
public interface IUpdateInstaller
{
    /// <summary>Whether this installer can install the given form on this machine.</summary>
    bool Supports(InstallKind kind);

    /// <summary>Installs the verified package named by <paramref name="request"/>.</summary>
    Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The placeholder that stands in for every platform installer until they land. It supports
/// nothing, so the orchestrator's guidance path is what the user sees - the daemon keeps
/// running and the update card points at the release page - instead of a stopped daemon
/// followed by a failure.
/// </summary>
public sealed class PendingPlatformUpdateInstaller : IUpdateInstaller
{
    public bool Supports(InstallKind kind) => false;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(UpdateInstallResult.Failed(
            "自动安装暂未支持：平台安装器尚未实现，请手动下载并安装新版本。"));
}
