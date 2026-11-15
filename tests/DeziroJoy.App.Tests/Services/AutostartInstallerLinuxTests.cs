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
    private readonly Func<string> _originalAutostartDirectory;

    public AutostartInstallerLinuxTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "dezirojoy-xdg-" + Guid.NewGuid().ToString("N"));
        _originalAutostartDirectory = AutostartInstaller.LinuxAutostartDirectory;
        AutostartInstaller.LinuxAutostartDirectory = () => _directory;
    }

    public void Dispose()
    {
        AutostartInstaller.LinuxAutostartDirectory = _originalAutostartDirectory;

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
        // The seam is pointed at a fixture directory for the round-trip test, so only the file
        // name - the one thing the default shape fixes - is asserted from the overridden path;
        // the directory resolution itself is covered by the resolver contract below.
        Assert.Equal("DeziroJoy.desktop", Path.GetFileName(AutostartInstaller.LinuxDesktopPath));
    }

    [Fact]
    public void The_Default_Autostart_Directory_Matches_Xdg_Resolution()
    {
        // XDG resolves the autostart directory as $XDG_CONFIG_HOME/autostart when the variable
        // is set and $HOME/.config/autostart otherwise - and neither has to exist yet. The
        // default once went through GetFolderPath(ApplicationData), which returns an empty
        // string when the directory does not exist: the desktop-file path became CWD-relative,
        // the entry was written where no desktop environment searches, and every refresh showed
        // 未启用 while the card said 已启用.
        Assert.Equal(
            Path.Combine("/custom/config", "autostart"),
            AutostartInstaller.DefaultLinuxAutostartDirectory(name => name == "XDG_CONFIG_HOME" ? "/custom/config" : "/home/qiyuaner"));

        Assert.Equal(
            Path.Combine("/home/qiyuaner", ".config", "autostart"),
            AutostartInstaller.DefaultLinuxAutostartDirectory(name => name == "HOME" ? "/home/qiyuaner" : null));

        // An empty XDG_CONFIG_HOME counts as unset, exactly as in XDG.
        Assert.Equal(
            Path.Combine("/home/qiyuaner", ".config", "autostart"),
            AutostartInstaller.DefaultLinuxAutostartDirectory(name => name switch
            {
                "XDG_CONFIG_HOME" => "",
                "HOME" => "/home/qiyuaner",
                _ => null,
            }));
    }

    [Fact]
    public void Install_Without_Any_Home_Directory_Refuses_Instead_Of_Writing_Into_The_Working_Directory()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the XDG path only exists on Linux");

        // No HOME and no XDG_CONFIG_HOME: there is nothing to resolve the autostart path
        // against, and a relative desktop file would be written into the working directory -
        // installed nowhere, while the card (checking the same relative path) says 已启用.
        AutostartInstaller.LinuxAutostartDirectory
            = () => AutostartInstaller.DefaultLinuxAutostartDirectory(_ => null);

        try
        {
            Assert.False(AutostartInstaller.Install("/opt/dezirojoy/DeziroJoy"));
            Assert.False(Path.IsPathRooted(AutostartInstaller.LinuxDesktopPath));
            Assert.False(File.Exists(AutostartInstaller.LinuxDesktopPath));
        }
        finally
        {
            AutostartInstaller.LinuxAutostartDirectory = () => _directory;
        }
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
