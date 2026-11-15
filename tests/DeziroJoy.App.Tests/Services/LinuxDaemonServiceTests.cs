using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The Linux resident-daemon service: a systemd <em>user</em> unit, no elevation anywhere.
/// Every systemctl call goes through the injected fake, so these tests assert the unit file,
/// the command sequence and the reported state without a systemd instance - or a machine that
/// has one. The autostart toggle shares the installer's directory seam with
/// <see cref="AutostartInstallerLinuxTests"/>, hence the collection.
/// </summary>
[Collection("DeziroJoy.LinuxAutostart")]
public class LinuxDaemonServiceTests : IDisposable
{
    private readonly string _unitDirectory;
    private readonly string _autostartDirectory;
    private readonly FakeSystemCtl _systemCtl;

    public LinuxDaemonServiceTests()
    {
        _unitDirectory = Path.Combine(Path.GetTempPath(), "dezirojoy-systemd-" + Guid.NewGuid().ToString("N"));
        _autostartDirectory = Path.Combine(Path.GetTempPath(), "dezirojoy-autostart-" + Guid.NewGuid().ToString("N"));
        _systemCtl = new FakeSystemCtl();

        AutostartInstaller.LinuxAutostartDirectory = () => _autostartDirectory;
    }

    public void Dispose()
    {
        AutostartInstaller.LinuxAutostartDirectory = () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart");

        TryDelete(_unitDirectory);
        TryDelete(_autostartDirectory);

        GC.SuppressFinalize(this);
    }

    private LinuxDaemonService CreateService(
        Func<string?>? executable = null,
        Func<string, string?>? environmentVariable = null)
        => new(
            _systemCtl,
            () => _unitDirectory,
            executable ?? (() => "/opt/dezirojoy/DeziroJoy"),
            environmentVariable ?? BusEnvironmentPresent);

    /// <summary>
    /// A session-bus environment that is present, so the service's self-check passes without
    /// the tests depending on the machine they run on.
    /// </summary>
    private static string? BusEnvironmentPresent(string name)
        => name == "XDG_RUNTIME_DIR" ? "/run/user/1000" : null;

