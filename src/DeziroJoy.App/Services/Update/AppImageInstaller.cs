namespace DeziroJoy.App.Services;

/// <summary>
/// Installs a type-2 AppImage update by replacing the single image file in place. The running
/// image is renamed aside, the verified download is renamed into its place and made executable,
/// and the install reports success - with <see cref="UpdateInstallResult.RelaunchHandled"/>
/// false, because starting the successor is deliberately not this installer's job.
/// </summary>
/// <remarks>
/// <para>
/// Linux allows renaming an executable a process has mapped, so this process keeps running from
/// the old inode (now named <c>.old</c>) while the new file takes the <c>$APPIMAGE</c> name. The
/// host then starts the swapped-in image only once this process has exited: the old GUI holds
/// the per-user/session instance lease for as long as it lives, and a successor started earlier
/// loses the instance arbitration to it and exits again. That ordering is why the relaunch moved
/// out of here - an installer that starts the new image during the install races the very lease
/// its own host still holds, and a readiness probe fired that early is answered by the old GUI,
/// so it would "confirm" a restart that never happened.
/// </para>
/// <para>
/// The swap is a real rollback net for everything up to the rename-in: an unwritable image or
/// directory restores the old file before reporting, so the user is never left without a
/// runnable image. Once the new file is in place the old image stays beside it as
/// <c>.old</c> - an AppImage is one file, so a manual rollback costs one rename.
/// </para>
/// </remarks>
public sealed class AppImageInstaller : IUpdateInstaller
{
    /// <summary>
    /// The mode the swapped-in image needs: an AppImage is a self-mounting ELF, so it must be
    /// executable for the host's relaunch to exec it. The downloader wrote the bytes without
    /// the execute bit, so it is set here explicitly.
    /// </summary>
    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly Func<string?> _appImagePath;

    /// <summary>
    /// Internal so the AppImage path stays an implementation detail: production and the update
    /// host use the real environment variable, tests hand in a temp file.
    /// </summary>
    internal AppImageInstaller(Func<string?>? appImagePath = null)
    {
        _appImagePath = appImagePath ?? (static () => Environment.GetEnvironmentVariable("APPIMAGE"));
    }

    public bool Supports(InstallKind kind) => kind == InstallKind.AppImage;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        var image = _appImagePath();

        // No $APPIMAGE (an unpacked run - which the detector would call Portable, so this is a
        // guard) or a file that is already gone: there is nothing to replace in place.
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            return Task.FromResult(Degrade());
        }

        var old = image + ".old";

        try
        {
            if (File.Exists(old))
            {
                File.Delete(old);
            }

            File.Move(image, old);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing has been replaced yet: an unwritable image or directory is a guidance
            // path, not a half-finished install.
            return Task.FromResult(Degrade());
        }

        try
        {
            File.Move(request.PackagePath, image);

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(image, ExecutableMode);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The swap-in failed: restore the old image before reporting, so the user is not
            // left without a runnable file, then hand over the manual path.
            RestoreOld(old, image);
            return Task.FromResult(Degrade());
        }

        // The new image is in place and the host relaunches it once this process is gone. The
        // aside copy stays on disk as the manual-rollback copy; a leftover .old is harmless.
        return Task.FromResult(UpdateInstallResult.Ok(relaunchHandled: false));
    }

    private static void RestoreOld(string old, string image)
    {
        try
        {
            if (File.Exists(image))
            {
                File.Delete(image);
            }

            if (File.Exists(old))
            {
                File.Move(old, image);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A rollback that cannot finish is reported by the caller's failure message; the
            // old image is still on disk as .old, which the user can rename back by hand.
        }
    }

    private static UpdateInstallResult Degrade() => UpdateInstallResult.Failed(
        "无法定位或替换 AppImage 文件（可能没有写权限），已取消自更新。请手动下载新版 AppImage 替换原文件（需保留可执行权限）。");
}
