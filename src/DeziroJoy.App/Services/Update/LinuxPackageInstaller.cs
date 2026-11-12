using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Installs the .deb or .rpm update on Linux. Both package managers refuse to overwrite a binary
/// a running process has mapped (ETXTBSY), and the orchestrator has already stopped the daemon and
/// asked the tray to exit - but this process itself still holds the GUI binary - so the install
/// cannot run here. It is handed to a detached shell chain that outlives this process: elevate
/// through pkexec, install the package, re-engage the daemon's user unit when one is installed,
/// and relaunch the app. This installer reports success the moment the chain is launched and
/// <see cref="UpdateInstallResult.RelaunchHandled"/> is true, because the chain - not the host -
/// restarts the app.
/// </summary>
/// <remarks>
/// The pkexec elevation reuses the same <see cref="IProcessRunner"/> seam as
/// <see cref="DeviceAccessFixer"/>, and the rpm path degrades to a copyable command exactly the
/// way the fixer degrades when its elevated path is unavailable: a missing dnf is caught here,
/// before the chain is launched, because a chain that fails after this process is gone has no way
/// to report it.
/// </remarks>
public sealed class LinuxPackageInstaller : IUpdateInstaller
{
    /// <summary>Where the .deb and the .rpm both install the application; the relaunch targets it.</summary>
    private const string InstallDirectory = "/opt/dezirojoy";

    private const string ExecutableName = "DeziroJoy";

    /// <summary>
    /// The daemon's systemd user unit, started without its <c>.service</c> suffix (systemctl adds
    /// it). It re-engages the unit when the user opted into the resident daemon and is a no-op -
    /// <c>2&gt;/dev/null || true</c> - otherwise; the relaunched GUI's own supervisor starts the
    /// daemon either way.
    /// </summary>
    private const string DaemonUnit = "dezirojoy-daemon";

    private readonly IProcessRunner _process;
    private readonly IUpdateProcessLauncher _launcher;

    internal LinuxPackageInstaller(
        IProcessRunner? process = null,
        IUpdateProcessLauncher? launcher = null)
    {
        _process = process ?? new ProcessRunner();
        _launcher = launcher ?? new UpdateProcessLauncher();
    }

    public bool Supports(InstallKind kind) => kind is InstallKind.Deb or InstallKind.Rpm;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind == InstallKind.Rpm && !DnfIsAvailable())
        {
            // An rpm system without dnf (a machine the package manager never made it onto): the
            // detached chain's pkexec dnf would fail silently once this process is gone, so the
            // copyable command is handed over now instead.
            return Task.FromResult(UpdateInstallResult.Failed(
                $"未找到 dnf，无法自动安装 rpm 更新。请手动执行：pkexec dnf install -y {Quote(request.PackagePath)}"));
        }

        var install = request.Kind == InstallKind.Rpm
            ? $"pkexec dnf install -y {Quote(request.PackagePath)}"
            : $"pkexec apt-get install -y {Quote(request.PackagePath)}";

        var script =
            $"{install} && (systemctl --user start {DaemonUnit} 2>/dev/null || true) && exec {InstallDirectory}/{ExecutableName}";

        // The chain is passed to sh verbatim through ArgumentList, so the shell - not .NET's
        // argument parser - does the quoting: the double-quoted package path survives intact.
        var startInfo = new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        var launched = _launcher.Start(startInfo);

        return Task.FromResult(launched
            ? UpdateInstallResult.Ok(relaunchHandled: true)
            : UpdateInstallResult.Failed("无法启动安装进程，更新未开始。请前往发布页手动下载安装。"));
    }

    /// <summary>
    /// Whether <c>dnf</c> is on PATH. Checked only for the rpm form, whose installer command is
    /// dnf; the deb form's apt-get is a given on any machine whose dpkg database reports the
    /// package installed (which is what made this an <see cref="InstallKind.Deb"/> in the first
    /// place).
    /// </summary>
    private bool DnfIsAvailable()
        => _process.Run("sh", "-c \"command -v dnf\"").ExitCode == 0;

    private static string Quote(string path) => $"\"{path}\"";
}
