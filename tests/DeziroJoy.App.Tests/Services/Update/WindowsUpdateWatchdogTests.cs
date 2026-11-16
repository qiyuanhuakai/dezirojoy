using System.Diagnostics;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The watchdog's own pieces: where its files live and how it is launched. The script text
/// itself is asserted through <see cref="WindowsUpdateInstallerTests"/>, which reads the file
/// the installer writes; what is checked here is the path policy and the launch shape.
/// </summary>
public sealed class WindowsUpdateWatchdogTests
{
    [Fact]
    public void The_Logs_Live_Under_The_Per_User_Local_Data_Directory()
    {
        var directory = WindowsUpdateWatchdog.ResolveLogDirectory(
            () => Path.Combine(Path.GetTempPath(), "localappdata"));

        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "localappdata", "DeziroJoy", "UpdateLogs"),
            directory);

        // Not the roaming profile and not the install directory: these files outlive the
        // update, a user has to be able to find them, and a log written into the install
        // directory would be deleted by the next install.
        Assert.DoesNotContain("ApplicationData", directory);
    }

    [Fact]
    public void A_Machine_With_No_Per_User_Directory_Falls_Back_To_The_Temporary_One()
    {
        var directory = WindowsUpdateWatchdog.ResolveLogDirectory(() => null);

        Assert.Contains(Path.GetTempPath(), directory);
        Assert.EndsWith("UpdateLogs", directory);
    }

    [Fact]
    public void The_Launch_Bypasses_The_Execution_Policy_Without_Touching_The_Machine()
    {
        var start = WindowsUpdateWatchdog.BuildStartInfo(
            "watchdog.ps1",
            "setup.exe",
            "install",
            Path.Combine("install", "DeziroJoy.exe"),
            "setup.log",
            "watchdog.log",
            4242);

        // The script is a file this app wrote, and Windows PowerShell runs no script at all
        // under the default client policy - so the bypass is scoped to this process only.
        Assert.Contains("-ExecutionPolicy", start.ArgumentList);
        Assert.Contains("Bypass", start.ArgumentList);
        Assert.Contains("-NoProfile", start.ArgumentList);
        Assert.Contains("-NonInteractive", start.ArgumentList);
        Assert.Contains("-File", start.ArgumentList);
        Assert.Contains("watchdog.ps1", start.ArgumentList);

        // The watchdog is a background process: no window, no shell, no pipes.
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void The_Launch_Names_The_System_PowerShell()
    {
        var start = WindowsUpdateWatchdog.BuildStartInfo(
            "watchdog.ps1", "setup.exe", "install", "DeziroJoy.exe", "setup.log", "watchdog.log", 1);

        Assert.EndsWith("powershell.exe", start.FileName);
        Assert.Equal(
            Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            start.FileName);
    }
}
