using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The Flatpak installer that installs nothing: it supports the form so the orchestrator has an
/// honest answer for it, and it names the command instead of pretending to install. The relaunch
/// is left to the user.
/// </summary>
public sealed class FlatpakInstallerTests
{
    private readonly FlatpakInstaller _installer = new();

    [Fact]
    public void It_Supports_Only_The_Flatpak_Form()
    {
        Assert.True(_installer.Supports(InstallKind.Flatpak));
        Assert.False(_installer.Supports(InstallKind.Deb));
        Assert.False(_installer.Supports(InstallKind.Rpm));
        Assert.False(_installer.Supports(InstallKind.AppImage));
        Assert.False(_installer.Supports(InstallKind.WindowsInstall));
        Assert.False(_installer.Supports(InstallKind.Portable));
    }

    [Fact]
    public async Task It_Names_The_Command_Instead_Of_Installing()
    {
        var request = new UpdateInstallRequest(
            InstallKind.Flatpak,
            "linux-x64",
            SemanticVersion.Parse("0.0.2"),
            "v0.0.2",
            "/tmp/unused",
            "unused");

        var result = await _installer.InstallAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("flatpak update dezirojoy", result.ErrorMessage);

        // The relaunch is the user's to make; this installer arranges nothing.
        Assert.False(result.RelaunchHandled);
    }
}
