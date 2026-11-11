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
[Collection("DeziroJoy.LinuxAutostart")]
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

        // Installing enables the unit, and that shows up as its own state - never folded
        // into "running", and never moving the GUI autostart toggle.
        Assert.True(vm.IsDaemonEnabled);
        Assert.Equal("已启用（登录自动）", vm.DaemonEnabledText);
        Assert.False(vm.AutostartEnabled);
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
    public void The_Enablement_Is_Its_Own_State_Not_The_Run_State()
    {
        var service = new FakeLinuxDaemonService
        {
            IsDaemonInstalled = true,
            IsDaemonRunning = false,
            IsDaemonEnabled = true,
        };

        // Enabled but not running: the unit starts at login even though it is stopped now.
        Assert.Contains("已安装但未运行", LinuxCard(service).DaemonStateText);
        Assert.Equal("已启用（登录自动）", LinuxCard(service).DaemonEnabledText);

        service.IsDaemonRunning = true;
        service.IsDaemonEnabled = false;

        // Running but not enabled: it runs now and will not come back at the next login.
        Assert.Contains("已安装并正在运行", LinuxCard(service).DaemonStateText);
        Assert.Equal("未启用（登录不会自动启动）", LinuxCard(service).DaemonEnabledText);
    }

    [Fact]
    public void Without_The_Unit_There_Is_No_Enablement_Line()
    {
        var vm = LinuxCard(new FakeLinuxDaemonService());

        Assert.Null(vm.DaemonEnabledText);
        Assert.False(vm.HasDaemonEnabledText);
    }

    [Fact]
    public void A_Failed_Query_Is_Its_Own_State_Never_Not_Running()
    {
        var service = new FakeLinuxDaemonService
        {
            IsDaemonInstalled = true,
            IsDaemonRunning = false,
            IsDaemonEnabled = false,
            DaemonQueryError = "环境缺少 XDG_RUNTIME_DIR 与 DBUS_SESSION_BUS_ADDRESS，systemctl --user 连不上用户总线",
        };

        var text = LinuxCard(service).DaemonStateText;

        // The scheduler never answered, so the card must not claim it did: no "未运行", the
        // reason is on screen, and no enablement line is invented from the failed query.
        Assert.Contains("无法查询状态", text);
        Assert.Contains("XDG_RUNTIME_DIR", text);
        Assert.DoesNotContain("未运行", text);
        Assert.Null(LinuxCard(service).DaemonEnabledText);
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

    [Fact]
    public void Refresh_Republishes_The_Enablement_State()
    {
        var service = new FakeLinuxDaemonService { IsDaemonInstalled = true };
        var vm = LinuxCard(service);

        service.IsDaemonEnabled = true;
        vm.RefreshCommand.Execute(null);

        Assert.True(vm.IsDaemonEnabled);
        Assert.Equal("已启用（登录自动）", vm.DaemonEnabledText);
    }

    [Fact]
    public void Start_And_Stop_Drive_The_Service_And_The_Running_State()
    {
        var service = new FakeLinuxDaemonService { IsDaemonInstalled = true, IsDaemonRunning = false };
        var vm = LinuxCard(service);

        Assert.True(vm.CanStartDaemon);
        Assert.False(vm.CanStopDaemon);

        vm.StartDaemonCommand.Execute(null);

        Assert.Equal(1, service.StartCalls);
        Assert.True(vm.IsDaemonRunning);
        Assert.True(vm.CanStopDaemon);
        Assert.False(vm.CanStartDaemon);
        Assert.Contains("已启动", vm.StatusMessage);

        vm.StopDaemonCommand.Execute(null);

        Assert.Equal(1, service.StopCalls);
        Assert.False(vm.IsDaemonRunning);
        Assert.True(vm.CanStartDaemon);
        Assert.Contains("已停止", vm.StatusMessage);
    }

    [Fact]
    public void The_Start_Stop_Buttons_Are_Inoperable_Before_The_Install()
    {
        var service = new FakeLinuxDaemonService();
        var vm = LinuxCard(service);

        Assert.False(vm.CanStartDaemon);
        Assert.False(vm.CanStopDaemon);

        // Driven anyway, the service refuses instead of pretending: a unit that was never
        // installed cannot start, and the card reports exactly that.
        vm.StartDaemonCommand.Execute(null);

        Assert.Equal(1, service.StartCalls);
        Assert.Contains("尚未安装", vm.StatusMessage);
        Assert.False(vm.IsDaemonRunning);
    }

    [Fact]
    public void The_Buttons_Drive_Systemctl_Through_The_Real_Service()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd user unit only exists on Linux");

        var systemCtl = new FakeSystemCtl();
        var unitDirectory = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-systemd-" + Guid.NewGuid().ToString("N"));
        var autostartDirectory = Path.Combine(Path.GetTempPath(), "dezirojoy-vm-autostart-" + Guid.NewGuid().ToString("N"));
        AutostartInstaller.LinuxAutostartDirectory = () => autostartDirectory;

        try
        {
            var service = new LinuxDaemonService(
                systemCtl,
                () => unitDirectory,
                () => "/opt/dezirojoy/DeziroJoy",
                _ => "/run/user/1000");
            var vm = new DaemonSettingsViewModel(service);

            vm.InstallDaemonCommand.Execute(null);
            Assert.True(vm.IsDaemonInstalled);
            Assert.True(vm.IsDaemonRunning);

            vm.StopDaemonCommand.Execute(null);

            Assert.Contains($"stop {LinuxDaemonService.UnitName}", systemCtl.Calls);
            Assert.False(vm.IsDaemonRunning);
            Assert.True(vm.CanStartDaemon);
            Assert.False(vm.CanStopDaemon);

            vm.StartDaemonCommand.Execute(null);

            Assert.Contains($"start {LinuxDaemonService.UnitName}", systemCtl.Calls);
            Assert.True(vm.IsDaemonRunning);
            Assert.True(vm.CanStopDaemon);
            Assert.False(vm.CanStartDaemon);
        }
        finally
        {
            AutostartInstaller.LinuxAutostartDirectory = static () => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "autostart");

            TryDelete(unitDirectory);
            TryDelete(autostartDirectory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Records every systemctl invocation and mirrors real systemd's state: is-active answers
    /// from the recorded state, is-enabled answers from the recorded enablement, start flips
    /// the former on and stop flips it off. The enablement branch exists because the daemon's
    /// own is-enabled is a reported fact - the GUI autostart toggle still reads the XDG entry,
    /// never the unit (the reviewed P1).
    /// </summary>
    private sealed class FakeSystemCtl : ISystemCtl
    {
        public List<string> Calls { get; } = [];

        public bool Active { get; set; } = true;

        public bool Enabled { get; set; } = true;

        public SystemCtlResult Run(string arguments)
        {
            Calls.Add(arguments);

            if (arguments.StartsWith("is-active", StringComparison.Ordinal))
            {
                return new SystemCtlResult(Active ? 0 : 3, Active ? "active" : "inactive", string.Empty);
            }

            if (arguments.StartsWith("is-enabled", StringComparison.Ordinal))
            {
                return new SystemCtlResult(Enabled ? 0 : 1, Enabled ? "enabled" : "disabled", string.Empty);
            }

            if (arguments.StartsWith("start", StringComparison.Ordinal))
            {
                Active = true;
            }
            else if (arguments.StartsWith("stop", StringComparison.Ordinal))
            {
                Active = false;
            }

            return new SystemCtlResult(0, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// In-memory same-user service: mutates on a completed operation and records autostart
    /// requests, so the card's Linux wording is pinned without a systemd instance. Internal so
    /// the GUI tests can render the Linux card over the same double.
    /// </summary>
    internal sealed class FakeLinuxDaemonService : IDaemonService
    {
        public bool IsSupported => true;

        public bool IsSameUserModel => true;

        public bool IsElevated => false;

        public bool IsDaemonInstalled { get; set; }

        public bool IsDaemonRunning { get; set; }

        public bool IsDaemonEnabled { get; set; }

        public string? DaemonQueryError { get; set; }

        public bool IsAutostartInstalled { get; set; }

        public string? DaemonRunLevel => null;

        public List<bool> AutostartRequests { get; } = [];

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public void Refresh()
        {
        }

        public DaemonActionResult InstallDaemon()
        {
            IsDaemonInstalled = true;
            IsDaemonRunning = true;
            IsDaemonEnabled = true;

            // Installing the unit deliberately does not install the XDG autostart entry:
            // the two are independent switches, exactly like the real service.
            return DaemonActionResult.Completed(
                $"已安装并启动 systemd 用户单元，登录时将自动运行。若需退出登录后继续常驻，请执行 {LinuxDeviceAccess.LingerCommand}。");
        }

        public DaemonActionResult UninstallDaemon()
        {
            IsDaemonInstalled = false;
            IsDaemonRunning = false;
            IsDaemonEnabled = false;

            return DaemonActionResult.Completed("已停止并移除 systemd 用户单元 dezirojoy-daemon.service。");
        }

        public DaemonActionResult StartDaemon()
        {
            StartCalls++;

            if (!IsDaemonInstalled)
            {
                return DaemonActionResult.Failed("常驻服务尚未安装，无法启动。");
            }

            IsDaemonRunning = true;

            return DaemonActionResult.Completed("已启动 systemd 用户单元 dezirojoy-daemon.service。");
        }

        public DaemonActionResult StopDaemon()
        {
            StopCalls++;

            if (!IsDaemonInstalled)
            {
                return DaemonActionResult.Failed("常驻服务尚未安装，无法停止。");
            }

            IsDaemonRunning = false;

            return DaemonActionResult.Completed("已停止 systemd 用户单元 dezirojoy-daemon.service，重新启动前不会运行。");
        }

        public DaemonActionResult SetAutostart(bool enabled)
        {
            AutostartRequests.Add(enabled);
            IsAutostartInstalled = enabled;

            return DaemonActionResult.Completed(enabled ? "已启用登录自启动" : "已关闭登录自启动");
        }
    }
}