    [Fact]
    public void Install_Writes_The_Unit_Then_Enables_And_Starts_It()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Completed, result.Result);
        Assert.True(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));

        // No daemon-reload of its own: systemctl's enable verb reloads the user manager once
        // the symlinks are in place, and a separate reload only added a failure mode - a
        // manager that was briefly unreachable aborted the install between the write and the
        // enable, leaving a unit that was never enabled and that every later refresh then
        // honestly reported as 未启用.
        Assert.Equal(
            [$"enable {LinuxDaemonService.UnitName}", $"start {LinuxDaemonService.UnitName}"],
            _systemCtl.Calls);

        // Surviving a logout needs linger; the install result is where the user learns that.
        Assert.Contains("loginctl enable-linger", result.Message);
    }

    [Fact]
    public void The_Unit_Restarts_On_Failure_And_Never_Depends_On_A_Graphical_Session()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();

        service.InstallDaemon();

        var unit = File.ReadAllText(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName));

        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains("After=default.target", unit);
        Assert.Contains("WantedBy=default.target", unit);

        // uinput works headless: binding the daemon to a graphical login would break exactly
        // the SSH and console sessions that need mapping most.
        Assert.DoesNotContain("graphical-session.target", unit);
    }

    [Fact]
    public void The_Unit_ExecStart_Points_At_The_Installed_Binary_With_The_Daemon_Flag()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();

        service.InstallDaemon();

        var unit = File.ReadAllText(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName));

        Assert.Contains("ExecStart=\"/opt/dezirojoy/DeziroJoy\" --daemon", unit);
    }

    [Fact]
    public void An_Install_That_Fails_After_Writing_The_Unit_Reports_The_Intermediate_State()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        _systemCtl.Fail("enable", exitCode: 1, standardError: "Failed to connect to bus: No medium found");
        var service = CreateService();

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);

        // The unit file is written before any systemctl call, so a failure here leaves it on
        // disk: the result says exactly that - with the reason and the way out - instead of a
        // flat failure that leaves the user wondering why every later refresh shows
        // "installed but not running".
        Assert.True(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));
        Assert.Contains("已写单元但启用失败", result.Message);
        Assert.Contains("Failed to connect to bus", result.Message);
        Assert.Contains(LinuxDaemonService.UnitName, result.Message);

        // systemctl's stderr carries its own full stop; the message must not double it.
        Assert.DoesNotContain("。。", result.Message);
    }

    [Fact]
    public void An_Install_That_Cannot_Reach_The_Bus_Says_What_Is_Missing()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        _systemCtl.Fail("enable", exitCode: 1, standardError: "Failed to connect to bus: No medium found");
        var service = CreateService(environmentVariable: _ => null);

        var result = service.InstallDaemon();

        // The intermediate state names the actual cause: the GUI has no session-bus
        // environment, so every systemctl call would fail no matter what the unit says.
        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("已写单元但启用失败", result.Message);
        Assert.Contains("XDG_RUNTIME_DIR", result.Message);
        Assert.Contains("DBUS_SESSION_BUS_ADDRESS", result.Message);
        Assert.True(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));
    }

    [Fact]
    public void A_Start_That_Fails_After_A_Good_Enable_Reports_The_Enablement_As_Done()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        // The enable worked (the next login will start the daemon); only the start now failed.
        // Reporting this stage as "启用失败" misattributes the failure - and the reason a
        // daemon fails to start is in its own output, which systemctl's stderr never carries.
        _systemCtl.Fail(
            "start",
            exitCode: 1,
            standardError: "Job for dezirojoy-daemon.service failed. See \"systemctl status\" for details.");
        var service = CreateService();

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("已启用但启动失败", result.Message);
        Assert.DoesNotContain("已写单元但启用失败", result.Message);
        Assert.Contains($"journalctl --user -u {LinuxDaemonService.UnitName}", result.Message);
        Assert.Contains("缺少依赖库", result.Message);
        Assert.Contains($"systemctl --user start {LinuxDaemonService.UnitName}", result.Message);
    }

    [Fact]
    public void Running_Through_DotNet_Run_Is_Refused_Because_The_Muxer_Would_Be_Registered()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService(executable: () => "/usr/bin/dotnet");

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("dotnet run", result.Message);
        Assert.False(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));
    }

    [Fact]
    public void Uninstall_Stops_Disables_And_Removes_The_Unit()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();
        service.InstallDaemon();

        var result = service.UninstallDaemon();

        Assert.Equal(DaemonOp.Completed, result.Result);
        Assert.False(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));
        Assert.Contains($"stop {LinuxDaemonService.UnitName}", _systemCtl.Calls);
        Assert.Contains($"disable {LinuxDaemonService.UnitName}", _systemCtl.Calls);
        Assert.Contains("daemon-reload", _systemCtl.Calls.Last());
    }

    [Fact]
    public void Refresh_Reads_The_Unit_File_The_Active_Enabled_States_And_The_Xdg_Entry()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();
        service.InstallDaemon();

        // Installing the daemon unit must NOT count as installing the GUI autostart entry:
        // the two are independent switches, and a toggle fed by the unit's is-enabled would
        // show ON here and snap back after every refresh.
        service.Refresh();
        Assert.True(service.IsDaemonInstalled);
        Assert.True(service.IsDaemonRunning);
        Assert.True(service.IsDaemonEnabled);
        Assert.False(service.IsAutostartInstalled);
        Assert.Null(service.DaemonQueryError);

        // The daemon's own enablement is queried for its own reported state - the missing
        // query behind the user's "每次刷新都显示未启用".
        Assert.Contains(
            _systemCtl.Calls,
            call => call.StartsWith("is-enabled", StringComparison.Ordinal));

        // The autostart state comes from the XDG entry, never from the unit's enablement:
        // the reviewed P1 was exactly that mis-wiring, and the toggle round-trips below
        // prove the two do not move together.
        service.SetAutostart(enabled: true);
        service.Refresh();
        Assert.True(service.IsAutostartInstalled);
        Assert.True(service.IsDaemonInstalled);
        Assert.True(service.IsDaemonEnabled);

        service.SetAutostart(enabled: false);
        service.Refresh();
        Assert.False(service.IsAutostartInstalled);

        _systemCtl.Active = false;
        service.Refresh();
        Assert.True(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.False(service.IsAutostartInstalled);

        // Enablement is its own fact: the unit stops starting at login while everything else
        // about it stays put.
        _systemCtl.Enabled = false;
        service.Refresh();
        Assert.True(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.False(service.IsDaemonEnabled);
        Assert.Null(service.DaemonQueryError);

        File.Delete(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName));
        service.Refresh();
        Assert.False(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.False(service.IsDaemonEnabled);
        Assert.False(service.IsAutostartInstalled);
    }

    [Fact]
    public void A_Failed_Query_Is_Reported_With_Its_Reason_Never_As_Not_Running()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        _systemCtl.Fail("is-active", exitCode: 1, standardError: "Failed to connect to bus: No medium found");
        var service = CreateService();
        service.InstallDaemon();

        service.Refresh();

        // The bus never answered: that is a failed query, not "not running", and the reason
        // is what gets reported - the user's "每次刷新都显示未启用" was exactly this failure
        // being flattened into a state.
        Assert.False(service.IsDaemonRunning);
        Assert.NotNull(service.DaemonQueryError);
        Assert.Contains("Failed to connect to bus", service.DaemonQueryError);
    }

    [Fact]
    public void A_Failed_Enablement_Query_Does_Not_Falsely_Report_Enabled()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        _systemCtl.Fail("is-enabled", exitCode: 1, standardError: "boom");
        var service = CreateService();
        service.InstallDaemon();

        service.Refresh();

        // The two queries are independent facts: the run state answered, the enablement did
        // not, so enablement stays unproven and the reason is reported.
        Assert.True(service.IsDaemonRunning);
        Assert.False(service.IsDaemonEnabled);
        Assert.Contains("boom", service.DaemonQueryError);
    }

    [Fact]
    public void A_Unit_Systemd_Cannot_See_Is_Explained_Not_Merely_Reported_Disabled()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        // The unit file is on disk where this app writes it, but systemd answers "not-found" -
        // it sits outside the user unit search path, so nothing is enabled and nothing will
        // start. This is the state behind the user's "安装后刷新又显示未启用": a bare 未启用
        // with no reason, on every refresh, forever.
        _systemCtl.EnabledAnswer = "not-found";
        _systemCtl.Active = false; // a unit systemd cannot find is not active either
        var service = CreateService();
        service.InstallDaemon();

        service.Refresh();

        Assert.True(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonEnabled);
        Assert.False(service.IsDaemonRunning);
        Assert.Null(service.DaemonQueryError);

        var note = service.DaemonEnablementNote;
        Assert.NotNull(note);
        Assert.Contains(service.UnitPath, note);
        Assert.Contains("搜索路径", note);
    }

    [Fact]
    public void A_Static_Or_Masked_Unit_Says_Why_It_Will_Never_Autostart()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();
        service.InstallDaemon();

        // No [Install] section: the unit exists and can be started by hand, but never at login.
        _systemCtl.EnabledAnswer = "static";
        service.Refresh();
        Assert.False(service.IsDaemonEnabled);
        Assert.Contains("static", service.DaemonEnablementNote);
        Assert.Contains("[Install]", service.DaemonEnablementNote);

        // Masked by an administrator: the same plain 未启用, a different reason and a
        // different way out.
        _systemCtl.EnabledAnswer = "masked";
        service.Refresh();
        Assert.False(service.IsDaemonEnabled);
        Assert.Contains($"systemctl --user unmask {LinuxDaemonService.UnitName}", service.DaemonEnablementNote);
    }

    [Fact]
    public void Ordinary_Enablement_Answers_Carry_No_Note()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService();
        service.InstallDaemon();

        service.Refresh();
        Assert.True(service.IsDaemonEnabled);
        Assert.Null(service.DaemonEnablementNote);

        // Plainly disabled is all the plain line needs to say; so is plainly enabled.
        _systemCtl.Enabled = false;
        service.Refresh();
        Assert.False(service.IsDaemonEnabled);
        Assert.Null(service.DaemonEnablementNote);

        // A query that never answered is not an enablement fact either - that stays the
        // query error, never a note that pretends to know the answer.
        _systemCtl.Enabled = true;
        _systemCtl.Fail("is-enabled", exitCode: 1, standardError: "boom");
        service.Refresh();
        Assert.Null(service.DaemonEnablementNote);
        Assert.Contains("boom", service.DaemonQueryError);
    }

    [Fact]
    public void The_Default_Unit_Directory_Matches_Systemd_Search_Path_Resolution()
    {
        // systemd resolves the user config directory as $XDG_CONFIG_HOME/systemd/user when the
        // variable is set and $HOME/.config/systemd/user otherwise - and neither has to exist
        // yet. The default once went through GetFolderPath(ApplicationData), which returns an
        // empty string when the directory does not exist: the unit path became CWD-relative,
        // the unit was written where systemd never searches, and every enable answered
        // not-found while the card said 已安装.
        Assert.Equal(
            Path.Combine("/custom/config", "systemd", "user"),
            LinuxDaemonService.DefaultUnitDirectory(name => name == "XDG_CONFIG_HOME" ? "/custom/config" : "/home/qiyuaner"));

        Assert.Equal(
            Path.Combine("/home/qiyuaner", ".config", "systemd", "user"),
            LinuxDaemonService.DefaultUnitDirectory(name => name == "HOME" ? "/home/qiyuaner" : null));

        // An empty XDG_CONFIG_HOME counts as unset, exactly as in systemd.
        Assert.Equal(
            Path.Combine("/home/qiyuaner", ".config", "systemd", "user"),
            LinuxDaemonService.DefaultUnitDirectory(name => name switch
            {
                "XDG_CONFIG_HOME" => "",
                "HOME" => "/home/qiyuaner",
                _ => null,
            }));
    }

    [Fact]
    public void An_Install_Without_Any_Home_Directory_Refuses_Instead_Of_Writing_Into_The_Working_Directory()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        // No HOME and no XDG_CONFIG_HOME: there is nothing to resolve the unit path against,
        // and a relative unit file would be written into the working directory - installed
        // nowhere, while the card (checking the same relative path) says 已安装.
        var service = new LinuxDaemonService(
            _systemCtl,
            () => LinuxDaemonService.DefaultUnitDirectory(_ => null),
            () => "/opt/dezirojoy/DeziroJoy",
            BusEnvironmentPresent);

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("无法确定用户配置目录", result.Message);
        Assert.False(Path.IsPathRooted(service.UnitPath));
        Assert.Empty(_systemCtl.Calls);
    }

    [Fact]
    public void Refresh_Without_The_Bus_Environment_Reports_It_Instead_Of_Querying()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService(environmentVariable: _ => null);
        service.InstallDaemon();
        var callsAfterInstall = _systemCtl.Calls.Count;

        service.Refresh();

        Assert.True(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.False(service.IsDaemonEnabled);
        Assert.Contains("XDG_RUNTIME_DIR", service.DaemonQueryError);
        Assert.Contains("DBUS_SESSION_BUS_ADDRESS", service.DaemonQueryError);

        // The missing environment is named up front: no systemctl call is made to fail first.
        Assert.Equal(callsAfterInstall, _systemCtl.Calls.Count);
    }

    [Fact]
    public void Start_Without_The_Bus_Environment_Says_So_Instead_Of_Failing_Cryptically()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the systemd/XDG paths only exist on Linux");

        var service = CreateService(environmentVariable: _ => null);
        Directory.CreateDirectory(_unitDirectory);
        File.WriteAllText(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName), "[Unit]");
        service.Refresh();

        var result = service.StartDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("XDG_RUNTIME_DIR", result.Message);
        Assert.DoesNotContain(_systemCtl.Calls, call => call.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Autostart_Toggle_Writes_And_Removes_The_Xdg_Entry_Not_The_Unit()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XDG path only exists on Linux");

        var service = CreateService();
        service.InstallDaemon();

        var enabled = service.SetAutostart(enabled: true);

        Assert.Equal(DaemonOp.Completed, enabled.Result);
        var desktop = Path.Combine(_autostartDirectory, "DeziroJoy.desktop");
        Assert.True(File.Exists(desktop));
        Assert.Contains("Exec=\"/opt/dezirojoy/DeziroJoy\"", File.ReadAllText(desktop));

        // The displayed toggle state must come from the entry that was just written - the
        // review's P1 was exactly this round-trip going unasserted.
        service.Refresh();
        Assert.True(service.IsAutostartInstalled);

        var disabled = service.SetAutostart(enabled: false);

        Assert.Equal(DaemonOp.Completed, disabled.Result);
        Assert.False(File.Exists(desktop));

        service.Refresh();
        Assert.False(service.IsAutostartInstalled);

        // The daemon unit is untouched by the toggle: still installed, still running.
        Assert.True(service.IsDaemonInstalled);
        Assert.True(service.IsDaemonRunning);
    }

    [Fact]
    public void Linux_Needs_No_Elevation_And_Reports_Its_Run_Level_As_Meaningless()
    {
        var service = CreateService();

        Assert.True(service.IsSameUserModel);
        Assert.False(service.IsElevated);
        Assert.Null(service.DaemonRunLevel);
    }

    [Fact]
    public void Off_Linux_The_Service_Says_So_Instead_Of_Pretending()
    {
        var service = CreateService();

        Assert.Equal(OperatingSystem.IsLinux(), service.IsSupported);

        if (OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.Equal(DaemonOp.Unsupported, service.InstallDaemon().Result);
        Assert.Equal(DaemonOp.Unsupported, service.UninstallDaemon().Result);
        Assert.Equal(DaemonOp.Unsupported, service.SetAutostart(enabled: true).Result);

        service.Refresh();
        Assert.False(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.False(service.IsDaemonEnabled);
        Assert.Null(service.DaemonQueryError);
        Assert.False(service.IsAutostartInstalled);
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
    /// Records every systemctl invocation and answers by verb: is-active reports the
    /// configurable run state, is-enabled reports the configurable enablement, everything else
    /// succeeds unless a failure was armed. The enablement state exists because the daemon's
    /// own is-enabled is now a reported fact - what the service must never do is derive the
    /// GUI autostart toggle from it (the reviewed P1); the toggle reads the XDG entry.
    /// </summary>
    private sealed class FakeSystemCtl : ISystemCtl
    {
        public List<string> Calls { get; } = [];

        public bool Active { get; set; } = true;

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Overrides the is-enabled answer with a raw systemd state word ("not-found",
        /// "static", "masked", ...) - the answers that are neither plainly enabled nor plainly
        /// disabled and that the card must explain instead of flattening.
        /// </summary>
        public string? EnabledAnswer { get; set; }

        private readonly Dictionary<string, SystemCtlResult> _failures = new(StringComparer.Ordinal);

        public void Fail(string verbPrefix, int exitCode, string standardError)
            => _failures[verbPrefix] = new SystemCtlResult(exitCode, string.Empty, standardError);

        public SystemCtlResult Run(string arguments)
        {
            Calls.Add(arguments);

            foreach (var (prefix, failure) in _failures)
            {
                if (arguments.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return failure;
                }
            }

            if (arguments.StartsWith("is-active", StringComparison.Ordinal))
            {
                return new SystemCtlResult(Active ? 0 : 3, Active ? "active" : "inactive", string.Empty);
            }

            if (arguments.StartsWith("is-enabled", StringComparison.Ordinal))
            {
                var answer = EnabledAnswer ?? (Enabled ? "enabled" : "disabled");
                var exit = answer is "enabled" or "enabled-runtime" ? 0 : 1;

                return new SystemCtlResult(exit, answer, string.Empty);
            }

            return new SystemCtlResult(0, string.Empty, string.Empty);
        }
    }
}
