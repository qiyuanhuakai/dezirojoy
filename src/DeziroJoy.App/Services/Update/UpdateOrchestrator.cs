using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>Where the update flow stands. One value at a time, and every failure is final.</summary>
public enum UpdatePhase
{
    /// <summary>Nothing has been asked for yet.</summary>
    Idle,

    /// <summary>The releases list is being queried.</summary>
    Checking,

    /// <summary>The running version is the newest published release.</summary>
    UpToDate,

    /// <summary>A newer release was found and can be downloaded.</summary>
    Available,

    /// <summary>The package is being downloaded and hashed.</summary>
    Downloading,

    /// <summary>The package is on disk and its SHA256 matched the release's sidecar.</summary>
    ReadyToInstall,

    /// <summary>The daemon and the tray are being stopped, or the installer is running.</summary>
    Installing,

    /// <summary>The new version is in place; the app is expected to be replaced now.</summary>
    Restarting,

    /// <summary>
    /// The flow stopped with a reason in <see cref="UpdateOrchestrator.ErrorMessage"/>. Every
    /// phase before <see cref="ReadyToInstall"/> can be retried; an install failure leaves
    /// whatever the installer left, which its message describes.
    /// </summary>
    Failed,
}

/// <summary>
/// The one-click update flow: check (already here), download (opt-in), verify, stop what
/// holds the old binary, install, restart. It owns the order of those steps and delegates
/// only the platform-specific install to an <see cref="IUpdateInstaller"/>.
/// </summary>
/// <remarks>
/// <para>
/// The order of the stop steps is a hard constraint, not a preference. On Linux, dpkg and
/// rpm cannot replace a binary a running process has mapped (ETXTBSY), so the daemon and the
/// tray must both be gone before the installer runs. On Windows the elevated daemon can only
/// be stopped over its IPC channel - a medium-integrity process cannot terminate it - which
/// is exactly what the default daemon seam does, over an endpoint that never starts a daemon:
/// a liveness probe that launched one would pop UAC for a daemon that was not even running,
/// and after a successful shutdown would resurrect the daemon it just stopped. "Gone" there
/// means the daemon's process, not only its pipe: the elevated daemon releases the pipe as part
/// of exiting and can still be unwinding behind it, so the default daemon seam waits for the
/// process it named before the install is allowed to start. The tray is
/// asked to exit over the activation channel, because a tray left running holds the old
/// binary and the instance lease: without the request, a relaunch on Linux would wake the old
/// tray instead of the new copy.
/// </para>
/// <para>
/// The restart is deliberately not this class's job. The platform installers arrange it -
/// Windows from the setup program's conditional [Run] entry, the Linux packages from a
/// detached chain that outlives this process, an AppImage by starting the file it renamed
/// into place - and the relaunched process simply re-runs the instance arbitration
/// (<see cref="GuiInstanceGuard"/>), which it now wins because the old holder is gone. That
/// is why no new IPC was added for it.
/// </para>
/// <para>
/// <see cref="StateChanged"/> is raised on the thread that advanced the phase; the host is
/// responsible for marshalling it to its UI thread. The orchestrator itself is driven from
/// one thread - <see cref="IsBusy"/> is what keeps a second call from racing the first.
/// </para>
/// </remarks>
public sealed class UpdateOrchestrator : IDisposable
{
    private readonly IUpdateService _updateService;
    private readonly UpdateDownloader _downloader;
    private readonly IReadOnlyList<IUpdateInstaller> _installers;
    private readonly Func<InstallKindInfo> _detectInstall;
    private readonly Func<CancellationToken, Task<bool>> _stopDaemon;
    private readonly Func<CancellationToken, Task<bool>> _askTrayExit;
    private readonly List<IDisposable> _owned = [];

    private InstallKindInfo? _install;
    private VerifiedPackage? _package;

