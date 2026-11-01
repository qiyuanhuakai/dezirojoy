using System;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The logon-autostart task must be a distinct, independently toggleable scheduled task from the
/// headless daemon, and it must use an ONLOGON trigger (a boot/service trigger would run in
/// Session 0 and be unable to inject input). These pin the command lines that guarantee both.
/// </summary>
public class AutostartInstallerTests
{
    [Fact]
    public void The_Autostart_Task_Name_Is_Distinct_From_The_Daemon_Task()
    {
        Assert.Equal("DeziroJoy-Autostart", AutostartInstaller.TaskName);
        Assert.NotEqual("DeziroJoy", AutostartInstaller.TaskName);
    }

    [Fact]
    public void Create_Command_Uses_An_OnLogon_Trigger_Not_A_Boot_Trigger()
    {
        var command = AutostartInstaller.BuildCreateCommandLine(@"C:\Apps\DeziroJoy.exe");

        Assert.Contains("/SC ONLOGON", command);
        Assert.DoesNotContain("/SC ONSTART", command);
        Assert.DoesNotContain("Session 0", command);
    }

    [Fact]
    public void Create_Command_Launches_The_Gui_Unelevated()
    {
        var command = AutostartInstaller.BuildCreateCommandLine(@"C:\Apps\DeziroJoy.exe");

        // The windowed app stays asInvoker and relaunches itself elevated on demand, so the
        // task must not force /RL HIGHEST the way the headless daemon does, and it launches the
        // GUI rather than the --daemon headless process.
        Assert.DoesNotContain("/RL HIGHEST", command);
        Assert.DoesNotContain("--daemon", command);
        Assert.Contains(@"C:\Apps\DeziroJoy.exe", command);
    }

    [Fact]
    public void Delete_Command_Targets_The_Autostart_Task()
    {
        var command = AutostartInstaller.BuildDeleteCommandLine();

        Assert.Equal("schtasks /Delete /TN \"DeziroJoy-Autostart\" /F", command);
    }

    [Fact]
    public void Create_Command_Rejects_An_Empty_Path()
    {
        Assert.Throws<ArgumentException>(() => AutostartInstaller.BuildCreateCommandLine("  "));
    }
}
