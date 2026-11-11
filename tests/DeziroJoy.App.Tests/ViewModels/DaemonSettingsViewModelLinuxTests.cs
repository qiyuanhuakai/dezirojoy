using DeziroJoy.App.Services;
using DeziroJoy.App.ViewModels;
using DeziroJoy.Platform.Linux;
using Xunit;

namespace DeziroJoy.App.Tests.ViewModels;

/// <summary>
/// The settings card on the same-user platform. Linux gets an operable resident-service card
/// (the systemd unit) and an injection explanation instead of "not applicable", while the
/// Windows wording stays exactly where it was - the two paths share one view model and must
/// not drift into each other.
/// </summary>
public class DaemonSettingsViewModelLinuxTests
{
    private static DaemonSettingsViewModel LinuxCard(FakeLinuxDaemonService service)
        => new(service);

    [Fact]
    public void The_Card_Is_Supported_And_Operable_On_Linux()
    {
        var vm = LinuxCard(new FakeLinuxDaemonService());

        Assert.True(vm.IsSupported);
        Assert.True(vm.IsSameUserModel);
        Assert.Contains("未安装", vm.DaemonStateText);

        vm.InstallDaemonCommand.Execute(null);

        Assert.True(vm.IsDaemonInstalled);
        Assert.Contains("已安装", vm.StatusMessage);
        Assert.Contains("loginctl enable-linger", vm.StatusMessage);
    }

    [Fact]
    public void The_State_Text_Separates_Installed_From_Running()
    {
        var service = new FakeLinuxDaemonService { IsDaemonInstalled = true, IsDaemonRunning = false };

        Assert.Contains("已安装但未运行", LinuxCard(service).DaemonStateText);

        service.IsDaemonRunning = true;

        Assert.Contains("已安装并正在运行", LinuxCard(service).DaemonStateText);
    }

    [Fact]
    public void The_Elevation_Text_Explains_The_Same_User_Design_And_The_Device_Permissions()
    {
        var vm = LinuxCard(new FakeLinuxDaemonService());

        var text = vm.ElevationText;

        Assert.Contains("同用户", text);
        Assert.Contains("uinput", text);
        Assert.Contains("UIPI", text);
        Assert.Contains("无需提权", text);

        // The permission fix is the shared wording, not a second copy that can drift.
        Assert.Contains(LinuxDeviceAccess.InputGroupCommand, text);
        Assert.Contains(LinuxDeviceAccess.UdevRulesPath, text);
    }

    [Fact]
    public void Uninstall_Reports_The_Unit_Removal()
    {
        var service = new FakeLinuxDaemonService { IsDaemonInstalled = true, IsDaemonRunning = true };
        var vm = LinuxCard(service);

        vm.UninstallDaemonCommand.Execute(null);

        Assert.False(vm.IsDaemonInstalled);
        Assert.Contains("已停止并移除", vm.StatusMessage);
    }

    [Fact]
    public void The_Autostart_Toggle_Drives_The_Service()
    {
        var service = new FakeLinuxDaemonService();
        var vm = LinuxCard(service);

        vm.AutostartEnabled = true;

        Assert.Equal([true], service.AutostartRequests);
        Assert.Contains("登录自启动", vm.StatusMessage);
    }

    [Fact]
    public void Refresh_Republishes_Running_State_And_Support()
    {
        var service = new FakeLinuxDaemonService { IsDaemonInstalled = true };
        var vm = LinuxCard(service);

        service.IsDaemonRunning = true;
        vm.RefreshCommand.Execute(null);

        Assert.True(vm.IsDaemonRunning);
    }

    /// <summary>
    /// In-memory same-user service: mutates on a completed operation and records autostart
    /// requests, so the card's Linux wording is pinned without a systemd instance.
    /// </summary>
    private sealed class FakeLinuxDaemonService : IDaemonService
    {
        public bool IsSupported => true;

        public bool IsSameUserModel => true;

        public bool IsElevated => false;

        public bool IsDaemonInstalled { get; set; }

        public bool IsDaemonRunning { get; set; }

        public bool IsAutostartInstalled { get; set; }

        public string? DaemonRunLevel => null;

        public List<bool> AutostartRequests { get; } = [];

        public void Refresh()
        {
        }

        public DaemonActionResult InstallDaemon()
        {
            IsDaemonInstalled = true;
            IsDaemonRunning = true;

            // Installing the unit deliberately does not install the XDG autostart entry:
            // the two are independent switches, exactly like the real service.
            return DaemonActionResult.Completed(
                $"已安装并启动 systemd 用户单元，登录时将自动运行。若需退出登录后继续常驻，请执行 {LinuxDeviceAccess.LingerCommand}。");
        }

        public DaemonActionResult UninstallDaemon()
        {
            IsDaemonInstalled = false;
            IsDaemonRunning = false;

            return DaemonActionResult.Completed("已停止并移除 systemd 用户单元 dezirojoy-daemon.service。");
        }

        public DaemonActionResult SetAutostart(bool enabled)
        {
            AutostartRequests.Add(enabled);
            IsAutostartInstalled = enabled;

            return DaemonActionResult.Completed(enabled ? "已启用登录自启动" : "已关闭登录自启动");
        }
    }
}