    /// <summary>
    /// Builds the flow around injectable seams. Production passes nothing and gets the real
    /// <see cref="UpdateService"/>, the real downloader, the placeholder installer and the
    /// production process stops; tests pass fakes so no network, daemon, tray or registry is
    /// involved.
    /// </summary>
    /// <param name="installers">
    /// The installers to pick from, in order: the first one whose <see cref="IUpdateInstaller.Supports"/>
    /// answers for the detected install form handles the install. Passing null installs the
    /// placeholder, which supports nothing - the guidance path - so a later mission adds its
    /// platform installer ahead of it and changes nothing else here.
    /// </param>
    public UpdateOrchestrator(
        IUpdateService? updateService = null,
        UpdateDownloader? downloader = null,
        IEnumerable<IUpdateInstaller>? installers = null,
        Func<InstallKindInfo>? detectInstall = null,
        Func<CancellationToken, Task<bool>>? stopDaemon = null,
        Func<CancellationToken, Task<bool>>? askTrayExit = null)
    {
        _updateService = updateService ?? Track(new UpdateService());
        _downloader = downloader ?? Track(new UpdateDownloader());
        _installers = installers is null
            ? [new PendingPlatformUpdateInstaller()]
            : [.. installers];
        _detectInstall = detectInstall ?? InstallKindDetector.Detect;
        _stopDaemon = stopDaemon ?? (cancellationToken =>
            UpdateProcessCoordinator.StopDaemonAsync(cancellationToken));
        _askTrayExit = askTrayExit ?? UpdateProcessCoordinator.AskTrayExitAsync;
    }

    /// <summary>Where the flow stands; the UI renders this and <see cref="StatusText"/>.</summary>
    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    /// <summary>A short Chinese phrase for what is happening or what to do next.</summary>
    public string? StatusText { get; private set; }

    /// <summary>The Chinese reason of the last failure; set only while <see cref="Phase"/> is <see cref="UpdatePhase.Failed"/>.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>0..1 while a package downloads; 1 once the verified file is in place.</summary>
    public double DownloadProgress { get; private set; }

    /// <summary>The newest release, once a check found one.</summary>
    public ReleaseInfo? Latest { get; private set; }

    /// <summary>How this copy was installed, detected on first use.</summary>
    public InstallKindInfo Install => _install ??= _detectInstall();

    /// <summary>The verified package on disk, once the download and its checksum succeeded.</summary>
    public string? PackagePath => _package?.Path;

    /// <summary>True while a step that must not be re-entered is running.</summary>
    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    /// <summary>
    /// Raised whenever the phase or the status text moved. Progress does not raise it - a
    /// caller that wants it passes its own <see cref="IProgress{T}"/> to
    /// <see cref="DownloadAsync"/> and reads <see cref="DownloadProgress"/>.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised once the new version is in place, with <see cref="RelaunchHandled"/> already
    /// set: the host shuts its GUI down and, when the installer did not arrange the relaunch
    /// itself, starts the successor.
    /// </summary>
    public event Action? RestartRequested;

    /// <summary>
    /// Whether the installer that just ran arranged the relaunch itself. Set before
    /// <see cref="RestartRequested"/> fires, so the handler knows whether it has to start the
    /// successor: Windows relaunches from the setup program's conditional [Run] entry, the
    /// Linux packages from a detached chain, an AppImage by starting the file it renamed into
    /// place - and an installer that did none of that leaves the work to the host.
    /// </summary>
    public bool RelaunchHandled { get; private set; }

