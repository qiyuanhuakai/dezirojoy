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
        };

        var vm = new DaemonSettingsViewModel(service);

        Assert.True(vm.IsSupported);
        Assert.True(vm.IsElevated);
        Assert.True(vm.IsDaemonInstalled);
        Assert.True(vm.AutostartEnabled);
        Assert.Contains("已安装", vm.DaemonStateText);
        Assert.Contains("管理员", vm.ElevationText);
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
}
