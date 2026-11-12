using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Installs a type-2 AppImage update by replacing the single image file in place. The running
/// image is renamed aside, the verified download is renamed into its place and made executable,
/// the new image is started, and the activation channel is probed to confirm it came up. On
/// success the aside copy is discarded; if the new image never answers, the swap is undone and
/// the old image restored - a real rollback that costs nothing, because an AppImage is one file.
/// </summary>
/// <remarks>
/// <para>
/// Linux allows renaming an executable a process has mapped, so this process keeps running from
/// the old inode (now named <c>.old</c>) while the new file takes the <c>$APPIMAGE</c> name. The
/// started image re-runs the instance arbitration and wins once the old holder is gone.
/// </para>
/// <para>
/// That last point is the probe's contract: the activation channel is scoped to one user/session,
/// so the probe only ever sees the new image once the updater's own GUI has released the instance
/// lease. The update host must therefore release that lease (its normal restart-on-success
/// shutdown) around the install; while it is still held, a Render-role probe is answered by the
/// still-running old GUI. This installer cannot release the lease itself - it does not own it -
/// so it treats "a Render answered" as ready and leaves the lease hand-off to the host.
/// </para>
/// </remarks>
public sealed class AppImageInstaller : IUpdateInstaller
{
    /// <summary>Pause between activation probes while waiting for the new image to come up.</summary>
    private static readonly TimeSpan PollPause = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long the new image gets to publish its activation channel. A .NET + Avalonia GUI takes
    /// a few seconds to boot, so the budget is generous; a corrupt image simply never answers.
    /// </summary>
    private static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(15);

    /// <summary>The probe budget of one activation attempt - short, because it is polled.</summary>
    private static readonly GuiActivationOptions ProbeOptions = new(
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The mode the swapped-in image needs: an AppImage is a self-mounting ELF, so it must be
    /// executable for <see cref="Process.Start"/> to exec it. The downloader wrote the bytes
    /// without the execute bit, so it is set here explicitly.
    /// </summary>
    private static readonly UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly Func<string?> _appImagePath;
    private readonly IUpdateProcessLauncher _launcher;
    private readonly Func<GuiInstanceIdentity, GuiInstanceRole, GuiActivationOptions, CancellationToken, Task<bool>> _probe;
    private readonly Func<Task> _pollDelay;
    private readonly TimeSpan _readyBudget;

    /// <summary>
    /// Internal so the seams stay an implementation detail: production and the update host use the
    /// defaults, tests inject the AppImage path, a recording launcher, a probe that answers on
    /// demand and an instant poll delay.
    /// </summary>
    internal AppImageInstaller(
        Func<string?>? appImagePath = null,
        IUpdateProcessLauncher? launcher = null,
        Func<GuiInstanceIdentity, GuiInstanceRole, GuiActivationOptions, CancellationToken, Task<bool>>? probe = null,
        Func<Task>? pollDelay = null,
        TimeSpan? readyBudget = null)
    {
        _appImagePath = appImagePath ?? (static () => Environment.GetEnvironmentVariable("APPIMAGE"));
        _launcher = launcher ?? new UpdateProcessLauncher();
        _probe = probe ?? (static (identity, role, options, cancellationToken) =>
            GuiActivationClient.TryProbeAsync(identity, role, options, cancellationToken));
        _pollDelay = pollDelay ?? (static () => Task.Delay(PollPause));
        _readyBudget = readyBudget ?? ReadyBudget;
    }

    public bool Supports(InstallKind kind) => kind == InstallKind.AppImage;

    public async Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        var image = _appImagePath();

        // No $APPIMAGE (an unpacked run - which the detector would call Portable, so this is a
        // guard) or a file that is already gone: there is nothing to replace in place.
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            return Degrade();
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
            // Nothing has been replaced yet: an unwritable image or directory is a guidance path,
            // not a half-finished install.
            return Degrade();
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
            // The swap-in failed: restore the old image before reporting, so the user is not left
            // without a runnable file, then hand over the manual path.
            RestoreOld(old, image);
            return Degrade();
        }

        if (!_launcher.Start(new ProcessStartInfo(image) { UseShellExecute = false }))
        {
            RollBack(old, image);
            return UpdateInstallResult.Failed("无法启动新版 AppImage，已回滚到旧版本。请前往发布页手动下载。");
        }

        if (await WaitForNewGuiAsync(cancellationToken).ConfigureAwait(false))
        {
            // The new image answered: the swap is committed and the aside copy is discarded.
            TryDelete(old);
            return UpdateInstallResult.Ok(relaunchHandled: true);
        }

        // The new image never came up within the budget: undo the swap, bring the old image back,
        // and report the failure honestly. The relaunched old copy is best-effort - in the common
        // case this very process is the old version and keeps running on the failure path, so the
        // restart only matters when it is not.
        RollBack(old, image);
        return UpdateInstallResult.Failed("新版 AppImage 未能启动，已回滚到旧版本。请前往发布页手动下载。");
    }

    /// <summary>Polls the activation channel until a GUI answers or the budget runs out.</summary>
    private async Task<bool> WaitForNewGuiAsync(CancellationToken cancellationToken)
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < _readyBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _probe(identity, GuiInstanceRole.Render, ProbeOptions, cancellationToken)
                .ConfigureAwait(false))
            {
                return true;
            }

            await _pollDelay().ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Restores the old image and starts it, undoing a failed swap end to end.</summary>
    private void RollBack(string old, string image)
    {
        RestoreOld(old, image);
        _launcher.Start(new ProcessStartInfo(image) { UseShellExecute = false });
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
            // A rollback that cannot finish is reported by the caller's failure message; the old
            // image is still on disk as .old, which the user can rename back by hand.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover .old is harmless; the committed update must not be rewritten because the
            // cleanup failed.
        }
    }

    private static UpdateInstallResult Degrade() => UpdateInstallResult.Failed(
        "无法定位或替换 AppImage 文件（可能没有写权限），已取消自更新。请手动下载新版 AppImage 替换原文件（需保留可执行权限）。");
}
