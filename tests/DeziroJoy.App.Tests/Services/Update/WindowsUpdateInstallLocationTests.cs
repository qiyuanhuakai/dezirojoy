using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// Where the Windows update installs and what it restarts. The install directory is the one
/// the setup program registered, not a guess from the running process - a copy of the app
/// started from somewhere else would otherwise send the installer into that copy and leave
/// the registered install orphaned.
/// </summary>
public sealed class WindowsUpdateInstallLocationTests : IDisposable
{
    private readonly string _directory;

    public WindowsUpdateInstallLocationTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "dezirojoy-install-location-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_Registered_Install_Directory_Wins_Over_The_Running_One()
    {
        var running = Path.Combine(_directory, "running-copy");

        Assert.Equal(
            _directory,
            WindowsUpdateInstallLocation.ResolveInstallDirectory(
                registeredLocation: () => _directory,
                baseDirectory: () => running));
    }

    [Fact]
    public void A_Registration_That_Names_A_Directory_That_Is_Gone_Falls_Back_To_The_Running_One()
    {
        // The setup's record outlives a moved or deleted install: the directory that is
        // actually there is the honest answer, not the one on file.
        Assert.Equal(
            _directory,
            WindowsUpdateInstallLocation.ResolveInstallDirectory(
                registeredLocation: () => Path.Combine(_directory, "gone"),
                baseDirectory: () => _directory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Registration_That_Names_Nothing_Falls_Back_To_The_Running_One(string? registered)
    {
        Assert.Equal(
            _directory,
            WindowsUpdateInstallLocation.ResolveInstallDirectory(
                registeredLocation: () => registered,
                baseDirectory: () => _directory));
    }

    [Fact]
    public void The_Restarted_Executable_Is_The_One_The_Setup_Installs()
    {
        Assert.Equal(
            Path.Combine(_directory, "DeziroJoy.exe"),
            WindowsUpdateInstallLocation.ResolveExecutable(_directory));
    }
}
