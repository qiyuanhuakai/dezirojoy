using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The update surface: the manual check, the result wording, the persisted startup toggle,
/// and the startup auto-check the main window runs when that toggle is on. The transport is
/// faked at the <see cref="IUpdateService"/> seam, so no test touches the network.
/// </summary>
public sealed class UpdateSettingsViewModelTests : IDisposable
{
    private readonly string _directory;

    public UpdateSettingsViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
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
}
