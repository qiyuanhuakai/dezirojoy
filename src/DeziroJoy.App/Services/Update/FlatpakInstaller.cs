namespace DeziroJoy.App.Services;

/// <summary>
/// The Flatpak installer that installs nothing. A Flatpak sandbox has no package database the app
/// can write and no way to reach the host's polkit, so an automatic update is structurally
/// impossible from inside it - the honest move is to name the command and leave the relaunch to
/// the user, which is why <see cref="UpdateInstallResult.RelaunchHandled"/> is false and the
/// result is a failure carrying the guidance.
/// </summary>
/// <remarks>
/// The orchestrator only calls an installer whose <see cref="Supports"/> answered, and a Flatpak
/// install ships no downloadable package, so the check/download steps surface this same guidance
/// before an install is ever attempted. This installer is the belt-and-braces path: whatever
/// future flow reaches <see cref="InstallAsync"/> for a Flatpak gets the command, not a pretend
/// install.
/// </remarks>
public sealed class FlatpakInstaller : IUpdateInstaller
{
    public bool Supports(InstallKind kind) => kind == InstallKind.Flatpak;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
        => Task.FromResult(UpdateInstallResult.Failed(
            "此版本通过 Flatpak 安装，无法在沙箱内自动更新。请在终端运行 flatpak update dezirojoy 完成更新。"));
}
