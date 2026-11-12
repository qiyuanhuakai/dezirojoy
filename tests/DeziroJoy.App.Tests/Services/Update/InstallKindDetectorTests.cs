using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The install-form detection. Every probe is injected, so what is under test is the order
/// the signals are read in - Flatpak before AppImage before the Windows uninstall key before
/// the package databases - rather than the machine the tests happen to run on. The one
/// exception is the <c>/opt/dezirojoy</c> branch: whether an injected path lands inside that
/// directory is decided by the OS, so the tests that walk it are Linux-only.
/// </summary>
public sealed class InstallKindDetectorTests
{
    private const string LinuxRid = "linux-x64";

    private static InstallKindInfo Detect(
        bool flatpakInfoExists = false,
        string? appImagePath = null,
        string? processPath = "/usr/bin/DeziroJoy",
        bool isWindows = false,
        bool windowsUninstallKeyExists = false,
        LinuxPackageFormat? installedPackage = null,
        string rid = LinuxRid)
        => InstallKindDetector.Detect(
            flatpakInfoExists: () => flatpakInfoExists,
            appImagePath: () => appImagePath,
            processPath: () => processPath,
            isWindows: () => isWindows,
            windowsUninstallKeyExists: () => windowsUninstallKeyExists,
            packageInstalled: format => installedPackage == format,
            runtimeIdentifier: () => rid);

    [Fact]
    public void The_Flatpak_Marker_Wins_Over_Every_Other_Signal()
    {
        // A Flatpak build is also a Linux build that may sit anywhere: the marker file the
        // runtime places at / is the only signal that means "sandbox", so it is read first.
        var info = Detect(flatpakInfoExists: true, appImagePath: "/tmp/DeziroJoy.AppImage", isWindows: true);

        Assert.Equal(InstallKind.Flatpak, info.Kind);
    }

    [Fact]
    public void An_AppImage_Is_Detected_From_The_Runtime_Variable()
    {
        var appImage = Path.Combine(Path.GetTempPath(), $"dezirojoy-test-{Guid.NewGuid():N}.AppImage");
        File.WriteAllText(appImage, "not really an image");

        try
        {
            var info = Detect(appImagePath: appImage, processPath: "/opt/dezirojoy/DeziroJoy");

            Assert.Equal(InstallKind.AppImage, info.Kind);
        }
        finally
        {
            File.Delete(appImage);
        }
    }

    [Fact]
    public void An_AppImage_Variable_Pointing_At_A_Missing_File_Is_Not_An_AppImage()
    {
        var info = Detect(appImagePath: "/nonexistent/DeziroJoy.AppImage", processPath: "/opt/dezirojoy/DeziroJoy");

        Assert.Equal(InstallKind.Portable, info.Kind);
    }

    [Fact]
    public void An_Empty_AppImage_Variable_Is_Not_An_AppImage()
    {
        var info = Detect(appImagePath: "   ");

        Assert.Equal(InstallKind.Portable, info.Kind);
    }

    [Fact]
    public void The_Windows_Uninstall_Key_Means_A_Setup_Install()
    {
        var info = Detect(isWindows: true, windowsUninstallKeyExists: true, rid: "win-x64");

        Assert.Equal(InstallKind.WindowsInstall, info.Kind);
        Assert.Equal("win-x64", info.Rid);
    }

    [Fact]
    public void A_Windows_Copy_The_Setup_Program_Never_Registered_Is_Portable()
    {
        var info = Detect(isWindows: true, windowsUninstallKeyExists: false);

        Assert.Equal(InstallKind.Portable, info.Kind);
    }

    [Fact]
    public void A_Program_Under_Opt_With_A_Deb_Package_Is_A_Deb_Install()
    {
        // The install directory is a POSIX path, and the detector compares it with the
        // separator of the running OS: off Linux the path is rooted against the current
        // drive, so this branch is unreachable rather than merely untested there.
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the /opt/dezirojoy + dpkg/rpm detection only exists on Linux");

        var info = Detect(processPath: "/opt/dezirojoy/DeziroJoy", installedPackage: LinuxPackageFormat.Deb);

        Assert.Equal(InstallKind.Deb, info.Kind);
    }

    [Fact]
    public void A_Program_Under_Opt_With_An_Rpm_Package_Is_An_Rpm_Install()
    {
        // The /opt layout - and the reason it is Linux-only - is the same as the .deb above.
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the /opt/dezirojoy + dpkg/rpm detection only exists on Linux");

        // The two share the /opt layout, so the package database is the only thing that can
        // tell them apart: the same path must produce a different answer per database.
        var info = Detect(processPath: "/opt/dezirojoy/DeziroJoy", installedPackage: LinuxPackageFormat.Rpm);

        Assert.Equal(InstallKind.Rpm, info.Kind);
    }

    [Fact]
    public void A_Program_Under_Opt_Without_Either_Package_Is_Portable()
    {
        var info = Detect(processPath: "/opt/dezirojoy/DeziroJoy");

        Assert.Equal(InstallKind.Portable, info.Kind);
    }

    [Fact]
    public void A_Program_Outside_Opt_Is_Portable_Without_Asking_The_Package_Databases()
    {
        var asked = false;
        var info = InstallKindDetector.Detect(
            processPath: () => "/home/someone/DeziroJoy/DeziroJoy",
            packageInstalled: _ =>
            {
                asked = true;
                return true;
            },
            runtimeIdentifier: () => LinuxRid);

        Assert.Equal(InstallKind.Portable, info.Kind);
        Assert.False(asked, "a path outside the install directory proves nothing about packages");
    }

    [Fact]
    public void A_Missing_Process_Path_Is_Portable()
    {
        var info = Detect(processPath: null);

        Assert.Equal(InstallKind.Portable, info.Kind);
    }

    [Fact]
    public void A_Path_That_Only_Looks_Like_The_Install_Directory_Is_Portable()
    {
        // /opt/dezirojoy-other/DeziroJoy starts with the same text but is a different tree.
        var info = Detect(processPath: "/opt/dezirojoy-other/DeziroJoy");

        Assert.Equal(InstallKind.Portable, info.Kind);
    }

    [Fact]
    public void The_Runtime_Identifier_Is_Carried_Through()
    {
        var info = Detect(isWindows: true, windowsUninstallKeyExists: true, rid: "win-arm64");

        Assert.Equal("win-arm64", info.Rid);
        Assert.True(info.HasUpdatePackage);
    }

    [Theory]
    [InlineData(InstallKind.WindowsInstall)]
    [InlineData(InstallKind.Deb)]
    [InlineData(InstallKind.Rpm)]
    [InlineData(InstallKind.AppImage)]
    public void Package_Forms_Are_The_Ones_With_Something_To_Replace(InstallKind kind)
    {
        Assert.True(new InstallKindInfo(kind, LinuxRid).HasUpdatePackage);
    }

    [Theory]
    [InlineData(InstallKind.Flatpak)]
    [InlineData(InstallKind.Portable)]
    public void Forms_Without_A_Package_Are_Reported_As_Such(InstallKind kind)
    {
        Assert.False(new InstallKindInfo(kind, LinuxRid).HasUpdatePackage);
    }

    [Fact]
    public void The_Production_Detector_Answers_With_A_Runtime_Identifier()
    {
        // Whatever this machine is, the detection must name a runtime identifier the asset
        // selector can work with - an empty one would silently break the package lookup.
        var info = InstallKindDetector.Detect();

        Assert.False(string.IsNullOrWhiteSpace(info.Rid));
    }
}
