using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The input-backend half of the settings card. SDL3 is the default since S3 and the legacy
/// XInput/evdev backend is the explicit fallback; the selection is persisted and only takes
/// effect through a safe daemon restart; the card never claims a switch the daemon has not
/// confirmed, and a restart that fails leaves the old backend running with the reason shown.
/// </summary>
public class DaemonSettingsViewModelBackendTests
{
    [Fact]
    public void The_Persisted_Selection_Is_What_The_Card_Opens_With()
    {
        var store = new InMemoryInputBackendStore();
        store.Save(InputBackend.Legacy);

        var vm = new DaemonSettingsViewModel(new FakeDaemonService(), backendStore: store);

        Assert.Equal(InputBackend.Legacy, vm.SelectedBackend);
        Assert.True(vm.IsLegacyBackendSelected);
        Assert.False(vm.IsSdl3BackendSelected);

        // The running backend is the daemon's answer, not the saved selection: with no
        // daemon connected it says so instead of implying the saved one is already live.
        Assert.Contains("未知", vm.RunningBackendText);
    }

    [Fact]
    public void The_Default_Selection_Is_Sdl3()
    {
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: new InMemoryInputBackendStore());

        Assert.Equal(InputBackend.Sdl3, vm.SelectedBackend);
        Assert.True(vm.IsSdl3BackendSelected);
        Assert.False(vm.IsLegacyBackendSelected);
    }

    [Fact]
    public void Choosing_A_Radio_Moves_The_Selection()
    {
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: new InMemoryInputBackendStore());

        vm.IsLegacyBackendSelected = true;

        Assert.Equal(InputBackend.Legacy, vm.SelectedBackend);
        Assert.True(vm.IsLegacyBackendSelected);
        Assert.False(vm.IsSdl3BackendSelected);

        vm.IsSdl3BackendSelected = true;

        Assert.Equal(InputBackend.Sdl3, vm.SelectedBackend);
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
    public async Task Falling_Back_To_Legacy_Persists_The_Fallback_And_Restarts()
    {
        // The rollback path an S3 default needs: choosing the legacy fallback persists it
        // under its own name and asks for the same safe restart, so a machine whose SDL3
        // runtime will not come up has a way back that the daemon reads at startup.
        var store = new InMemoryInputBackendStore();
        store.Save(InputBackend.Sdl3);
        var restarts = 0;
        var vm = new DaemonSettingsViewModel(
            new FakeDaemonService(),
            backendStore: store,
            restartDaemon: () =>
            {
                restarts++;
                return Task.FromResult(true);
            });

        vm.IsLegacyBackendSelected = true;
        await vm.ApplyBackendCommand.ExecuteAsync(null);

        Assert.Equal(1, restarts);
        Assert.Equal(InputBackend.Legacy, store.Load());
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

        // The daemon runs the legacy fallback the SDL3 default does not name: the two
        // differ, and the card says the restart is what applies the change.
        vm.ReportRunningBackend(InputBackend.Legacy, null);

        Assert.Contains("旧后端回退", vm.RunningBackendText);
        Assert.True(vm.HasBackendMismatch);
        Assert.Contains("重启常驻服务后生效", vm.BackendMismatchText);

        // The daemon restarts onto SDL3: the mismatch clears.
        vm.ReportRunningBackend(InputBackend.Sdl3, null);

        Assert.Contains("SDL3", vm.RunningBackendText);
        Assert.False(vm.HasBackendMismatch);

        // A start that failed on SDL3 is reported beside the running backend - never
        // silently swapped for the legacy one.
        vm.ReportRunningBackend(InputBackend.Sdl3, "SDL3 输入后端初始化失败：libSDL3.so 缺失");

        Assert.Contains("libSDL3.so", vm.RunningBackendErrorText);
        Assert.Contains("未切换回其他后端", vm.RunningBackendErrorText);
        Assert.True(vm.HasRunningBackendError);
    }

    [Fact]
    public void A_Damaged_File_Opens_On_The_Default_With_The_Reason_On_The_Status_Line()
    {
        // The upgrade-with-damage case: the selection is lost, the default runs, and the
        // card says why on its own line instead of silently showing a selection nobody chose.
        var path = Path.Combine(Path.GetTempPath(), $"dezirojoy-backend-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "this is not json");

            var vm = new DaemonSettingsViewModel(
                new FakeDaemonService(),
                backendStore: new InputBackendStore(path));

            Assert.Equal(InputBackend.Sdl3, vm.SelectedBackend);
            Assert.True(vm.IsSdl3BackendSelected);
            Assert.Contains("无法解析", vm.BackendStatusMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
