using System.Diagnostics;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The AppImage installer: rename the running image aside, put the verified download in its
/// place, start it, probe the activation channel, and either commit (discard the aside copy) or
/// roll the whole swap back. The renames act on real temp files - the honest way to prove the
/// swap and its undo - while the launch and the probe are injected fakes, so no real AppImage is
/// ever exec'd.
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

    private AppImageInstaller Installer(
        FakeLauncher launcher,
        bool ready,
        TimeSpan? readyBudget = null)
        => new(
            () => _image,
            launcher,
            (_, _, _, _) => Task.FromResult(ready),
            () => Task.CompletedTask,
            readyBudget);

    [Fact]
    public async Task A_Ready_New_Image_Commits_The_Swap_And_Discards_The_Old()
    {
        var launcher = new FakeLauncher();

        var result = await Installer(launcher, ready: true).InstallAsync(
            Request(_package),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.RelaunchHandled);

        // The new image is in place and the aside copy is gone.
        Assert.Equal("NEW IMAGE", File.ReadAllText(_image));
        Assert.False(File.Exists(_image + ".old"));

        // The new image was started exactly once.
        var start = Assert.Single(launcher.Calls);
        Assert.Equal(_image, start.FileName);
    }

    [Fact]
    public async Task An_Image_That_Never_Answers_Is_Rolled_Back_To_The_Old()
    {
        var launcher = new FakeLauncher();

        // A spent budget: the probe answers false once, then the wait gives up.
        var result = await Installer(launcher, ready: false, TimeSpan.FromMilliseconds(1))
            .InstallAsync(Request(_package), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);

        // The old image is restored and the new download is gone.
        Assert.Equal("OLD IMAGE", File.ReadAllText(_image));
        Assert.False(File.Exists(_image + ".old"));

        // The new image was started, found not ready, and the old image restarted in its place.
        Assert.Equal(2, launcher.Calls.Count);
        Assert.All(launcher.Calls, start => Assert.Equal(_image, start.FileName));
    }

    [Fact]
    public async Task A_Swap_In_That_Fails_Restores_The_Old_Image_Before_Reporting()
    {
        var launcher = new FakeLauncher();
        var missing = Path.Combine(_dir, "does-not-exist.AppImage");

        var result = await Installer(launcher, ready: true).InstallAsync(
            Request(missing),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);

        // The rename-aside already happened, so the old image is moved back rather than lost.
        Assert.Equal("OLD IMAGE", File.ReadAllText(_image));
        Assert.False(File.Exists(_image + ".old"));

        // Nothing was started: the failure is a guidance path, not a half-finished install.
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task A_Missing_AppImage_Path_Degrades_Without_Touching_Anything()
    {
        var launcher = new FakeLauncher();
        var installer = new AppImageInstaller(
            () => null,
            launcher,
            (_, _, _, _) => Task.FromResult(true),
            () => Task.CompletedTask);

        var result = await installer.InstallAsync(
            Request(_package),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("手动下载", result.ErrorMessage);
        Assert.Empty(launcher.Calls);

        // The download is left untouched for a manual install.
        Assert.Equal("NEW IMAGE", File.ReadAllText(_package));
    }

    [Fact]
    public void It_Supports_Only_The_AppImage_Form()
    {
        var launcher = new FakeLauncher();
        var installer = Installer(launcher, ready: true);

        Assert.True(installer.Supports(InstallKind.AppImage));
        Assert.False(installer.Supports(InstallKind.Deb));
        Assert.False(installer.Supports(InstallKind.Rpm));
        Assert.False(installer.Supports(InstallKind.WindowsInstall));
        Assert.False(installer.Supports(InstallKind.Flatpak));
        Assert.False(installer.Supports(InstallKind.Portable));
    }

    private sealed class FakeLauncher : IUpdateProcessLauncher
    {
        public List<ProcessStartInfo> Calls { get; } = [];

        public bool Next { get; set; } = true;

        public bool Start(ProcessStartInfo startInfo)
        {
            Calls.Add(startInfo);
            return Next;
        }
    }
}
