using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The AppImage installer: rename the running image aside and put the verified download in
/// its place, keeping the aside copy as the manual-rollback file. Starting the successor is
/// deliberately NOT the installer's job any more - it used to start the new image and probe
/// for it while its own host still held the instance lease, so the probe was answered by the
/// old GUI and the new image exited on losing the arbitration. The relaunch now belongs to the
/// host, which starts the swapped-in image only once this process is gone, so
/// <see cref="UpdateInstallResult.RelaunchHandled"/> is false here. The renames act on real
/// temp files - the honest way to prove the swap and its undo - and nothing is ever exec'd.
/// </summary>
public sealed class AppImageInstallerTests : IDisposable
{
    private readonly string _dir;
    private readonly string _image;
    private readonly string _package;

    public AppImageInstallerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dezirojoy-appimage-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _image = Path.Combine(_dir, "DeziroJoy-0.0.1-x86_64.AppImage");
        _package = Path.Combine(_dir, "DeziroJoy-0.0.2-x86_64.AppImage");
        File.WriteAllText(_image, "OLD IMAGE");
        File.WriteAllText(_package, "NEW IMAGE");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static UpdateInstallRequest Request(string packagePath)
        => new(
            InstallKind.AppImage,
            "linux-x64",
            SemanticVersion.Parse("0.0.2"),
            "v0.0.2",
            packagePath,
            "DeziroJoy-0.0.2-x86_64.AppImage");

    [Fact]
    public async Task A_Verified_Download_Is_Swapped_In_And_The_Old_Image_Stays_As_The_Manual_Rollback_Copy()
    {
        var result = await Installer().InstallAsync(
            Request(_package),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The relaunch is the host's job now: it starts the swapped-in image only after this
        // process has exited, so the new image wins the instance arbitration.
        Assert.False(result.RelaunchHandled);

        // The new image is in place and executable, and the old one stays beside it as the
        // rollback copy - an AppImage is one file, so a manual rollback costs one rename.
        Assert.Equal("NEW IMAGE", File.ReadAllText(_image));
        Assert.Equal("OLD IMAGE", File.ReadAllText(_image + ".old"));

        if (OperatingSystem.IsLinux())
        {
            Assert.NotEqual(
                UnixFileMode.None,
                File.GetUnixFileMode(_image) & UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_Swap_In_That_Fails_Restores_The_Old_Image_Before_Reporting()
    {
        var missing = Path.Combine(_dir, "does-not-exist.AppImage");

        var result = await Installer().InstallAsync(
            Request(missing),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);

        // The rename-aside already happened, so the old image is moved back rather than lost.
        Assert.Equal("OLD IMAGE", File.ReadAllText(_image));
        Assert.False(File.Exists(_image + ".old"));
    }

    [Fact]
    public async Task A_Missing_AppImage_Path_Degrades_Without_Touching_Anything()
    {
        var result = await new AppImageInstaller(() => null).InstallAsync(
            Request(_package),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("手动下载", result.ErrorMessage);

        // The download is left untouched for a manual install.
        Assert.Equal("NEW IMAGE", File.ReadAllText(_package));
    }

    [Fact]
    public void It_Supports_Only_The_AppImage_Form()
    {
        var installer = Installer();

        Assert.True(installer.Supports(InstallKind.AppImage));
        Assert.False(installer.Supports(InstallKind.Deb));
        Assert.False(installer.Supports(InstallKind.Rpm));
        Assert.False(installer.Supports(InstallKind.WindowsInstall));
        Assert.False(installer.Supports(InstallKind.Flatpak));
        Assert.False(installer.Supports(InstallKind.Portable));
    }

    private AppImageInstaller Installer() => new(() => _image);
}
