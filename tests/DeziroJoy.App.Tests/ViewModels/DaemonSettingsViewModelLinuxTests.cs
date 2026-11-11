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
    private static DaemonSettingsViewModel LinuxCard(FakeLinuxDaemonService service, IDeviceAccessFixer? fixer = null)
        => new(service, fixer);

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
    public async Task The_Fix_Button_Runs_The_Fixer_And_Reports_The_Outcome()
    {
        var fixer = new FakeDeviceAccessFixer
        {
            Result = DeviceAccessFixResult.Fixed(
                "已修复设备访问权限：已写入 /etc/udev/rules.d/99-dezirojoy.rules；已将 qiyuaner 加入 input 组（重新登录后生效）。"),
        };
        var vm = LinuxCard(new FakeLinuxDaemonService(), fixer);

        Assert.True(vm.CanFixDeviceAccess);

        await vm.FixDeviceAccessCommand.ExecuteAsync(null);

        Assert.Equal(1, fixer.Calls);
        Assert.Contains("已修复", vm.DeviceAccessFixStatus);
        Assert.True(vm.HasDeviceAccessFixStatus);
        Assert.False(vm.IsFixingDeviceAccess);
        Assert.True(vm.CanFixDeviceAccess);

        // A successful fix leaves nothing to explain, so the tooltip stays the plain notice.
        Assert.Empty(vm.DeviceAccessFixNote);
        Assert.Equal(LinuxDeviceAccess.FixNotice, vm.DeviceAccessFixTooltip);
    }

    [Fact]
    public async Task While_The_Fix_Runs_The_Button_Is_Inoperable_And_The_Status_Says_So()
    {
        var gate = new TaskCompletionSource<DeviceAccessFixResult>();
        var fixer = new FakeDeviceAccessFixer { Pending = gate.Task };
        var vm = LinuxCard(new FakeLinuxDaemonService(), fixer);

        var running = vm.FixDeviceAccessCommand.ExecuteAsync(null);

        // pkexec blocks on the polkit dialog for as long as the user takes to answer it, so
        // the fixing state is observable and the button is disabled against a second run.
        Assert.True(vm.IsFixingDeviceAccess);
        Assert.False(vm.CanFixDeviceAccess);
        Assert.Contains("正在修复", vm.DeviceAccessFixStatus);

        gate.SetResult(DeviceAccessFixResult.Fixed("已修复设备访问权限。"));
        await running;

        Assert.False(vm.IsFixingDeviceAccess);
        Assert.True(vm.CanFixDeviceAccess);
        Assert.Contains("已修复", vm.DeviceAccessFixStatus);
    }

    [Fact]
    public async Task A_Degraded_Fix_Keeps_The_Manual_Commands_And_Explains_Itself_In_The_Tooltip()
    {
        var reason = "未找到 pkexec（当前 PATH 中可能没有它，或未安装 polkit）";
        var fixer = new FakeDeviceAccessFixer
        {
            Result = DeviceAccessFixResult.Degraded(
                reason,
                $"一键修复不可用：{reason}。请手动执行：{LinuxDeviceAccess.DeviceAccessGuidance}"),
        };
        var vm = LinuxCard(new FakeLinuxDaemonService(), fixer);

        await vm.FixDeviceAccessCommand.ExecuteAsync(null);

        // The degradation hands over the same fix as copyable commands - the wording every
        // other Linux surface already quotes - instead of a dead button.
        Assert.Contains(LinuxDeviceAccess.InputGroupCommand, vm.DeviceAccessFixStatus);
        Assert.Contains(LinuxDeviceAccess.UdevRulesPath, vm.DeviceAccessFixStatus);

        // The tooltip states what the fix changes and why the one-click path was not used.
        Assert.Contains(LinuxDeviceAccess.FixNotice, vm.DeviceAccessFixTooltip);
        Assert.Contains("重新登录", vm.DeviceAccessFixTooltip);
        Assert.Contains(reason, vm.DeviceAccessFixTooltip);
    }

    [Fact]
    public async Task A_Failed_Fix_Reports_The_Reason_Too()
    {
        var fixer = new FakeDeviceAccessFixer
        {
            Result = DeviceAccessFixResult.Failed("探测失败", "无法探测设备访问状态：boom"),
        };
        var vm = LinuxCard(new FakeLinuxDaemonService(), fixer);

        await vm.FixDeviceAccessCommand.ExecuteAsync(null);

        Assert.Contains("无法探测设备访问状态：boom", vm.DeviceAccessFixStatus);
        Assert.Contains("探测失败", vm.DeviceAccessFixTooltip);
    }

    [Fact]
    public async Task A_Fix_With_Nothing_To_Do_Says_So_Without_A_Note()
    {
        var fixer = new FakeDeviceAccessFixer
        {
            Result = DeviceAccessFixResult.NoGap("未检测到设备访问缺口：手柄节点可读、/dev/uinput 可写，无需修复。"),
        };
        var vm = LinuxCard(new FakeLinuxDaemonService(), fixer);

        await vm.FixDeviceAccessCommand.ExecuteAsync(null);

        Assert.Contains("无需修复", vm.DeviceAccessFixStatus);
        Assert.Empty(vm.DeviceAccessFixNote);
        Assert.Equal(LinuxDeviceAccess.FixNotice, vm.DeviceAccessFixTooltip);
    }

    [Fact]
    public async Task A_Daemon_Operation_Does_Not_Clobber_The_Fix_Status()
    {
        var fixer = new FakeDeviceAccessFixer
        {
            Result = DeviceAccessFixResult.Fixed("已修复设备访问权限。"),
        };
        var vm = LinuxCard(new FakeLinuxDaemonService { IsDaemonInstalled = true }, fixer);

        await vm.FixDeviceAccessCommand.ExecuteAsync(null);
        vm.StartDaemonCommand.Execute(null);

        // The two actions report on their own lines, so neither erases the other's outcome.
        Assert.Contains("已启动", vm.StatusMessage);
        Assert.Contains("已修复", vm.DeviceAccessFixStatus);
    }

    [Fact]
    public void The_Fix_Is_Not_Offered_Where_There_Is_No_Device_Access_Model()
    {
        // Windows has no evdev/uinput permission model to repair, so the card offers nothing.
        var vm = new DaemonSettingsViewModel(new FakeDaemonService());

        Assert.False(vm.CanFixDeviceAccess);
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

    /// <summary>
    /// In-memory <see cref="IDeviceAccessFixer"/>: records the calls and answers either from
    /// <see cref="Result"/> or, when <see cref="Pending"/> is set, blocks on it - so the card's
    /// fixing state can be observed mid-run without a real pkexec dialog. The production fixer
    /// itself is covered by <see cref="Services.DeviceAccessFixerTests"/>.
    /// </summary>
    internal sealed class FakeDeviceAccessFixer : IDeviceAccessFixer
    {
        public DeviceAccessFixResult Result { get; set; } =
            DeviceAccessFixResult.Fixed("已修复设备访问权限（测试）。");

        public Task<DeviceAccessFixResult>? Pending { get; set; }

        public int Calls { get; private set; }

        public DeviceAccessFixResult Fix()
        {
            Calls++;
            return Pending is null ? Result : Pending.GetAwaiter().GetResult();
        }
    }
}
