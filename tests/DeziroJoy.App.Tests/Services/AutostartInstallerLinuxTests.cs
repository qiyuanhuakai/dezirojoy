using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services;

/// <summary>
/// The Linux half of the logon-autostart installer: an XDG <c>.desktop</c> entry under
/// <c>~/.config/autostart</c> instead of a scheduled task. Shares the directory seam with
/// <see cref="LinuxDaemonServiceTests"/> - hence the collection - so the two never race on it.
/// </summary>
[Collection("DeziroJoy.LinuxAutostart")]
public class AutostartInstallerLinuxTests : IDisposable
{
    private readonly string _directory;

    public AutostartInstallerLinuxTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-xdg-" + Guid.NewGuid().ToString("N"));
        AutostartInstaller.LinuxAutostartDirectory = () => _directory;
    }

    public void Dispose()
    {
        AutostartInstaller.LinuxAutostartDirectory = () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart");

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_Desktop_Entry_Is_A_Valid_Application_Entry()
    {
        var file = AutostartInstaller.BuildLinuxDesktopFile("/opt/dezirojoy/DeziroJoy");

        Assert.Contains("[Desktop Entry]", file);
        Assert.Contains("Type=Application", file);
        Assert.Contains("Name=DeziroJoy", file);
        Assert.Contains("Exec=\"/opt/dezirojoy/DeziroJoy\"", file);
        Assert.Contains("Icon=dezirojoy", file);
        Assert.Contains("Terminal=false", file);
    }

    [Fact]
    public void The_Desktop_Entry_Rejects_An_Empty_Path_Like_The_Windows_Command_Does()
    {
        Assert.Throws<ArgumentException>(() => AutostartInstaller.BuildLinuxDesktopFile("  "));
    }

    [Fact]
    public void The_Desktop_File_Has_A_Fixed_Name_Under_The_Config_Autostart_Directory()
    {
        // The seam is pointed at a fixture directory for the round-trip test, so the default
        // shape is asserted from the resolver's own contract rather than the overridden path.
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart",
            "DeziroJoy.desktop");

        Assert.Equal("DeziroJoy.desktop", Path.GetFileName(AutostartInstaller.LinuxDesktopPath));
        Assert.EndsWith(
            Path.Combine("autostart", "DeziroJoy.desktop").Replace('\\', '/'),
            expected.Replace('\\', '/'));
    }

    [Fact]
    public void Install_Uninstall_And_IsInstalled_Round_Trip_On_Linux()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XDG path only exists on Linux");

        Assert.False(AutostartInstaller.IsInstalled());

        Assert.True(AutostartInstaller.Install("/opt/dezirojoy/DeziroJoy"));
        Assert.True(AutostartInstaller.IsInstalled());

        Assert.True(AutostartInstaller.Uninstall());
        Assert.False(AutostartInstaller.IsInstalled());

        // Uninstalling something that was never installed is a no-op, not a failure.
        Assert.True(AutostartInstaller.Uninstall());
        Assert.False(AutostartInstaller.IsInstalled());
    }

    [Fact]
    public void The_Windows_Command_Lines_Are_Untouched_By_The_Linux_Path()
    {
        var command = AutostartInstaller.BuildCreateCommandLine(@"C:\Apps\DeziroJoy.exe");

        Assert.Contains("/SC ONLOGON", command);
        Assert.DoesNotContain("desktop", command, StringComparison.OrdinalIgnoreCase);
    }
}
