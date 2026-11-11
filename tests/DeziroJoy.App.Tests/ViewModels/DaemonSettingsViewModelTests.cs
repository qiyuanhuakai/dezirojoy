using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The daemon/autostart card must publish the real service state and drive the real operations,
/// never a fabricated one. These use <see cref="FakeDaemonService"/> to pin that contract,
/// including the elevated-relaunch (Pending) path where the toggle keeps the requested value.
/// </summary>
public class DaemonSettingsViewModelTests
{
    [Fact]
    public void Initial_State_Reflects_The_Service()
    {
        var service = new FakeDaemonService(isSupported: true, isElevated: true)
        {
            IsDaemonInstalled = true,
            IsAutostartInstalled = true,
            DaemonRunLevel = "Highest",
        };

        var vm = new DaemonSettingsViewModel(service);

        Assert.True(vm.IsSupported);
        Assert.True(vm.IsElevated);
        Assert.True(vm.IsDaemonInstalled);
        Assert.True(vm.AutostartEnabled);
        Assert.Contains("已安装", vm.DaemonStateText);
        Assert.Contains("常驻服务以管理员身份运行", vm.ElevationText);
    }

    [Fact]
    public void InstallDaemon_Updates_State_And_Status()
    {
        var service = new FakeDaemonService();
        var vm = new DaemonSettingsViewModel(service);
        Assert.False(vm.IsDaemonInstalled);

        vm.InstallDaemonCommand.Execute(null);

        Assert.True(vm.IsDaemonInstalled);
        Assert.Contains("已注册", vm.StatusMessage);
        Assert.True(vm.HasStatusMessage);
    }

    [Fact]
    public void UninstallDaemon_Updates_State_And_Status()
    {
        var service = new FakeDaemonService { IsDaemonInstalled = true };
        var vm = new DaemonSettingsViewModel(service);
        Assert.True(vm.IsDaemonInstalled);

        vm.UninstallDaemonCommand.Execute(null);

        Assert.False(vm.IsDaemonInstalled);
        Assert.Contains("已移除", vm.StatusMessage);
    }

    [Fact]
    public void Toggling_Autostart_On_Calls_The_Service_And_Reflects_It()
    {
        var service = new FakeDaemonService();
        var vm = new DaemonSettingsViewModel(service);

        vm.AutostartEnabled = true;

        Assert.True(Assert.Single(service.AutostartRequests));
        Assert.True(vm.AutostartEnabled);
        Assert.Contains("已启用", vm.StatusMessage);
    }

    [Fact]
    public void Toggling_Autostart_Off_Calls_The_Service_And_Reflects_It()
    {
        var service = new FakeDaemonService { IsAutostartInstalled = true };
        var vm = new DaemonSettingsViewModel(service);
        Assert.True(vm.AutostartEnabled);

        vm.AutostartEnabled = false;

        Assert.False(Assert.Single(service.AutostartRequests));
        Assert.False(vm.AutostartEnabled);
    }

    [Fact]
    public void A_Pending_Elevated_Relaunch_Keeps_The_Requested_Toggle_Value()
    {
        var service = new FakeDaemonService { NextResult = DaemonOp.Pending };
        var vm = new DaemonSettingsViewModel(service);

        vm.AutostartEnabled = true;

        // The elevated copy has not finished, so the state is unchanged but the toggle keeps the
        // requested value and the status tells the user to refresh.
        Assert.False(service.IsAutostartInstalled);
        Assert.True(vm.AutostartEnabled);
        Assert.Contains("刷新", vm.StatusMessage);
    }

    [Fact]
    public void Refresh_Republishes_The_Current_Service_State()
    {
        var service = new FakeDaemonService();
        var vm = new DaemonSettingsViewModel(service);
        var callsAfterConstruction = service.RefreshCalls;

        service.IsDaemonInstalled = true;
        vm.RefreshCommand.Execute(null);

        Assert.True(vm.IsDaemonInstalled);
        Assert.True(service.RefreshCalls > callsAfterConstruction);
    }

    [Fact]
    public void An_Unsupported_Platform_Reports_It_And_Disables_Nothing_Silently()
    {
        var vm = new DaemonSettingsViewModel(new FakeDaemonService(isSupported: false));

        Assert.False(vm.IsSupported);
        Assert.Contains("不支持", vm.DaemonStateText);
        Assert.Contains("不适用", vm.ElevationText);

        vm.InstallDaemonCommand.Execute(null);
        Assert.Contains("不支持", vm.StatusMessage);
    }

    [Fact]
    public void An_Unelevated_Gui_With_An_Elevated_Daemon_Still_Reports_Injection_Reach()
    {
        var service = new FakeDaemonService(isSupported: true, isElevated: false)
        {
            IsDaemonInstalled = true,
            DaemonRunLevel = "Highest",
        };

        var vm = new DaemonSettingsViewModel(service);

        Assert.False(vm.IsElevated);
        Assert.Contains("常驻服务以管理员身份运行", vm.ElevationText);
    }

    [Fact]
    public void Installing_The_Daemon_Reports_The_Run_Level_It_Gained()
    {
        var service = new FakeDaemonService();
        var vm = new DaemonSettingsViewModel(service);

        vm.InstallDaemonCommand.Execute(null);

        Assert.Contains("常驻服务以管理员身份运行", vm.ElevationText);
    }

    [Fact]
    public void A_Daemon_Without_The_Highest_Run_Level_Says_Injection_Cannot_Reach()
    {
        var service = new FakeDaemonService { IsDaemonInstalled = true, DaemonRunLevel = "LeastPrivilege" };
        var vm = new DaemonSettingsViewModel(service);

        Assert.Contains("未以管理员身份运行", vm.ElevationText);
    }

    [Fact]
    public void An_Unreadable_Run_Level_Is_Reported_As_Unknown_Never_As_Elevated()
    {
        var service = new FakeDaemonService { IsDaemonInstalled = true, DaemonRunLevel = null };
        var vm = new DaemonSettingsViewModel(service);

        Assert.Contains("无法读取", vm.ElevationText);
        Assert.DoesNotContain("以管理员身份运行", vm.ElevationText);
    }

    [Fact]
    public void No_Daemon_Installed_Says_Injection_Cannot_Reach_Elevated_Targets()
    {
        var vm = new DaemonSettingsViewModel(new FakeDaemonService());

        Assert.Contains("未安装", vm.ElevationText);
    }

    [Fact]
    public void Windows_Hides_The_Standalone_Start_Stop_Controls()
    {
        var service = new FakeDaemonService { IsDaemonInstalled = true };
        var vm = new DaemonSettingsViewModel(service);

        // schtasks has no standalone start or stop: the task either exists and runs at logon
        // or it does not, so the card exposes neither control - and the service says so if
        // driven anyway, instead of pretending it started something.
        Assert.False(vm.IsSameUserModel);
        Assert.False(vm.CanStartDaemon);
        Assert.False(vm.CanStopDaemon);

        vm.StartDaemonCommand.Execute(null);
        Assert.Contains("不支持", vm.StatusMessage);

        vm.StopDaemonCommand.Execute(null);
        Assert.Contains("不支持", vm.StatusMessage);
    }
}
