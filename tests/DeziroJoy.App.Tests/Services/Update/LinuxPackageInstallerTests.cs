using System.Diagnostics;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The Linux package installer: hand the whole install-and-relaunch chain to a detached shell so
/// it outlives the process being replaced (ETXTBSY), degrade an rpm system without dnf to a
/// copyable command before launching anything, and report the relaunch as handled. Both the
/// dnf probe and the detached launch are injected fakes, so no pkexec dialog and no real shell
/// ever runs.
/// </summary>
public sealed class LinuxPackageInstallerTests
{
    private const string DebPath = "/tmp/dezirojoy-update/DeziroJoy_0.0.2_amd64.deb";

    private const string RpmPath = "/tmp/dezirojoy-update/DeziroJoy-0.0.2-x86_64.rpm";

    private readonly FakeRunner _runner = new();
    private readonly FakeLauncher _launcher = new();

    private LinuxPackageInstaller Installer() => new(_runner, _launcher);

    private static UpdateInstallRequest Request(InstallKind kind, string path)
        => new(
            kind,
            "linux-x64",
            SemanticVersion.Parse("0.0.2"),
            "v0.0.2",
            path,
            Path.GetFileName(path));

    [Fact]
    public async Task The_Deb_Install_Is_Handed_To_A_Detached_Chain_That_Relaunches_The_App()
    {
        var installer = Installer();

        var result = await installer.InstallAsync(
            Request(InstallKind.Deb, DebPath),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The detached chain relaunches the app, so the host must not start a successor too.
        Assert.True(result.RelaunchHandled);

        var call = Assert.Single(_launcher.Calls);
        Assert.Equal("sh", call.FileName);
        Assert.Equal(2, call.ArgumentList.Count);
        Assert.Equal("-c", call.ArgumentList[0]);

        var command = call.ArgumentList[1];
        Assert.Contains($"pkexec apt-get install -y \"{DebPath}\"", command);
        Assert.Contains("systemctl --user start dezirojoy-daemon 2>/dev/null || true", command);
        Assert.Contains("exec /opt/dezirojoy/DeziroJoy", command);

        // The deb form never probes for dnf: apt-get is a given on a dpkg-registered machine.
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task The_Rpm_Install_Uses_Dnf_When_It_Is_Available()
    {
        _runner.Next = new ProcessResult(0, "/usr/bin/dnf", string.Empty);
        var installer = Installer();

        var result = await installer.InstallAsync(
            Request(InstallKind.Rpm, RpmPath),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The dnf availability is checked first, through the shared process seam.
        var probe = Assert.Single(_runner.Calls);
        Assert.Contains("command -v dnf", probe.Arguments);

        var command = Assert.Single(_launcher.Calls).ArgumentList[1];
        Assert.Contains($"pkexec dnf install -y \"{RpmPath}\"", command);
        Assert.Contains("exec /opt/dezirojoy/DeziroJoy", command);
    }

    [Fact]
    public async Task An_Rpm_System_Without_Dnf_Degrades_To_A_Copyable_Command()
    {
        _runner.Next = new ProcessResult(1, string.Empty, string.Empty);
        var installer = Installer();

        var result = await installer.InstallAsync(
            Request(InstallKind.Rpm, RpmPath),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);

        // Nothing is launched: a chain that would fail after this process is gone has no way to
        // report it, so the copyable command is handed over now instead.
        Assert.Empty(_launcher.Calls);
        Assert.Contains("未找到 dnf", result.ErrorMessage);
        Assert.Contains($"pkexec dnf install -y \"{RpmPath}\"", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Detached_Chain_That_Cannot_Start_Is_An_Honest_Failure()
    {
        _launcher.Next = false;
        var installer = Installer();

        var result = await installer.InstallAsync(
            Request(InstallKind.Deb, DebPath),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("无法启动安装进程", result.ErrorMessage);
    }

    [Fact]
    public void It_Supports_The_Two_Linux_Package_Forms()
    {
        var installer = Installer();

        Assert.True(installer.Supports(InstallKind.Deb));
        Assert.True(installer.Supports(InstallKind.Rpm));
        Assert.False(installer.Supports(InstallKind.WindowsInstall));
        Assert.False(installer.Supports(InstallKind.AppImage));
        Assert.False(installer.Supports(InstallKind.Flatpak));
        Assert.False(installer.Supports(InstallKind.Portable));
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<(string FileName, string Arguments)> Calls { get; } = [];

        public ProcessResult Next { get; set; } = new(0, string.Empty, string.Empty);

        public ProcessResult Run(string fileName, string arguments)
        {
            Calls.Add((fileName, arguments));
            return Next;
        }
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
