using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The input-backend half of the settings card. The selection is persisted and only takes
/// effect through a safe daemon restart; the card never claims a switch the daemon has not
/// confirmed, and a restart that fails leaves the old backend running with the reason shown.
/// </summary>
public class DaemonSettingsViewModelBackendTests
{
    [Fact]
    public void The_Persisted_Selection_Is_What_The_Card_Opens_With()
    {
        var store = new InMemoryInputBackendStore();
        store.Save(InputBackend.Sdl3);

        var vm = new DaemonSettingsViewModel(new FakeDaemonService(), backendStore: store);

        Assert.Equal(InputBackend.Sdl3, vm.SelectedBackend);
        Assert.True(vm.IsSdl3BackendSelected);
        Assert.False(vm.IsPlatformBackendSelected);

        // The running backend is the daemon's answer, not the saved selection: with no
        // daemon connected it says so instead of implying the saved one is already live.
        Assert.Contains("未知", vm.RunningBackendText);
    }

    [Fact]
    public void The_Default_Selection_Is_The_Platform_Backend()
    {
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: new InMemoryInputBackendStore());

        Assert.Equal(InputBackend.Platform, vm.SelectedBackend);
        Assert.True(vm.IsPlatformBackendSelected);
        Assert.False(vm.IsSdl3BackendSelected);
    }

    [Fact]
    public void Choosing_A_Radio_Moves_The_Selection()
    {
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: new InMemoryInputBackendStore());

        vm.IsSdl3BackendSelected = true;

        Assert.Equal(InputBackend.Sdl3, vm.SelectedBackend);
        Assert.True(vm.IsSdl3BackendSelected);
        Assert.False(vm.IsPlatformBackendSelected);

        vm.IsPlatformBackendSelected = true;

        Assert.Equal(InputBackend.Platform, vm.SelectedBackend);
    }

    [Fact]
    public async Task Applying_Persists_The_Selection_And_Restarts_The_Daemon()
    {
        var store = new InMemoryInputBackendStore();
        var restarts = 0;
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: store,
            restartDaemon: () =>
            {
                restarts++;
                return Task.FromResult(true);
            });

        vm.SelectedBackend = InputBackend.Sdl3;
        await vm.ApplyBackendCommand.ExecuteAsync(null);

        Assert.Equal(1, restarts);
        Assert.Equal(InputBackend.Sdl3, store.Load());
        Assert.Contains("重启", vm.BackendStatusMessage);
        Assert.True(vm.HasBackendStatusMessage);
    }

    [Fact]
    public async Task Applying_Without_A_Daemon_To_Restart_Says_So_Instead_Of_Implying_It()
    {
        var store = new InMemoryInputBackendStore();
        var vm = new DaemonSettingsViewModel(new FakeDaemonService(), backendStore: store);

        vm.SelectedBackend = InputBackend.Sdl3;
        await vm.ApplyBackendCommand.ExecuteAsync(null);

        Assert.Equal(InputBackend.Sdl3, store.Load());
        Assert.Contains("重启常驻服务后生效", vm.BackendStatusMessage);
    }

    [Fact]
    public async Task A_Failed_Restart_Reports_It_And_Keeps_The_Old_Backend_Running()
    {
        var store = new InMemoryInputBackendStore();
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: store,
            restartDaemon: () => Task.FromResult(false));

        vm.SelectedBackend = InputBackend.Sdl3;
        await vm.ApplyBackendCommand.ExecuteAsync(null);

        Assert.Contains("手动重启", vm.BackendStatusMessage);
        Assert.Contains("不会静默切换", vm.BackendStatusMessage);
    }

    [Fact]
    public async Task A_Restart_That_Throws_Leaves_The_Card_Usable()
    {
        var store = new InMemoryInputBackendStore();
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: store,
            restartDaemon: () => throw new InvalidOperationException("boom"));

        vm.SelectedBackend = InputBackend.Sdl3;
        await vm.ApplyBackendCommand.ExecuteAsync(null);

        Assert.Contains("boom", vm.BackendStatusMessage);
        Assert.Equal(InputBackend.Sdl3, store.Load());
    }

    [Fact]
    public void The_Running_Backend_The_Daemon_Reports_Is_Shown_As_Its_Own_Fact()
    {
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: new InMemoryInputBackendStore());

        // Before any snapshot: unknown, not claimed.
        Assert.Contains("未知", vm.RunningBackendText);
        Assert.False(vm.HasRunningBackendError);
        Assert.False(vm.HasBackendMismatch);

        vm.ReportRunningBackend(InputBackend.Platform, null);

        Assert.Contains("平台默认", vm.RunningBackendText);
        Assert.False(vm.HasBackendMismatch);

        vm.ReportRunningBackend(InputBackend.Sdl3, "SDL3 输入后端初始化失败：libSDL3.so 缺失");

        Assert.Contains("SDL3", vm.RunningBackendText);
        Assert.Contains("libSDL3.so", vm.RunningBackendErrorText);
        Assert.Contains("未切换回其他后端", vm.RunningBackendErrorText);
        Assert.True(vm.HasRunningBackendError);
        Assert.True(vm.HasBackendMismatch);
        Assert.Contains("重启常驻服务后生效", vm.BackendMismatchText);
    }
}
