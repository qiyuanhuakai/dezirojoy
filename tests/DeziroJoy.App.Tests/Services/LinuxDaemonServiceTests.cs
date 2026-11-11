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

    private LinuxDaemonService CreateService(Func<string?>? executable = null)
        => new(_systemCtl, () => _unitDirectory, executable ?? (() => "/opt/dezirojoy/DeziroJoy"));

    [Fact]
    public void Install_Writes_The_Unit_Then_Reloads_Enables_And_Starts_It()
    {
        var service = CreateService();

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Completed, result.Result);
        Assert.True(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));
        Assert.Contains("daemon-reload", _systemCtl.Calls[0]);
        Assert.Contains($"enable {LinuxDaemonService.UnitName}", _systemCtl.Calls[1]);
        Assert.Contains($"start {LinuxDaemonService.UnitName}", _systemCtl.Calls[2]);

        // Surviving a logout needs linger; the install result is where the user learns that.
        Assert.Contains("loginctl enable-linger", result.Message);
    }

    [Fact]
    public void The_Unit_Restarts_On_Failure_And_Never_Depends_On_A_Graphical_Session()
    {
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
        var service = CreateService();

        service.InstallDaemon();

        var unit = File.ReadAllText(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName));

        Assert.Contains("ExecStart=\"/opt/dezirojoy/DeziroJoy\" --daemon", unit);
    }

    [Fact]
    public void A_Systemctl_Failure_Is_Reported_With_Its_Output()
    {
        _systemCtl.Fail("enable", exitCode: 1, standardError: "Failed to connect to bus: No medium found");
        var service = CreateService();

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("Failed to connect to bus", result.Message);
    }

    [Fact]
    public void Running_Through_DotNet_Run_Is_Refused_Because_The_Muxer_Would_Be_Registered()
    {
        var service = CreateService(executable: () => "/usr/bin/dotnet");

        var result = service.InstallDaemon();

        Assert.Equal(DaemonOp.Failed, result.Result);
        Assert.Contains("dotnet run", result.Message);
        Assert.False(File.Exists(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName)));
    }

    [Fact]
    public void Uninstall_Stops_Disables_And_Removes_The_Unit()
    {
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
    public void Refresh_Reads_The_File_The_Active_State_And_The_Enabled_State()
    {
        var service = CreateService();
        service.InstallDaemon();

        service.Refresh();

        Assert.True(service.IsDaemonInstalled);
        Assert.True(service.IsDaemonRunning);
        Assert.True(service.IsAutostartInstalled);

        _systemCtl.Active = false;
        service.Refresh();
        Assert.True(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.True(service.IsAutostartInstalled);

        File.Delete(Path.Combine(_unitDirectory, LinuxDaemonService.UnitName));

        // systemd reports a removed unit as inactive and disabled, so the fake says so too.
        _systemCtl.Active = false;
        _systemCtl.Enabled = false;
        service.Refresh();
        Assert.False(service.IsDaemonInstalled);
        Assert.False(service.IsDaemonRunning);
        Assert.False(service.IsAutostartInstalled);
    }

    [Fact]
    public void The_Autostart_Toggle_Writes_And_Removes_The_Xdg_Entry_Not_The_Unit()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XDG path only exists on Linux");

        var service = CreateService();

        var enabled = service.SetAutostart(enabled: true);

        Assert.Equal(DaemonOp.Completed, enabled.Result);
        var desktop = Path.Combine(_autostartDirectory, "DeziroJoy.desktop");
        Assert.True(File.Exists(desktop));
        Assert.Contains("Exec=\"/opt/dezirojoy/DeziroJoy\"", File.ReadAllText(desktop));

        var disabled = service.SetAutostart(enabled: false);

        Assert.Equal(DaemonOp.Completed, disabled.Result);
        Assert.False(File.Exists(desktop));
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
    /// Records every systemctl invocation and answers by verb: is-active and is-enabled report
    /// configurable state, everything else succeeds unless a failure was armed.
    /// </summary>
    private sealed class FakeSystemCtl : ISystemCtl
    {
        public List<string> Calls { get; } = [];

        public bool Active { get; set; } = true;

        public bool Enabled { get; set; } = true;

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
                return new SystemCtlResult(Enabled ? 0 : 1, Enabled ? "enabled" : "disabled", string.Empty);
            }

            return new SystemCtlResult(0, string.Empty, string.Empty);
        }
    }
}