    /// <summary>
    /// Queries the releases list. The result is the underlying
    /// <see cref="IUpdateService.CheckForUpdateAsync"/> result, so the caller keeps the
    /// network, timeout and payload messages it already knows. A cancelled
    /// <paramref name="cancellationToken"/> throws after the phase moved to
    /// <see cref="UpdatePhase.Failed"/>; every other failure - including one nothing
    /// classified - comes back as a result with its reason, never as an exception that would
    /// leave the phase wedged.
    /// </summary>
    public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return Task.FromResult(UpdateCheckResult.Failed("正在处理更新，请稍候再试。"));
        }

        return CheckCoreAsync(cancellationToken);
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        Enter(UpdatePhase.Checking, "正在检查更新…");

        try
        {
            var result = await _updateService.CheckForUpdateAsync(cancellationToken).ConfigureAwait(false);

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    Latest = result.Latest;
                    _package = null;
                    DownloadProgress = 0d;
                    Enter(UpdatePhase.Available, $"发现新版本 {result.Latest?.TagName}。");
                    break;

                case UpdateCheckStatus.UpToDate:
                    Latest = null;
                    _package = null;
                    Enter(UpdatePhase.UpToDate, "当前已是最新版本。");
                    break;

                default:
                    Enter(
                        UpdatePhase.Failed,
                        "检查更新失败。",
                        result.ErrorMessage ?? "检查更新失败，请稍后再试。");
                    break;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            // A cancelled caller token is the one exception this flow propagates. The phase
            // must not stay busy, or every later call would be refused forever.
            Fail("已取消。", "更新已取消。");
            throw;
        }
        catch (Exception exception)
        {
            // Last resort: nothing here may reach the host as an unhandled exception with the
            // phase wedged - an unexpected failure is a presentable one, with its reason, the
            // way every other surface of this product reports failure.
            Trace.WriteLine($"[DeziroJoy] update check failed unexpectedly: {exception}");
            Fail("检查更新失败。", $"检查更新时发生意外错误：{exception.Message}");
            return UpdateCheckResult.Failed(ErrorMessage!);
        }
    }

    /// <summary>
    /// Downloads the package this install form needs and verifies it against the release's
    /// SHA256 sidecar. A form with no package - Flatpak, a portable copy - never downloads
    /// anything: the user is told what to do instead. A cancelled
    /// <paramref name="cancellationToken"/> throws after the phase moved to
    /// <see cref="UpdatePhase.Failed"/>; every other failure - including one nothing
    /// classified - comes back as a reason on the phase, never as an exception that would
    /// leave the phase wedged.
    /// </summary>
    public async Task<bool> DownloadAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            // Deliberately no state change: an operation is already in flight, and reporting
            // this one as failed would overwrite the phase that is still running.
            return false;
        }

        if (Latest is null)
        {
            Fail("下载失败。", "尚未发现可用的新版本，请先检查更新。");
            return false;
        }

        var install = Install;
        var expected = UpdateAssetSelector.PackageAssetName(install.Rid, install.Kind, Latest.Version);
        if (expected is null)
        {
            Fail("下载失败。", FormGuidance(install.Kind, Latest.PageUrl));
            return false;
        }

        var selection = UpdateAssetSelector.TrySelect(Latest, install);
        if (selection is null)
        {
            Fail(
                "下载失败。",
                $"此发布未提供适用于本机的更新包（{expected}），请前往发布页手动下载：{Latest.PageUrl}");
            return false;
        }

        Enter(UpdatePhase.Downloading, $"正在下载 {selection.PackageName}…");

        try
        {
            var result = await _downloader
                .DownloadAsync(
                    selection.Asset,
                    selection.ChecksumAsset,
                    new ProgressForward(progress, value => DownloadProgress = value),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded || result.FilePath is null)
            {
                Fail("下载失败。", result.ErrorMessage ?? "下载更新包失败，请稍后再试。");
                return false;
            }

            _package = new VerifiedPackage(selection, result.FilePath);
            Enter(UpdatePhase.ReadyToInstall, $"更新包已下载并校验通过（{selection.PackageName}）。");
            return true;
        }
        catch (OperationCanceledException)
        {
            // The downloader cleans up its partial file and rethrows on a cancelled caller
            // token; the phase must not stay busy, or every later call would be refused.
            Fail("已取消。", "更新已取消。");
            throw;
        }
        catch (Exception exception)
        {
            // Last resort - a failure the downloader did not classify (an unwritable cache
            // directory, say) is still a presentable failure rather than an exception that
            // would leave the host with a wedged phase.
            Trace.WriteLine($"[DeziroJoy] update download failed unexpectedly: {exception}");
            Fail("下载失败。", $"下载更新包时发生意外错误：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Installs the verified package. Everything that must be stopped is stopped first, and
    /// nothing is stopped when the install cannot even be attempted: the capability gate runs
    /// before the daemon, so a machine without an installer keeps its daemon and its tray.
    /// A cancelled <paramref name="cancellationToken"/> throws after the phase moved to
    /// <see cref="UpdatePhase.Failed"/>; every other failure - including one nothing
    /// classified - comes back as a reason on the phase, never as an exception that would
    /// leave the phase wedged at Installing.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            // Same guard as the check: a second click must not run the stop sequence twice,
            // and it must not overwrite the phase of the install already under way.
            return false;
        }

        var latest = Latest;
        if (_package is null || latest is null)
        {
            Fail("安装失败。", "没有已验证的更新包，请先下载更新。");
            return false;
        }

        var install = Install;
        var installer = _installers.FirstOrDefault(candidate => candidate.Supports(install.Kind));
        if (installer is null)
        {
            Fail("安装失败。", FormGuidance(install.Kind, latest.PageUrl));
            return false;
        }

        RelaunchHandled = false;

        try
        {
            Enter(UpdatePhase.Installing, "正在停止守护进程…");
            if (!await _stopDaemon(cancellationToken).ConfigureAwait(false))
            {
                Fail(
                    "安装失败。",
                    "无法停止守护进程，更新已取消；映射功能不受影响，请重试。");
                return false;
            }

            Enter(UpdatePhase.Installing, "正在请托盘退出…");
            if (!await _askTrayExit(cancellationToken).ConfigureAwait(false))
            {
                Fail(
                    "安装失败。",
                    "无法请托盘退出，更新已取消；守护进程已停止，请手动退出托盘后重启应用。");
                return false;
            }

            Enter(UpdatePhase.Installing, "正在安装更新…");

            var request = new UpdateInstallRequest(
                install.Kind,
                install.Rid,
                latest.Version,
                latest.TagName,
                _package.Path,
                _package.Selection.PackageName);

            var outcome = await installer.InstallAsync(request, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                Fail("安装失败。", outcome.ErrorMessage ?? "安装更新失败，请稍后再试。");
                return false;
            }

            // Read by the host when RestartRequested fires, so it knows whether it still has
            // to start the successor itself.
            RelaunchHandled = outcome.RelaunchHandled;
            Enter(UpdatePhase.Restarting, "更新已安装，正在重启…");
            RestartRequested?.Invoke();
            return true;
        }
        catch (OperationCanceledException)
        {
            // Whatever was stopped before the cancel is described by the message: the install
            // did not finish, and the phase must not stay busy.
            Fail("已取消。", "安装已取消；如已停止守护进程或托盘，请重启应用。");
            throw;
        }
        catch (Exception exception)
        {
            // Last resort - an installer that fails in a way it did not describe must not
            // leave the host with an unhandled exception and the phase wedged at Installing.
            Trace.WriteLine($"[DeziroJoy] update install failed unexpectedly: {exception}");
            Fail("安装失败。", $"安装更新时发生意外错误：{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// What to tell the user when this install form has no package to install. Flatpak gets
    /// its own command - the sandbox cannot be updated from inside it - and a portable copy
    /// gets the release page, because there is nothing there to replace in place.
    /// </summary>
    private static string FormGuidance(InstallKind kind, string pageUrl) => kind switch
    {
        InstallKind.Flatpak =>
            "此版本通过 Flatpak 安装，请在终端运行 flatpak update dezirojoy 完成更新。",
        InstallKind.Portable =>
            $"此版本为免安装或开发构建，无法自动更新，请前往发布页手动下载：{pageUrl}",
        _ =>
            $"此安装形式暂不支持自动安装，请前往发布页手动下载：{pageUrl}",
    };

    private void Enter(UpdatePhase phase, string status, string? error = null)
    {
        Phase = phase;
        StatusText = status;
        ErrorMessage = error;
        StateChanged?.Invoke();
    }

    private void Fail(string status, string error)
    {
        Enter(UpdatePhase.Failed, status, error);
    }

    /// <summary>
    /// Takes ownership of a default the caller did not inject, so disposing this orchestrator
    /// releases the HttpClient behind it. An injected instance stays the caller's.
    /// </summary>
    private T Track<T>(T value)
    {
        if (value is IDisposable disposable)
        {
            _owned.Add(disposable);
        }

        return value;
    }

    public void Dispose()
    {
        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }

        _owned.Clear();
    }

    /// <summary>A package whose SHA256 already matched, and the selection it came from.</summary>
    private sealed record VerifiedPackage(UpdateAssetSelection Selection, string Path);

    /// <summary>
    /// Forwards download progress to the caller and mirrors it into
    /// <see cref="DownloadProgress"/>. Deliberately not a <see cref="Progress{T}"/>: that
    /// would post every report to a captured synchronization context, so a headless caller
    /// could not observe the final value deterministically.
    /// </summary>
    private sealed class ProgressForward(IProgress<double>? inner, Action<double> onReport) : IProgress<double>
    {
        public void Report(double value)
        {
            onReport(value);
            inner?.Report(value);
        }
    }
}
