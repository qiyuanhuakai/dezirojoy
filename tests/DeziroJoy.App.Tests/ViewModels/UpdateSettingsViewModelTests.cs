using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The update surface: the manual check, the result wording, the persisted startup toggle,
/// the opt-in auto-download, the download progress and cancel, the one-click install and the
/// restart branches. The transport is faked at the <see cref="IUpdateService"/> seam and the
/// download at a stub <see cref="HttpMessageHandler"/>, so no test touches the network; the
/// install and the successor launch are recording seams.
/// </summary>
public sealed class UpdateSettingsViewModelTests : IDisposable
{
    private readonly string _directory;

    /// <summary>The downloader the orchestrator factory built; disposed with the test.</summary>
    private UpdateDownloader? _downloader;

    public UpdateSettingsViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        _downloader?.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static ReleaseInfo Release(string tag, DateTimeOffset? publishedAt, string pageUrl) => new()
    {
        TagName = tag,
        Version = SemanticVersion.Parse(tag),
        PublishedAt = publishedAt,
        PageUrl = pageUrl,
    };

    private static UpdateCheckResult NewerRelease() => UpdateCheckResult.Available(Release(
        "v0.0.2-alpha.1",
        new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero),
        "https://github.com/qiyuanhuakai/dezirojoy/releases/tag/v0.0.2-alpha.1"));

    [Fact]
    public async Task A_New_Release_Is_Reported_With_Its_Version_Date_And_Page()
    {
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(NewerRelease()));

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.HasUpdate);
        Assert.False(vm.IsResultError);
        Assert.Contains("v0.0.2-alpha.1", vm.ResultText);
        Assert.Contains("2026-02-01", vm.ResultText);
        Assert.Equal(
            "https://github.com/qiyuanhuakai/dezirojoy/releases/tag/v0.0.2-alpha.1",
            vm.ReleasePageUrl);
    }

    [Fact]
    public async Task Being_Up_To_Date_Says_So_And_Offers_No_Download()
    {
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(UpdateCheckResult.UpToDate()));

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasUpdate);
        Assert.False(vm.IsResultError);
        Assert.Equal("已是最新版本。", vm.ResultText);
    }

    [Fact]
    public async Task A_Failure_Is_Shown_In_The_Services_Chinese_Words()
    {
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(
            UpdateCheckResult.Failed("检查更新超时，请检查网络连接后重试。")));

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.True(vm.IsResultError);
        Assert.False(vm.HasUpdate);
        Assert.Equal("检查更新超时，请检查网络连接后重试。", vm.ResultText);
    }

    [Fact]
    public async Task The_Button_Runs_The_Check_And_Reports_Progress_While_It_Is_In_Flight()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>();
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(gate));

        var check = vm.CheckForUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsChecking);
        Assert.Equal("正在检查更新…", vm.ResultText);
        Assert.True(vm.HasResult);

        gate.SetResult(UpdateCheckResult.UpToDate());
        await check;

        Assert.False(vm.IsChecking);
        Assert.Equal("已是最新版本。", vm.ResultText);
    }

    [Fact]
    public async Task A_Second_Check_While_One_Is_Running_Is_Ignored()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>();
        var service = new FakeUpdateService(gate);
        var vm = new UpdateSettingsViewModel(service);

        var first = vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        var second = vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await second;

        Assert.Equal(1, service.Calls);

        gate.SetResult(UpdateCheckResult.UpToDate());
        await first;
    }

    [Fact]
    public async Task A_Cancelled_Check_Says_Nothing()
    {
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(
            new OperationCanceledException()));

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsChecking);
        Assert.False(vm.HasResult);
    }

    [Fact]
    public void Asking_For_The_Release_Page_Raises_An_Event_For_The_View()
    {
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(NewerRelease()));
        var raised = 0;
        vm.OpenReleasePageRequested += (_, _) => raised++;

        vm.OpenReleasePageCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void The_Startup_Toggle_Is_Off_By_Default_And_Persists_Through_The_Shared_Settings()
    {
        var settings = new ThemeService(_directory);
        settings.Load();
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(UpdateCheckResult.UpToDate()), settings);

        Assert.False(vm.AutoCheckEnabled);

        vm.AutoCheckEnabled = true;

        var reloaded = new ThemeService(_directory);
        reloaded.Load();
        Assert.True(reloaded.AutoCheckUpdatesEnabled);
        Assert.True(new UpdateSettingsViewModel(new FakeUpdateService(UpdateCheckResult.UpToDate()), reloaded).AutoCheckEnabled);
    }

    [Fact]
    public void Both_Toggles_Restore_From_The_Persisted_File_Across_A_Restart()
    {
        var settings = new ThemeService(_directory);
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(UpdateCheckResult.UpToDate()), settings);

        vm.AutoCheckEnabled = true;
        vm.AutoDownloadEnabled = true;

        // A restart builds a fresh service and a fresh card over it. The composition root
        // loads the file after the view models exist (App.InstallRenderProcess), so the
        // toggles must already hold the persisted values the moment the service is built -
        // exactly the order a real launch runs in.
        var restarted = new UpdateSettingsViewModel(
            new FakeUpdateService(UpdateCheckResult.UpToDate()),
            new ThemeService(_directory));

        Assert.True(restarted.AutoCheckEnabled);
        Assert.True(restarted.AutoDownloadEnabled);
    }

    [Fact]
    public void Turning_A_Toggle_Back_Off_Persists_That_Too()
    {
        var settings = new ThemeService(_directory);
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(UpdateCheckResult.UpToDate()), settings);

        vm.AutoCheckEnabled = true;
        vm.AutoDownloadEnabled = true;

        var restarted = new UpdateSettingsViewModel(
            new FakeUpdateService(UpdateCheckResult.UpToDate()),
            new ThemeService(_directory));

        Assert.True(restarted.AutoCheckEnabled);
        Assert.True(restarted.AutoDownloadEnabled);

        restarted.AutoCheckEnabled = false;
        restarted.AutoDownloadEnabled = false;

        var restartedAgain = new UpdateSettingsViewModel(
            new FakeUpdateService(UpdateCheckResult.UpToDate()),
            new ThemeService(_directory));

        Assert.False(restartedAgain.AutoCheckEnabled);
        Assert.False(restartedAgain.AutoDownloadEnabled);
    }

    [Fact]
    public async Task The_Update_Card_Holds_The_Persisted_Toggles_Under_The_Production_Startup_Order()
    {
        var seeded = new ThemeService(_directory);
        seeded.SetAutoCheckUpdates(true);
        seeded.SetAutoDownloadUpdates(true);

        // The order App.InstallRenderProcess runs in: the window's view model is built over
        // a freshly constructed service, and the file is only read afterwards (the load half
        // of the composition root's late ApplyPersisted). The card snapshots both toggles in
        // its constructor, so the values have to be there before that late load runs.
        var themes = new ThemeService(_directory);
        var window = CreateViewModel(themes, new FakeUpdateService(UpdateCheckResult.UpToDate()));
        themes.Load();

        Assert.True(window.Update.AutoCheckEnabled);
        Assert.True(window.Update.AutoDownloadEnabled);

        Assert.NotNull(window.StartupUpdateCheck);
        await window.StartupUpdateCheck!;
    }

    [Fact]
    public void The_Running_Version_Is_Shown_From_The_Service()
    {
        var vm = new UpdateSettingsViewModel(new FakeUpdateService(UpdateCheckResult.UpToDate()));

        Assert.Equal("0.0.1-beta.3-preview", vm.CurrentVersionText);
    }

    [Fact]
    public async Task The_Startup_Auto_Check_Runs_Once_When_The_Toggle_Is_On()
    {
        var settings = new ThemeService(_directory);
        settings.SetAutoCheckUpdates(true);
        var service = new FakeUpdateService(UpdateCheckResult.UpToDate());

        var vm = CreateViewModel(settings, service);

        Assert.NotNull(vm.StartupUpdateCheck);
        await vm.StartupUpdateCheck!;

        Assert.Equal(1, service.Calls);
        Assert.Equal("已是最新版本。", vm.Update.ResultText);
    }

    [Fact]
    public void No_Startup_Check_Runs_When_The_Toggle_Is_Off()
    {
        var settings = new ThemeService(_directory);
        var service = new FakeUpdateService(UpdateCheckResult.UpToDate());

        var vm = CreateViewModel(settings, service);

        Assert.Null(vm.StartupUpdateCheck);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void No_Startup_Check_Runs_Without_A_Settings_Service()
    {
        var service = new FakeUpdateService(UpdateCheckResult.UpToDate());

        var vm = CreateViewModel(null, service);

        Assert.Null(vm.StartupUpdateCheck);
        Assert.Equal(0, service.Calls);
    }

    private const string PackageUrl = "https://example.invalid/DeziroJoy_0.0.2_amd64.deb";

    private const string PackageName = "DeziroJoy_0.0.2_amd64.deb";

    private const string SidecarUrl = PackageUrl + ".sha256";

    /// <summary>The pid the successor chain is told to wait for, injected so the script is assertable.</summary>
    private const int SuccessorPid = 4242;

    private UpdateOrchestrator Orchestrator(
        IUpdateService? service = null,
        InstallKind kind = InstallKind.Deb,
        IReadOnlyList<IUpdateInstaller>? installers = null,
        HttpMessageHandler? handler = null,
        Task<bool>? stopDaemon = null)
    {
        var (name, url) = AssetFor(kind);
        var payload = PatternedPayload();
        handler ??= new StubHandler(assetUrl => assetUrl switch
        {
            _ when assetUrl == url => new ByteArrayContent(payload),
            _ when assetUrl == url + ".sha256" => new StringContent($"{ChecksumFor(payload)}\n"),
            _ => (HttpContent?)null,
        });

        _downloader = new UpdateDownloader(handler, TimeSpan.FromSeconds(30), () => Path.Combine(_directory, "cache"));

        return new UpdateOrchestrator(
            service ?? new FakeUpdateService(UpdateCheckResult.Available(ReleaseWithPackage(name, url))),
            _downloader,
            installers ?? [new RecordingInstaller()],
            detectInstall: () => new InstallKindInfo(kind, RidFor(kind)),
            stopDaemon: stopDaemon is null
                ? _ => Task.FromResult(true)
                : _ => stopDaemon,
            askTrayExit: (_, _) => Task.FromResult(true));
    }

    /// <summary>The package this install form downloads, named the way the release names it.</summary>
    private static (string Name, string Url) AssetFor(InstallKind kind) => kind switch
    {
        InstallKind.AppImage => (
            "DeziroJoy-0.0.2-x86_64.AppImage",
            "https://example.invalid/DeziroJoy-0.0.2-x86_64.AppImage"),
        InstallKind.WindowsInstall => (
            "DeziroJoy-0.0.2-win-x64-setup.exe",
            "https://example.invalid/DeziroJoy-0.0.2-win-x64-setup.exe"),
        _ => (PackageName, PackageUrl),
    };

    /// <summary>The RID this install form is detected with; the release assets are named for it.</summary>
    private static string RidFor(InstallKind kind)
        => kind == InstallKind.WindowsInstall ? "win-x64" : "linux-x64";

    /// <summary>
    /// A payload past the downloader's 80 KiB buffer, so the progress sink sees at least one
    /// intermediate value before the final 1.
    /// </summary>
    private static byte[] PatternedPayload()
    {
        var payload = new byte[200_000];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }

    private static string ChecksumFor(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ReleaseInfo ReleaseWithPackage(string name, string url) => new()
    {
        TagName = "v0.0.2",
        Version = SemanticVersion.Parse("0.0.2"),
        PublishedAt = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero),
        PageUrl = "https://example.invalid/release",
        Assets =
        [
            new ReleaseAsset { Name = name, DownloadUrl = url, Size = 200_000 },
            new ReleaseAsset { Name = name + ".sha256", DownloadUrl = url + ".sha256", Size = 64 },
        ],
    };

    private static UpdateSettingsViewModel Card(
        ThemeService? settings,
        UpdateOrchestrator orchestrator,
        Func<string?>? appImagePath = null,
        IUpdateProcessLauncher? launcher = null)
        => new(
            new FakeUpdateService(UpdateCheckResult.UpToDate()),
            settings,
            orchestrator,
            appImagePath,
            launcher,
            () => SuccessorPid);

    [Fact]
    public async Task A_Found_Release_Downloads_Itself_When_The_Auto_Download_Toggle_Is_On()
    {
        var settings = new ThemeService(_directory);
        settings.SetAutoDownloadUpdates(true);
        var vm = Card(settings, Orchestrator());

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(vm.AutoDownload);
        Assert.True(await vm.AutoDownload!);

        Assert.True(vm.CanInstall);
        Assert.Equal(1d, vm.DownloadProgress);
        Assert.Contains("校验通过", vm.ResultText);
        Assert.False(vm.IsResultError);
    }

    [Fact]
    public async Task No_Download_Runs_When_The_Auto_Download_Toggle_Is_Off()
    {
        var vm = Card(new ThemeService(_directory), Orchestrator());

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.Null(vm.AutoDownload);
        Assert.True(vm.CanDownload);
        Assert.False(vm.CanInstall);
    }

    [Fact]
    public async Task The_Download_Button_Downloads_The_Package_And_Reports_Progress()
    {
        var vm = Card(null, Orchestrator());
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        var reported = new List<double>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.DownloadProgress))
            {
                reported.Add(vm.DownloadProgress);
            }
        };

        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.Contains(reported, value => value is > 0d and < 1d);
        Assert.Equal(1d, vm.DownloadProgress);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public async Task A_User_Cancel_During_The_Download_Stops_It_And_Says_So()
    {
        var vm = Card(null, Orchestrator(handler: new GatedHandler()));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        var download = vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsDownloading);
        Assert.True(vm.CanCancelDownload);
        Assert.False(vm.CanDownload);

        vm.CancelUpdateCommand.Execute(null);
        await download;

        Assert.Equal("更新已取消。", vm.ResultText);
        Assert.False(vm.IsDownloading);
    }

    [Fact]
    public async Task A_Teardown_Cancel_During_The_Download_Says_Nothing()
    {
        var vm = Card(null, Orchestrator(handler: new GatedHandler()));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        using var teardown = new CancellationTokenSource();
        var download = vm.DownloadAsync(teardown.Token);

        Assert.True(vm.IsDownloading);

        // The window going away cancels the caller's token, not the card's own 取消 source:
        // the downloader cleans up its partial file on that token, and the panel says nothing
        // about the aborted download - exactly as a cancelled check always has.
        teardown.Cancel();
        Assert.False(await download);

        Assert.False(vm.IsDownloading);
        Assert.False(vm.HasResult);
        Assert.Equal(string.Empty, vm.ResultText);
    }

    [Fact]
    public async Task A_Failed_Download_Is_Presented_In_Chinese_And_Stays_Retryable()
    {
        var vm = Card(null, Orchestrator(handler: new MissingSidecarHandler()));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsResultError);
        Assert.Contains("SHA256", vm.ResultText);
        Assert.True(vm.CanDownload, "a failed download can be retried");
    }

    [Fact]
    public async Task The_Install_Button_Installs_The_Verified_Package_And_Asks_The_Host_To_Restart()
    {
        var installer = new RecordingInstaller();
        var launcher = new RecordingLauncher();
        var vm = Card(null, Orchestrator(installers: [installer]), launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Single(installer.Calls);
        Assert.Equal(UpdatePhase.Restarting, vm.Orchestrator.Phase);
        Assert.Contains("正在重启", vm.ResultText);
        Assert.Equal(1, restarted);

        // The installer arranged the relaunch itself, so the host starts no successor.
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task A_Second_Install_Click_While_One_Is_Running_Is_Ignored()
    {
        var installer = new RecordingInstaller(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var vm = Card(null, Orchestrator(installers: [installer]));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var install = vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsInstalling);
        Assert.True(vm.CanInstall, "the button stays visible while the install runs");
        Assert.True(vm.IsBusy);

        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Single(installer.Calls);

        installer.Release();
        await install;
    }

    [Fact]
    public async Task The_Windows_Install_Notice_Names_Every_Stage_Before_The_Click()
    {
        var vm = Card(null, Orchestrator(kind: InstallKind.WindowsInstall));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.CanInstall);
        Assert.True(vm.HasInstallNotice);

        // Every stage has to be readable before the click: this process closes seconds after
        // it, so a stage that was only described afterwards was never described at all.
        Assert.Contains("守护进程", vm.InstallNotice);
        Assert.Contains("托盘", vm.InstallNotice);
        Assert.Contains("关闭本程序", vm.InstallNotice);
        Assert.Contains("安装进度", vm.InstallNotice);
        Assert.Contains("自动重启", vm.InstallNotice);
        Assert.Contains("诊断日志", vm.InstallNotice);

        // And the failure destination is the real one, not a promise: the watchdog opens the
        // log it writes there.
        Assert.Contains(WindowsUpdateWatchdog.ResolveLogDirectory(), vm.InstallNotice!);
    }

    [Fact]
    public async Task A_Stop_That_Cannot_Prove_The_Daemon_Gone_Cancels_The_Install_And_Says_So()
    {
        // The update's gate is a proof, not a request: a daemon that is still resident when the
        // setup begins holds the install directory's files open and the setup's DeleteFile fails
        // with access denied (error 5). So the card says exactly that - the daemon could not be
        // confirmed gone - and mapping keeps working, rather than naming a stop that was merely
        // attempted.
        var vm = Card(
            null,
            Orchestrator(kind: InstallKind.WindowsInstall, stopDaemon: Task.FromResult(false)));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsResultError);
        Assert.Contains("无法确认守护进程已退出", vm.ResultText);
        Assert.Contains("映射功能不受影响", vm.ResultText);
        Assert.Equal(0, restarted); // nothing is installed and nothing is restarted
    }

    [Fact]
    public async Task The_Stop_Stages_Keep_Their_Own_Wording_While_The_Daemon_And_The_Tray_Are_Stopped()
    {
        // The install has not started yet: what the card says is what is happening now, which
        // on Windows is the daemon going down - not the handoff line that comes after it.
        var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Card(
            null,
            Orchestrator(kind: InstallKind.WindowsInstall, stopDaemon: stop.Task));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var install = vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsInstalling);
        Assert.Contains("守护进程", vm.ResultText);
        Assert.DoesNotContain("安装程序显示安装进度", vm.ResultText);
        Assert.True(vm.HasInstallNotice, "the sequence stays readable through the stop stages");

        stop.SetResult(true);
        await install;
    }

    [Fact]
    public async Task The_Windows_Installing_Line_Says_The_Setup_Shows_The_Progress_And_Restarts()
    {
        var installer = new RecordingInstaller(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var vm = Card(null, Orchestrator(kind: InstallKind.WindowsInstall, installers: [installer]));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var install = vm.InstallUpdateCommand.ExecuteAsync(null);

        // The last line this GUI ever shows: the setup takes the progress over from here, and
        // it is the one that restarts the app once it has finished.
        Assert.True(vm.IsInstalling);
        Assert.True(vm.IsProgressVisible, "the bar stays up while the install runs");
        Assert.Contains("安装程序显示安装进度", vm.ResultText);
        Assert.Contains("自动重启", vm.ResultText);
        Assert.True(vm.HasInstallNotice, "the sequence stays on screen until the restart is under way");

        installer.Release();
        await install;
    }

    [Fact]
    public async Task The_Progress_Bar_Is_Offered_Only_While_Work_Is_Running()
    {
        var vm = Card(null, Orchestrator(handler: new GatedHandler()));
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        // Idle: a progress bar with nothing to report is noise.
        Assert.False(vm.IsProgressVisible);

        var download = vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.IsDownloading);
        Assert.True(vm.IsProgressVisible);
        Assert.False(vm.IsInstalling, "the download's bar is the determinate one");

        vm.CancelUpdateCommand.Execute(null);
        await download;

        Assert.False(vm.IsProgressVisible);
    }

    [Fact]
    public async Task A_Package_Form_Gets_The_Generic_Install_Notice()
    {
        var vm = Card(null, Orchestrator());
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        Assert.True(vm.HasInstallNotice);
        Assert.Contains("SHA256", vm.InstallNotice);

        // The Windows sequence - stop the daemon, close, let the setup show its progress - is
        // not what a Linux package update does.
        Assert.DoesNotContain("安装进度", vm.InstallNotice);
    }

    [Fact]
    public async Task The_Windows_Install_Hands_The_Work_To_The_Watchdog_And_Asks_The_Host_To_Exit()
    {
        var launcher = new RecordingLauncher();
        var watchdog = new WindowsUpdateInstaller(
            launcher,
            () => SuccessorPid,
            () => _directory,
            () => Path.Combine(_directory, "logs"));
        var vm = Card(
            null,
            Orchestrator(kind: InstallKind.WindowsInstall, installers: [watchdog]),
            launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, restarted);
        Assert.Equal(UpdatePhase.Restarting, vm.Orchestrator.Phase);
        Assert.True(vm.Orchestrator.RelaunchHandled);

        // Exactly one process is started, and it is the watchdog rather than the setup: the
        // host neither runs the installer itself nor starts a successor of its own, so nothing
        // it holds stays locked through the install.
        var start = Assert.Single(launcher.Calls);
        Assert.Equal("powershell.exe", Path.GetFileName(start.FileName));
    }

    [Fact]
    public async Task The_AppImage_Restart_Starts_A_Detached_Chain_That_Waits_For_This_Process_To_Exit()    {
        var image = Path.Combine(_directory, "DeziroJoy-0.0.2-x86_64.AppImage");
        File.WriteAllText(image, "NEW IMAGE");
        var launcher = new RecordingLauncher();
        var vm = Card(
            null,
            Orchestrator(
                kind: InstallKind.AppImage,
                installers: [new RecordingInstaller(relaunchHandled: false)]),
            appImagePath: () => image,
            launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, restarted);

        // One detached chain: wait for this process to be gone - it holds the instance lease -
        // then exec the swapped-in image, which wins the arbitration once the old GUI is gone.
        var start = Assert.Single(launcher.Calls);
        Assert.Equal(2, start.ArgumentList.Count);
        Assert.Equal("-c", start.ArgumentList[0]);
        var script = start.ArgumentList[1];
        Assert.Contains($"kill -0 {SuccessorPid}", script);
        Assert.Contains($"exec '{image}'", script);
    }

    [Fact]
    public async Task The_AppImage_Successor_Chain_Escapes_The_Image_Path_It_Execs()
    {
        // $APPIMAGE is a path the user chose. A value with a quote or a dollar sign in it
        // must never be parsed as script by the chain that relaunches the app.
        var image = Path.Combine(_directory, "a b'c$(id)`d.AppImage");
        File.WriteAllText(image, "NEW IMAGE");
        var launcher = new RecordingLauncher();
        var vm = Card(
            null,
            Orchestrator(
                kind: InstallKind.AppImage,
                installers: [new RecordingInstaller(relaunchHandled: false)]),
            appImagePath: () => image,
            launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, restarted);

        // Close, escape and reopen around the embedded quote: the rest stays literal, so the
        // chain execs exactly that path and runs nothing the path happens to contain.
        var expected =
            "'" + Path.GetDirectoryName(image) + "/a b'\\''c$(id)`d.AppImage'";
        var script = Assert.Single(launcher.Calls).ArgumentList[1];
        Assert.Contains($"exec {expected}", script);
        Assert.Contains($"kill -0 {SuccessorPid}", script);
    }

    [Fact]
    public async Task A_Handled_Relaunch_Starts_No_Chain_But_Still_Asks_The_Host_To_Exit()
    {
        var launcher = new RecordingLauncher();
        var vm = Card(
            null,
            Orchestrator(installers: [new RecordingInstaller(relaunchHandled: true)]),
            launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, restarted);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task A_Missing_Image_At_Restart_Keeps_The_App_Running_With_An_Honest_Message()
    {
        var launcher = new RecordingLauncher();
        var vm = Card(
            null,
            Orchestrator(
                kind: InstallKind.AppImage,
                installers: [new RecordingInstaller(relaunchHandled: false)]),
            appImagePath: () => null,
            launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        // The new image is on disk, but nothing will bring it up: this process keeps running
        // the old version from the old inode and says so, instead of exiting into nothing.
        Assert.Equal(0, restarted);
        Assert.Empty(launcher.Calls);
        Assert.True(vm.IsResultError);
        Assert.Contains("手动重新打开", vm.ResultText);
    }

    [Fact]
    public async Task A_Successor_Chain_That_Cannot_Start_Keeps_The_App_Running_With_An_Honest_Message()
    {
        var image = Path.Combine(_directory, "DeziroJoy-0.0.2-x86_64.AppImage");
        File.WriteAllText(image, "NEW IMAGE");
        var launcher = new RecordingLauncher { Next = false };
        var vm = Card(
            null,
            Orchestrator(
                kind: InstallKind.AppImage,
                installers: [new RecordingInstaller(relaunchHandled: false)]),
            appImagePath: () => image,
            launcher: launcher);
        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);
        await vm.DownloadUpdateCommand.ExecuteAsync(null);

        var restarted = 0;
        vm.RestartRequested += (_, _) => restarted++;
        await vm.InstallUpdateCommand.ExecuteAsync(null);

        // The launch was attempted and refused: the image is in place, but exiting into an app
        // that never starts would be worse - this process keeps running the old version.
        Assert.Single(launcher.Calls);
        Assert.Equal(0, restarted);
        Assert.True(vm.IsResultError);
        Assert.Contains("手动重新打开", vm.ResultText);
    }

    [Fact]
    public async Task A_Flatpak_Build_Is_Told_The_Command_Instead_Of_A_Download()
    {
        var settings = new ThemeService(_directory);
        settings.SetAutoDownloadUpdates(true);
        var vm = Card(settings, Orchestrator(kind: InstallKind.Flatpak));

        await vm.CheckForUpdatesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("flatpak update dezirojoy", vm.InstallGuidance);
        Assert.True(vm.HasInstallGuidance);
        Assert.False(vm.CanDownload);
        Assert.Null(vm.AutoDownload);
    }

    [Fact]
    public void The_Auto_Download_Toggle_Is_Off_By_Default_And_Persists_Through_The_Shared_Settings()
    {
        var settings = new ThemeService(_directory);
        settings.Load();
        var vm = Card(settings, Orchestrator());

        Assert.False(vm.AutoDownloadEnabled);

        vm.AutoDownloadEnabled = true;

        var reloaded = new ThemeService(_directory);
        reloaded.Load();
        Assert.True(reloaded.AutoDownloadUpdatesEnabled);
        Assert.True(Card(reloaded, Orchestrator()).AutoDownloadEnabled);
    }

    private static MainWindowViewModel CreateViewModel(ThemeService? settings, IUpdateService? service = null)
    {
        var provider = new FakeGamepadProvider();
        var sink = new RecordingOutputSink();
        var store = new InMemoryProfileStore();
        var host = new EngineHost(provider, sink, store);

        return new MainWindowViewModel(host, store, settings: settings, updateService: service);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        private readonly UpdateCheckResult? _result;

        public FakeUpdateService(UpdateCheckResult result) => _result = result;

        public FakeUpdateService(TaskCompletionSource<UpdateCheckResult> gate)
            => _responder = () => gate.Task;

        public FakeUpdateService(Exception exception) => _responder = () => throw exception;

        private readonly Func<Task<UpdateCheckResult>>? _responder;

        public string CurrentVersionText => "0.0.1-beta.3-preview";

        public int Calls { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return _responder is { } responder ? responder() : Task.FromResult(_result!);
        }
    }

    /// <summary>Serves the package and its sidecar; anything else is a 404.</summary>
    private sealed class StubHandler(Func<string, HttpContent?> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = responder(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>
    /// Serves the sidecar at once and holds the package response open until the caller's token
    /// is cancelled - the shape a slow download has, so the 取消 button has something to stop.
    /// </summary>
    private sealed class GatedHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant() + "\n"),
            };
        }
    }

    /// <summary>A release whose checksum sidecar is missing: the download refuses to trust it.</summary>
    private sealed class MissingSidecarHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class RecordingInstaller(TaskCompletionSource? gate = null, bool relaunchHandled = true)
        : IUpdateInstaller
    {
        public List<string> Calls { get; } = [];

        public bool Supports(InstallKind kind) =>
            kind is InstallKind.Deb or InstallKind.AppImage or InstallKind.WindowsInstall;

        public void Release() => gate!.TrySetResult();

        public async Task<UpdateInstallResult> InstallAsync(
            UpdateInstallRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add(request.PackageName);

            if (gate is not null)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return UpdateInstallResult.Ok(relaunchHandled: relaunchHandled);
        }
    }

    private sealed class RecordingLauncher : IUpdateProcessLauncher
    {
        public List<ProcessStartInfo> Calls { get; } = [];

        /// <summary>What the next Start answers; false models a chain that could not be started.</summary>
        public bool Next { get; set; } = true;

        public bool Start(ProcessStartInfo startInfo)
        {
            Calls.Add(startInfo);
            return Next;
        }
    }
}
