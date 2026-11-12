using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The update state machine. Everything around the install is a seam - the check, the
/// download, the installers, the daemon stop and the tray exit - so what is under test is
/// the order those steps run in and the honest failure of every step that cannot complete.
/// </summary>
public sealed class UpdateOrchestratorTests : IDisposable
{
    private const string PackageUrl = "https://example.invalid/DeziroJoy_0.0.2_amd64.deb";

    private const string SidecarUrl = "https://example.invalid/DeziroJoy_0.0.2_amd64.deb.sha256";

    private const string PackageName = "DeziroJoy_0.0.2_amd64.deb";

    private readonly string _cache;

    private readonly List<string> _log = [];

    private readonly RecordingInstaller _installer;

    public UpdateOrchestratorTests()
    {
        _cache = Path.Combine(Path.GetTempPath(), $"dezirojoy-orchestrator-test-{Guid.NewGuid():N}");
        _installer = new RecordingInstaller(() => _log.Add("install"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private static ReleaseInfo Release()
        => new()
        {
            TagName = "v0.0.2",
            Version = SemanticVersion.Parse("0.0.2"),
            PageUrl = "https://example.invalid/release",
            Assets =
            [
                new ReleaseAsset { Name = PackageName, DownloadUrl = PackageUrl, Size = 16 },
                new ReleaseAsset { Name = PackageName + ".sha256", DownloadUrl = SidecarUrl, Size = 64 },
            ],
        };

    private static string ChecksumFor(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private UpdateOrchestrator Orchestrator(
        IUpdateService? service = null,
        InstallKind? kind = null,
        IReadOnlyList<IUpdateInstaller>? installers = null,
        bool daemonStops = true,
        bool trayExits = true,
        HttpMessageHandler? handler = null,
        Func<CancellationToken, Task<bool>>? stopDaemon = null,
        Func<CancellationToken, Task<bool>>? askTrayExit = null)
    {
        var payload = Encoding.UTF8.GetBytes("the deb payload");
        handler ??= new StubHandler(url => url switch
        {
            PackageUrl => new StringContent(Encoding.UTF8.GetString(payload)),
            SidecarUrl => new StringContent($"{ChecksumFor(payload)}\n"),
            _ => (HttpContent?)null,
        });

        return new UpdateOrchestrator(
            service ?? new FakeUpdateService(UpdateCheckResult.Available(Release())),
            new UpdateDownloader(handler, TimeSpan.FromSeconds(10), () => _cache),
            installers ?? [_installer],
            detectInstall: () => new InstallKindInfo(kind ?? InstallKind.Deb, "linux-x64"),
            stopDaemon: stopDaemon ?? (_ =>
            {
                _log.Add("daemon");
                return Task.FromResult(daemonStops);
            }),
            askTrayExit: askTrayExit ?? (_ =>
            {
                _log.Add("tray");
                return Task.FromResult(trayExits);
            }));
    }

    [Fact]
    public async Task A_Check_Moves_The_Flow_To_Available_And_Keeps_The_Assets()
    {
        var orchestrator = Orchestrator();

        var result = await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(UpdatePhase.Available, orchestrator.Phase);
        Assert.Equal("v0.0.2", orchestrator.Latest!.TagName);
        Assert.Contains(orchestrator.Latest!.Assets, asset => asset.Name == PackageName);
        Assert.Contains("0.0.2", orchestrator.StatusText);
    }

    [Fact]
    public async Task An_Up_To_Date_Check_Reports_Up_To_Date_And_Clears_The_Release()
    {
        var orchestrator = Orchestrator(
            new FakeUpdateService(UpdateCheckResult.UpToDate()));

        var result = await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(UpdatePhase.UpToDate, orchestrator.Phase);
        Assert.Null(orchestrator.Latest);
    }

    [Fact]
    public async Task A_Failed_Check_Keeps_The_Reason()
    {
        var orchestrator = Orchestrator(
            new FakeUpdateService(UpdateCheckResult.Failed("检查更新失败：无法连接到 GitHub，请检查网络连接。")));

        var result = await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.Contains("无法连接", orchestrator.ErrorMessage);
    }

    [Fact]
    public async Task A_Download_Verifies_The_Package_And_Reaches_Ready_To_Install()
    {
        var orchestrator = Orchestrator();
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        var progress = new List<double>();
        var downloaded = await orchestrator.DownloadAsync(
            new SyncProgress(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.True(downloaded, orchestrator.ErrorMessage);
        Assert.Equal(UpdatePhase.ReadyToInstall, orchestrator.Phase);
        Assert.NotNull(orchestrator.PackagePath);
        Assert.True(File.Exists(orchestrator.PackagePath));
        Assert.Equal(1d, orchestrator.DownloadProgress);
        Assert.Equal(1d, progress[^1]);
    }

    [Fact]
    public async Task A_Download_Without_A_Check_Is_Refused()
    {
        var orchestrator = Orchestrator();

        var downloaded = await orchestrator.DownloadAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(downloaded);
        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.Contains("尚未发现可用的新版本", orchestrator.ErrorMessage);
    }

    [Fact]
    public async Task A_Form_Without_A_Package_Never_Downloads_And_Says_What_To_Do()
    {
        var orchestrator = Orchestrator(kind: InstallKind.Flatpak);
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        var downloaded = await orchestrator.DownloadAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(downloaded);
        Assert.Contains("flatpak update", orchestrator.ErrorMessage);
        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
    }

    [Fact]
    public async Task A_Release_Without_A_Package_For_This_Machine_Is_Refused()
    {
        var release = new ReleaseInfo
        {
            TagName = "v0.0.2",
            Version = SemanticVersion.Parse("0.0.2"),
            PageUrl = "https://example.invalid/release",
            Assets = [new ReleaseAsset { Name = "DeziroJoy_0.0.2_arm64.deb", DownloadUrl = "https://example.invalid/arm64" }],
        };
        var orchestrator = Orchestrator(new FakeUpdateService(UpdateCheckResult.Available(release)));
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        var downloaded = await orchestrator.DownloadAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(downloaded);
        Assert.Contains("未提供适用于本机的更新包", orchestrator.ErrorMessage);
    }

    [Fact]
    public async Task An_Install_Stops_The_Daemon_Then_The_Tray_Then_Installs()
    {
        var orchestrator = Orchestrator();
        var restarted = false;
        orchestrator.RestartRequested += () => restarted = true;
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.True(installed, orchestrator.ErrorMessage);
        Assert.Equal(["daemon", "tray", "install"], _log);
        Assert.Equal(UpdatePhase.Restarting, orchestrator.Phase);
        Assert.True(restarted, "the host must be told the app is about to be replaced");
        Assert.True(_installer.Handled, "the installer ran");
    }

    [Fact]
    public async Task The_Install_Request_Carries_The_Form_The_Version_And_The_Verified_Package()
    {
        var orchestrator = Orchestrator();
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        var request = Assert.IsType<UpdateInstallRequest>(_installer.LastRequest);
        Assert.Equal(InstallKind.Deb, request.Kind);
        Assert.Equal("linux-x64", request.Rid);
        Assert.Equal(SemanticVersion.Parse("0.0.2"), request.Version);
        Assert.Equal("v0.0.2", request.TagName);
        Assert.Equal(PackageName, request.PackageName);
        Assert.True(File.Exists(request.PackagePath));
    }

    [Fact]
    public async Task An_Install_Without_A_Verified_Package_Is_Refused()
    {
        var orchestrator = Orchestrator();
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(installed);
        Assert.Contains("没有已验证的更新包", orchestrator.ErrorMessage);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task A_Form_No_Installer_Handles_Stops_Nothing()
    {
        // The capability gate runs before the daemon, so a machine that cannot be updated in
        // place keeps its daemon and its tray instead of losing both to a failed install.
        var orchestrator = Orchestrator(installers: []);
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(installed);
        Assert.Empty(_log);
        Assert.Contains("发布页", orchestrator.ErrorMessage);
        Assert.False(_installer.Handled);
    }

    [Fact]
    public async Task A_Daemon_That_Will_Not_Stop_Cancels_The_Install_Before_Anything_Else()
    {
        var orchestrator = Orchestrator(daemonStops: false);
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(installed);
        Assert.Equal(["daemon"], _log);
        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.Contains("无法停止守护进程", orchestrator.ErrorMessage);
        Assert.Contains("映射功能不受影响", orchestrator.ErrorMessage);
        Assert.False(_installer.Handled);
    }

    [Fact]
    public async Task A_Tray_That_Will_Not_Exit_Cancels_The_Install_And_Says_So()
    {
        var orchestrator = Orchestrator(trayExits: false);
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(installed);
        Assert.Equal(["daemon", "tray"], _log);
        Assert.Contains("托盘", orchestrator.ErrorMessage);
        Assert.Contains("守护进程已停止", orchestrator.ErrorMessage);
        Assert.False(_installer.Handled);
    }

    [Fact]
    public async Task A_Failing_Installer_Is_Reported_Honestly()
    {
        var orchestrator = Orchestrator();
        _installer.Outcome = UpdateInstallResult.Failed("安装失败：pkexec 不可用。");
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(installed);
        Assert.Equal(["daemon", "tray", "install"], _log);
        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.Equal("安装失败：pkexec 不可用。", orchestrator.ErrorMessage);
    }

    [Fact]
    public async Task A_Second_Check_While_One_Is_Running_Is_Refused()
    {
        var gate = new TaskCompletionSource<UpdateCheckResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = Orchestrator(new FakeUpdateService(gate.Task));

        var first = orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        var second = await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.Failed, second.Status);
        Assert.Contains("正在处理更新", second.ErrorMessage);
        Assert.True(orchestrator.IsBusy);

        gate.SetResult(UpdateCheckResult.Available(Release()));
        await first;

        Assert.Equal(UpdatePhase.Available, orchestrator.Phase);
        Assert.False(orchestrator.IsBusy);
    }

    [Fact]
    public async Task A_Second_Install_While_One_Is_Running_Is_Refused_Without_Touching_State()
    {
        // The stop sequence must run once: a double-clicked install button that stopped the
        // daemon, the tray and the installer twice is exactly what a real installer cannot
        // survive - and the refusal must not overwrite the phase of the install under way.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = Orchestrator(installers: [new RecordingInstaller(() => _log.Add("install"), gate.Task)]);
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var first = orchestrator.InstallAsync(TestContext.Current.CancellationToken);
        var second = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(second);
        Assert.Equal(UpdatePhase.Installing, orchestrator.Phase);
        Assert.True(orchestrator.IsBusy);

        gate.SetResult();
        Assert.True(await first, orchestrator.ErrorMessage);

        Assert.Equal(["daemon", "tray", "install"], _log);
        Assert.Equal(UpdatePhase.Restarting, orchestrator.Phase);
    }

    [Fact]
    public async Task A_Download_Started_Twice_Does_Not_Overwrite_The_First_One()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = Orchestrator(handler: new GatedHandler(gate.Task));
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        var first = orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var second = await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(second);
        Assert.Equal(UpdatePhase.Downloading, orchestrator.Phase);
        Assert.True(orchestrator.IsBusy);

        gate.SetResult();
        Assert.True(await first, orchestrator.ErrorMessage);
        Assert.Equal(UpdatePhase.ReadyToInstall, orchestrator.Phase);
    }

    [Fact]
    public async Task A_Cancelled_Check_Leaves_The_Flow_Not_Busy()
    {
        var orchestrator = Orchestrator(
            new FakeUpdateService(
                Task.FromException<UpdateCheckResult>(new OperationCanceledException())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.False(orchestrator.IsBusy, "a cancelled check must not refuse every later one");
        Assert.Contains("已取消", orchestrator.ErrorMessage);
    }

    [Fact]
    public async Task A_Cancelled_Download_Leaves_The_Flow_Not_Busy()
    {
        using var cancellation = new CancellationTokenSource();
        var orchestrator = Orchestrator(handler: new CancellingHandler(cancellation.Token));
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);

        var download = orchestrator.DownloadAsync(cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.False(orchestrator.IsBusy);
        Assert.Contains("已取消", orchestrator.ErrorMessage);
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task A_Cancelled_Install_Leaves_The_Flow_Not_Busy()
    {
        var orchestrator = Orchestrator(
            stopDaemon: _ => throw new OperationCanceledException());
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.InstallAsync(TestContext.Current.CancellationToken));

        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.False(orchestrator.IsBusy);
        Assert.Contains("已取消", orchestrator.ErrorMessage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_Relaunch_Flag_Is_Set_Before_The_Restart_Is_Announced(bool relaunchHandled)
    {
        // The host cannot ask the installer afterwards, so the flag has to be readable at the
        // moment the restart is announced - that is when it decides whether to relaunch.
        _installer.Outcome = UpdateInstallResult.Ok(relaunchHandled);
        var orchestrator = Orchestrator();
        var seen = false;
        orchestrator.RestartRequested += () => seen = orchestrator.RelaunchHandled;
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await orchestrator.InstallAsync(TestContext.Current.CancellationToken));

        Assert.Equal(relaunchHandled, orchestrator.RelaunchHandled);
        Assert.Equal(relaunchHandled, seen);
    }

    [Fact]
    public async Task An_Unexpected_Failure_Is_Reported_Instead_Of_Wedging_The_Flow()
    {
        // An exception nobody classified must not reach the host with the phase stuck at
        // Installing: it becomes a presentable failure, and the flow is usable again.
        var orchestrator = Orchestrator(
            stopDaemon: _ => throw new InvalidOperationException("the seam exploded"));
        await orchestrator.CheckAsync(TestContext.Current.CancellationToken);
        await orchestrator.DownloadAsync(cancellationToken: TestContext.Current.CancellationToken);

        var installed = await orchestrator.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(installed);
        Assert.Equal(UpdatePhase.Failed, orchestrator.Phase);
        Assert.False(orchestrator.IsBusy, "an unclassified failure must not lock the flow");
        Assert.Contains("意外错误", orchestrator.ErrorMessage);
        Assert.Contains("the seam exploded", orchestrator.ErrorMessage);
    }

    [Fact]
    public async Task An_Injected_Dependency_Is_Not_Disposed_With_The_Orchestrator()
    {
        // A caller that shares one UpdateService between the existing check surface and the
        // orchestrator must not have its HttpClient killed by disposing the orchestrator.
        var service = new TrackingUpdateService(UpdateCheckResult.Available(Release()));
        var payload = Encoding.UTF8.GetBytes("the deb payload");
        var downloader = new UpdateDownloader(
            new StubHandler(url => url switch
            {
                PackageUrl => new StringContent(Encoding.UTF8.GetString(payload)),
                SidecarUrl => new StringContent($"{ChecksumFor(payload)}\n"),
                _ => (HttpContent?)null,
            }),
            TimeSpan.FromSeconds(10),
            () => _cache);

        var orchestrator = new UpdateOrchestrator(
            service,
            downloader,
            [_installer],
            detectInstall: () => new InstallKindInfo(InstallKind.Deb, "linux-x64"));
        orchestrator.Dispose();

        Assert.False(service.Disposed);

        // The injected downloader still works: a disposed one would throw instead.
        var download = await downloader.DownloadAsync(
            new ReleaseAsset { Name = PackageName, DownloadUrl = PackageUrl, Size = payload.Length },
            new ReleaseAsset { Name = PackageName + ".sha256", DownloadUrl = SidecarUrl },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(download.Succeeded, download.ErrorMessage);
    }

    [Fact]
    public async Task The_Placeholder_Installer_Supports_Nothing_And_Says_Why()
    {
        var installer = new PendingPlatformUpdateInstaller();

        Assert.False(installer.Supports(InstallKind.WindowsInstall));
        Assert.False(installer.Supports(InstallKind.AppImage));

        var outcome = await installer.InstallAsync(
            new UpdateInstallRequest(
                InstallKind.Deb,
                "linux-x64",
                SemanticVersion.Parse("0.0.2"),
                "v0.0.2",
                "/tmp/DeziroJoy_0.0.2_amd64.deb",
                PackageName),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Contains("平台安装器尚未实现", outcome.ErrorMessage);
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        private readonly UpdateCheckResult? _result;

        private readonly Func<Task<UpdateCheckResult>>? _responder;

        public FakeUpdateService(UpdateCheckResult result) => _result = result;

        public FakeUpdateService(Task<UpdateCheckResult> responder) => _responder = () => responder;

        public string CurrentVersionText => "0.0.1-beta.3-preview";

        public int Calls { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return _responder is { } respond ? respond() : Task.FromResult(_result!);
        }
    }

    private sealed class RecordingInstaller : IUpdateInstaller
    {
        private readonly Action? _onInstall;

        private readonly Task? _gate;

        public RecordingInstaller(Action? onInstall = null, Task? gate = null)
        {
            _onInstall = onInstall;
            _gate = gate;
        }

        public bool Handled { get; private set; }

        public UpdateInstallRequest? LastRequest { get; private set; }

        public UpdateInstallResult Outcome { get; set; } = UpdateInstallResult.Ok();

        public bool Supports(InstallKind kind) => true;

        public async Task<UpdateInstallResult> InstallAsync(
            UpdateInstallRequest request,
            CancellationToken cancellationToken)
        {
            _onInstall?.Invoke();
            if (_gate is { } gate)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            Handled = true;
            LastRequest = request;
            return Outcome;
        }
    }

    private sealed class TrackingUpdateService(UpdateCheckResult result) : IUpdateService, IDisposable
    {
        public bool Disposed { get; private set; }

        public string CurrentVersionText => "0.0.1-beta.3-preview";

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Serves the checksum sidecar at once and holds the package back until the gate opens, so
    /// a download can be observed while it is still running.
    /// </summary>
    private sealed class GatedHandler(Task gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == SidecarUrl)
            {
                return Ok(new StringContent($"{ChecksumFor(Encoding.UTF8.GetBytes("the deb payload"))}\n"));
            }

            await gate.WaitAsync(cancellationToken);
            return Ok(new StringContent("the deb payload"));
        }
    }

    /// <summary>
    /// Serves the checksum sidecar at once and then waits on the caller's token, so a
    /// cancellation lands in the middle of a download exactly as a real network drop would.
    /// </summary>
    private sealed class CancellingHandler(CancellationToken token) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == SidecarUrl)
            {
                return Ok(new StringContent($"{ChecksumFor(Encoding.UTF8.GetBytes("the deb payload"))}\n"));
            }

            await Task.Delay(System.Threading.Timeout.Infinite, token);
            throw new InvalidOperationException("the cancellation should have ended this first");
        }
    }

    private static HttpResponseMessage Ok(HttpContent content)
        => new(HttpStatusCode.OK) { Content = content };

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class StubHandler(Func<string, HttpContent?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = respond(request.RequestUri!.ToString());
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
