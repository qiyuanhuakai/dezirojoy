using DeziroJoy.Platform.Windows.Elevation;
using Xunit;

namespace DeziroJoy.Platform.Tests.Windows;

/// <summary>
/// The scheduled-task command lines are pure string building, so they are verified here
/// even though schtasks itself only runs on Windows. The shape matters: a wrong /RL or
/// /SC silently produces a daemon that either never starts or starts unelevated.
/// </summary>
public class DaemonInstallerTests
{
    [Fact]
    public void Create_Command_Runs_The_Daemon_At_Logon_With_The_Highest_Privileges()
    {
        var command = DaemonInstaller.BuildCreateCommandLine(@"C:\Apps\DeziroJoy.exe");

        Assert.Contains("schtasks", command);
        Assert.Contains("/Create", command);
        Assert.Contains("/SC ONLOGON", command);
        Assert.Contains("/RL HIGHEST", command);
        Assert.Contains("/TN \"DeziroJoy\"", command);
        Assert.Contains("--daemon", command);
        Assert.Contains(@"C:\Apps\DeziroJoy.exe", command);
    }

    [Fact]
    public void Create_Command_Quotes_The_Executable_So_Paths_With_Spaces_Survive()
    {
        var command = DaemonInstaller.BuildCreateCommandLine(@"C:\Program Files\DeziroJoy\DeziroJoy.exe");

        Assert.Contains("\\\"C:\\Program Files\\DeziroJoy\\DeziroJoy.exe\\\" --daemon", command);
    }

    [Fact]
    public void Delete_Command_Targets_The_Same_Task_Name_And_Skips_Confirmation()
    {
        var command = DaemonInstaller.BuildDeleteCommandLine();

        Assert.Contains("schtasks", command);
        Assert.Contains("/Delete", command);
        Assert.Contains("/TN \"DeziroJoy\"", command);
        Assert.Contains("/F", command);
        Assert.DoesNotContain("--daemon", command);
    }

    [Fact]
    public void Create_Command_Rejects_An_Empty_Executable_Path()
    {
        Assert.Throws<ArgumentException>(() => DaemonInstaller.BuildCreateCommandLine(string.Empty));
    }
}
