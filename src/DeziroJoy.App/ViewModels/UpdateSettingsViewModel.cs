using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeziroJoy.App.Services;

namespace DeziroJoy.App.ViewModels;

/// <summary>
/// The update card of the settings panel and the host of the update flow. It drives the
/// <see cref="UpdateOrchestrator"/> state machine - check, opt-in download with progress,
/// one-click install, restart - keeps the two persisted toggles in the shared
/// <see cref="ThemeService"/>, and raises <see cref="RestartRequested"/> when the app has to
/// exit so the successor can take over. The network, the download and every install step are
/// seams of the orchestrator, so this view model stays Avalonia-free and testable; the two
/// platform actions it deliberately does not perform - opening the release page and exiting
/// the process for a restart - are raised as events for the view to answer.
/// </summary>
/// <remarks>
/// <para>
/// The flow is deliberately not "one button updates everything". The check may run on its
/// own (startup, when the user opted in), the download may run on its own (when the user
/// opted into auto-download), and the install is always one explicit click: a silent install
/// would close the user's window mid-work on Windows (Restart Manager) or pop a polkit prompt
/// on Linux for no gain. Every failure - offline, timeout, checksum mismatch, an install form
/// with no package - comes back as a presentable Chinese result, never as an exception; the
/// one exception is a cancelled token, which only window teardown or the user's own 取消
/// button ever produces.
/// </para>
/// <para>
/// State changes are published from whatever thread advanced the phase - the orchestrator is
/// driven from one thread per operation but never promises the UI thread - so the properties
/// here are plain notifications, exactly as the orchestrator's own progress forwarding is.
/// </para>
/// </remarks>
public partial class UpdateSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>Pause between the successor chain's "is the old process gone?" polls.</summary>
    private const string SuccessorPollPause = "0.2";

    private readonly ThemeService? _settings;

    private readonly UpdateOrchestrator _orchestrator;

    private readonly bool _ownsOrchestrator;

    private readonly Func<string?> _appImagePath;

    private readonly IUpdateProcessLauncher _launcher;

    private readonly Func<int> _processId;

    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The cancel source the 取消 button targets; replaced per download.</summary>
    private CancellationTokenSource? _downloadCancel;

    /// <summary>
    /// Internal so the seams stay an implementation detail: production and the window use the
    /// defaults - the real orchestrator with the four platform installers registered ahead of
    /// the placeholder, the real launcher, the real process - while tests inject an
    /// orchestrator over fakes, a recording launcher and a known AppImage path.
    /// </summary>
    internal UpdateSettingsViewModel(
        IUpdateService service,
        ThemeService? settings = null,
        UpdateOrchestrator? orchestrator = null,
        Func<string?>? appImagePath = null,
        IUpdateProcessLauncher? processLauncher = null,
        Func<int>? processId = null)
    {
        ArgumentNullException.ThrowIfNull(service);

        _settings = settings;
        _appImagePath = appImagePath ?? (static () => Environment.GetEnvironmentVariable("APPIMAGE"));
        _launcher = processLauncher ?? new UpdateProcessLauncher();
        _processId = processId ?? (static () => Environment.ProcessId);
        _currentVersionText = service.CurrentVersionText;

        _orchestrator = orchestrator ?? new UpdateOrchestrator(
            updateService: service,
            installers:
            [
                new WindowsUpdateInstaller(),
                new LinuxPackageInstaller(),
                new AppImageInstaller(),
                new FlatpakInstaller(),
                new PendingPlatformUpdateInstaller(),
            ]);
        _ownsOrchestrator = orchestrator is null;
        _orchestrator.StateChanged += OnStateChanged;
        _orchestrator.RestartRequested += OnRestartRequested;

        _suppressAutoCheckChange = true;
        _autoCheckEnabled = settings?.AutoCheckUpdatesEnabled ?? false;
        _suppressAutoCheckChange = false;

        _suppressAutoDownloadChange = true;
        _autoDownloadEnabled = settings?.AutoDownloadUpdatesEnabled ?? false;
        _suppressAutoDownloadChange = false;
    }

    /// <summary>The running version, e.g. "0.0.1-beta.5-preview".</summary>
    [ObservableProperty]
    private string _currentVersionText;

    /// <summary>
    /// The outcome line: "正在检查更新…", "发现新版本：…（发布于 …）", "已是最新版本。", the
    /// orchestrator's progress wording, or a Chinese failure reason.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = string.Empty;

    /// <summary>True when <see cref="ResultText"/> reports a failure rather than an outcome.</summary>
    [ObservableProperty]
    private bool _isResultError;

    /// <summary>True while a check is in flight; the manual check button is disabled against it.</summary>
    [ObservableProperty]
    private bool _isChecking;

    /// <summary>True while the package downloads; drives the progress bar and the cancel button.</summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>True while the installer runs; the install button is disabled against it.</summary>
    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>True while any step that must not be re-entered is running.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True when a newer release is known; drives the release-page button.</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>The release page of the newest release, for the download button.</summary>
    [ObservableProperty]
    private string _releasePageUrl = string.Empty;

    /// <summary>0..1 while the package downloads; 1 once the verified file is in place.</summary>
    [ObservableProperty]
    private double _downloadProgress;

    /// <summary>Whether a release check runs once at startup; persisted.</summary>
    [ObservableProperty]
    private bool _autoCheckEnabled;

    /// <summary>Whether a check that finds a release downloads it right away; persisted. Never installs.</summary>
    [ObservableProperty]
    private bool _autoDownloadEnabled;

    /// <summary>
    /// What this install form cannot do by itself, or null when it can: the Flatpak command
    /// and the portable-copy release page. Shown beside the buttons so the user is told before
    /// clicking a download that would only fail.
    /// </summary>
    [ObservableProperty]
    private string? _installGuidance;

    private bool _suppressAutoCheckChange;

    private bool _suppressAutoDownloadChange;

    public bool HasResult => ResultText.Length > 0;

    public bool HasInstallGuidance => InstallGuidance is { Length: > 0 };

    /// <summary>Whether the download button is offered: a release is known and this form has a package.</summary>
    public bool CanDownload =>
        HasUpdate
        && InstallGuidance is null
        && !_orchestrator.IsBusy
        && _orchestrator.Phase is UpdatePhase.Available or UpdatePhase.Failed;

    /// <summary>Whether the cancel button is offered: only a download in flight can be cancelled.</summary>
    public bool CanCancelDownload => _orchestrator.Phase == UpdatePhase.Downloading;

    /// <summary>
    /// Whether the install button is offered: a verified package is waiting, or the install it
    /// starts is under way - visible but disabled while busy, so one click is one install.
    /// </summary>
    public bool CanInstall => _orchestrator.Phase is UpdatePhase.ReadyToInstall or UpdatePhase.Installing;

    /// <summary>Raised when the user asks for the release page; the view owns opening it.</summary>
    public event EventHandler? OpenReleasePageRequested;

    /// <summary>
    /// Raised once the new version is in place and the host may exit this process: the
    /// successor is either the platform installer's own doing (Windows setup, the Linux
    /// package chain) or a detached chain this view model already started for the AppImage
    /// swap. The view answers by exiting the process - a hard exit, because the window's own
    /// close path would hand the instance lease to a freshly spawned tray and the successor
    /// would lose its arbitration to that.
    /// </summary>
    public event EventHandler? RestartRequested;

    [RelayCommand]
    private async Task CheckForUpdateAsync() => await CheckForUpdatesAsync(CancellationToken.None);

    [RelayCommand]
    private async Task DownloadUpdateAsync() => await DownloadAsync(CancellationToken.None);

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_orchestrator.IsBusy)
        {
            // One explicit click: a second one while the install runs must not restart the
            // stop sequence, and it must not overwrite the phase already under way.
            return;
        }

        await _orchestrator.InstallAsync(CancellationToken.None);
    }

    /// <summary>
    /// Cancels the download in flight. The orchestrator's contract makes this the one failure
    /// that arrives as an exception: the phase already moved to 已取消。, and the cancel text
    /// stays standing as the user's confirmation.
    /// </summary>
    [RelayCommand]
    private void CancelUpdate() => _downloadCancel?.Cancel();

    [RelayCommand]
    private void OpenReleasePage() => OpenReleasePageRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Runs the check and publishes its outcome. The manual button and the startup auto-check
    /// both go through here, so the two can never drift apart. A cancelled check (window
    /// teardown) says nothing: the panel keeps whatever it had.
    /// </summary>
    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_orchestrator.IsBusy)
        {
            return;
        }

        try
        {
            await _orchestrator.CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ClearResult();
        }
    }

    /// <summary>
    /// Downloads the package this install form needs and verifies it. The opt-in auto-download
    /// and the manual download button both go through here. Every failure - no package for this
    /// form, a missing sidecar, a checksum mismatch, a dead network - comes back as a
    /// presentable result; only a cancelled token throws, and the phase already says 已取消。
    /// </summary>
    internal async Task<bool> DownloadAsync(CancellationToken cancellationToken)
    {
        if (_orchestrator.IsBusy || !HasUpdate || InstallGuidance is not null)
        {
            return false;
        }

        _downloadCancel = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _downloadCancel.Token,
            cancellationToken);

        try
        {
            return await _orchestrator.DownloadAsync(
                new ProgressSink(value => DownloadProgress = value),
                linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Window teardown, not the user's 取消: the panel says nothing about it.
                ClearResult();
            }

            return false;
        }
    }

    /// <summary>The fire-and-forget download the opt-in toggle started, for tests to await.</summary>
    internal Task<bool>? AutoDownload { get; private set; }

    /// <summary>The orchestrator this card drives; exposed for the window's lifetime management.</summary>
    internal UpdateOrchestrator Orchestrator => _orchestrator;

    /// <summary>Whether this card built the orchestrator itself and the window may dispose it.</summary>
    internal bool OwnsOrchestrator => _ownsOrchestrator;

    /// <summary>
    /// Stops the fire-and-forget auto-download the startup check started and releases the
    /// cancel sources. Called as the window goes away, so no download is still running against
    /// an orchestrator about to be disposed. Both sources are cancelled before they are
    /// disposed: a download in flight must take its own cancellation path - the downloader
    /// deletes its partial file on a cancelled token - rather than discover a disposed source
    /// and throw past that cleanup.
    /// </summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _downloadCancel?.Cancel();
        _downloadCancel?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnStateChanged()
    {
        var phase = _orchestrator.Phase;

        IsChecking = phase == UpdatePhase.Checking;
        IsDownloading = phase == UpdatePhase.Downloading;
        IsInstalling = phase == UpdatePhase.Installing;
        IsBusy = _orchestrator.IsBusy;
        HasUpdate = _orchestrator.Latest is not null;
        DownloadProgress = _orchestrator.DownloadProgress;
        ReleasePageUrl = _orchestrator.Latest?.PageUrl ?? string.Empty;
        ResultText = TextFor(phase);
        IsResultError = phase == UpdatePhase.Failed;

        RefreshInstallGuidance();
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanCancelDownload));
        OnPropertyChanged(nameof(CanInstall));

        if (phase == UpdatePhase.Available
            && AutoDownloadEnabled
            && InstallGuidance is null
            && !_orchestrator.IsBusy)
        {
            // Opt-in auto-download: the check found a release, so the package fetches itself
            // while the user reads the card. The install stays one explicit click.
            AutoDownload = DownloadAsync(_lifetime.Token);
        }
    }

    /// <summary>
    /// The line the card shows for one phase. The rich "found a release" wording and the
    /// up-to-date line are the card's own established copy; every other phase quotes the
    /// orchestrator's status, so the card and the flow can never disagree about what is
    /// happening.
    /// </summary>
    private string TextFor(UpdatePhase phase)
    {
        switch (phase)
        {
            case UpdatePhase.Available when _orchestrator.Latest is { } latest:
                var published = latest.PublishedAt is { } at
                    ? at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : "日期未知";
                return $"发现新版本：{latest.TagName}（发布于 {published}）";

            case UpdatePhase.UpToDate:
                return "已是最新版本。";

            case UpdatePhase.Failed:
                return _orchestrator.ErrorMessage ?? "更新失败。";

            case UpdatePhase.Idle:
                return string.Empty;

            default:
                return _orchestrator.StatusText ?? string.Empty;
        }
    }

    /// <summary>
    /// Reads the install form once the flow has a reason to care and names what it cannot do.
    /// The detection runs behind the orchestrator's cached property, so a machine pays for it
    /// at most once, and only after a check actually found something.
    /// </summary>
    private void RefreshInstallGuidance()
    {
        var guidance = _orchestrator.Phase switch
        {
            UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.ReadyToInstall
                or UpdatePhase.Installing or UpdatePhase.Restarting => GuidanceFor(_orchestrator.Install.Kind),
            _ => null,
        };

        if (InstallGuidance == guidance)
        {
            return;
        }

        InstallGuidance = guidance;
        OnPropertyChanged(nameof(HasInstallGuidance));
    }

    private static string? GuidanceFor(InstallKind kind) => kind switch
    {
        InstallKind.Flatpak =>
            "此版本通过 Flatpak 安装，无法在应用内自动更新。请在终端运行 flatpak update dezirojoy 完成更新。",
        InstallKind.Portable =>
            "此版本为免安装或开发构建，无法自动更新，请前往发布页手动下载。",
        _ => null,
    };

    private void ClearResult()
    {
        ResultText = string.Empty;
        IsResultError = false;
    }

    /// <summary>
    /// The installer just put the new version in place. When it arranged the relaunch itself -
    /// the Windows setup program's conditional run entry, the Linux package chain - this process
    /// is on its way out anyway and nothing is started here. When it did not - the AppImage
    /// swap - the successor is started through a detached chain that waits for this process to
    /// exit first: the old GUI holds the per-user/session instance lease for as long as it
    /// lives, so a successor started any earlier loses the arbitration and exits again. That is
    /// exactly the failure the AppImage installer used to have when it started the new image
    /// itself, while its own host still held the lease.
    /// </summary>
    private void OnRestartRequested()
    {
        if (!_orchestrator.RelaunchHandled && !StartSuccessor())
        {
            // The swap is committed on disk but nothing will bring the new version up: keep
            // this process alive - it runs the old version from the old inode - and say so,
            // instead of exiting into an app that never starts.
            ResultText = "更新已安装，但无法自动重启：请手动重新打开应用。";
            IsResultError = true;
            return;
        }

        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Starts the detached successor chain for the AppImage form: wait until this process is
    /// gone, then exec the swapped-in image. The wait is the whole point - the old GUI must
    /// release the instance lease before the new image arbitrates for it. Returns false when
    /// there is no image to exec or the chain could not be started.
    /// </summary>
    private bool StartSuccessor()
    {
        if (_orchestrator.Install.Kind != InstallKind.AppImage)
        {
            return false;
        }

        var image = _appImagePath();
        if (string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            return false;
        }

        // Passed to sh verbatim through ArgumentList, so the shell - not .NET's argument
        // parser - does the quoting, exactly as the Linux package installer's chain is.
        var script =
            $"while kill -0 {_processId()} 2>/dev/null; do sleep {SuccessorPollPause}; done; exec \"{image}\"";

        var startInfo = new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        return _launcher.Start(startInfo);
    }

    partial void OnAutoCheckEnabledChanged(bool value)
    {
        if (_suppressAutoCheckChange)
        {
            return;
        }

        _settings?.SetAutoCheckUpdates(value);
    }

    partial void OnAutoDownloadEnabledChanged(bool value)
    {
        if (_suppressAutoDownloadChange)
        {
            return;
        }

        _settings?.SetAutoDownloadUpdates(value);
    }

    /// <summary>
    /// Reports download progress straight onto the view model. Deliberately not a
    /// <see cref="Progress{T}"/>: that would post every report to a captured synchronization
    /// context, so a headless caller could not observe the final value deterministically.
    /// </summary>
    private sealed class ProgressSink(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }
}
