using System.Diagnostics;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The Windows installer: launch setup.exe detached with the workflow's silent flags and hand
/// the relaunch to the setup's conditional [Run] entry. Every launch goes through the injected
/// recording launcher and the log through a known path, so no real setup.exe - and no Restart
/// Manager - is ever involved here.
/// </summary>
/// <remarks>
/// The detachment is what is under test. A GUI that redirects the setup's output and waits for
/// it keeps its own <c>DeziroJoy.exe</c> and DLLs open for the whole install, while the setup's
/// Restart Manager (<c>CloseApplications=yes</c> in DeziroJoy.iss) needs that same GUI closed to
/// replace them - the deadlock that ended in "DeleteFile failed, error 5 (access denied)" and a
/// rollback. So the launch must carry no redirection, and the install must be reported the
/// moment the setup is up, not when it finishes.
/// </remarks>
public sealed class WindowsUpdateInstallerTests : IDisposable
{
    private readonly string _log;
    private readonly RecordingLauncher _launcher;

    public WindowsUpdateInstallerTests()
    {
        _log = Path.Combine(Path.GetTempPath(), $"dezirojoy-setup-test-{Guid.NewGuid():N}.log");
        _launcher = new RecordingLauncher();
    }

    public void Dispose()
    {
        if (File.Exists(_log))
        {
            File.Delete(_log);
        }
    }

    private static UpdateInstallRequest Request(string packagePath)
        => new(
            InstallKind.WindowsInstall,
            "win-x64",
            SemanticVersion.Parse("0.0.2"),
            "v0.0.2",
            packagePath,
            "DeziroJoy-0.0.2-win-x64-setup.exe");

    private WindowsUpdateInstaller Installer() => new(_launcher, () => _log);

    [Fact]
    public async Task A_Detached_Launch_Is_A_Success_And_The_Setup_Relaunches_The_App()
    {
        var result = await Installer().InstallAsync(
            Request("C:\\updates\\DeziroJoy-0.0.2-win-x64-setup.exe"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The relaunch comes from the setup's conditional [Run] entry, not the updater: the GUI
        // exits now so it stops holding the files setup must replace.
        Assert.True(result.RelaunchHandled);

        var start = Assert.Single(_launcher.Calls);
        Assert.Equal("C:\\updates\\DeziroJoy-0.0.2-win-x64-setup.exe", start.FileName);
        Assert.Contains("/VERYSILENT", start.Arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", start.Arguments);
        Assert.Contains("/NORESTART", start.Arguments);
        Assert.Contains($"/LOG=\"{_log}\"", start.Arguments);
    }

    [Fact]
    public async Task The_Launch_Holds_No_Pipe_The_Setup_Could_Block_On()
    {
        // The blocking half of the old installer: capturing the setup's output and reading it to
        // the end is what kept this GUI alive holding its own files through the install.
        await Installer().InstallAsync(
            Request("C:\\updates\\setup.exe"),
            TestContext.Current.CancellationToken);

        var start = Assert.Single(_launcher.Calls);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public async Task The_Command_Line_Is_Passed_Verbatim_Rather_Than_Re_Quoted()
    {
        // The flags are the release workflow's verified form, quoted /LOG included. Handing them
        // over as an argument list would let the runtime re-quote the embedded quotes, which is
        // a needless way to change what Inno's own parser sees.
        await Installer().InstallAsync(
            Request("C:\\updates\\setup.exe"),
            TestContext.Current.CancellationToken);

        var start = Assert.Single(_launcher.Calls);
        Assert.Empty(start.ArgumentList);
        Assert.Contains($"/LOG=\"{_log}\"", start.Arguments);
    }

    [Fact]
    public async Task A_Setup_That_Could_Not_Start_Names_What_Failed()
    {
        _launcher.Next = false;

        var result = await Installer().InstallAsync(
            Request("C:\\updates\\setup.exe"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("无法启动", result.ErrorMessage);
        Assert.Contains("C:\\updates\\setup.exe", result.ErrorMessage);
    }

    [Fact]
    public void It_Supports_Only_The_Windows_Install_Form()
    {
        var installer = Installer();

        Assert.True(installer.Supports(InstallKind.WindowsInstall));
        Assert.False(installer.Supports(InstallKind.Deb));
        Assert.False(installer.Supports(InstallKind.Rpm));
        Assert.False(installer.Supports(InstallKind.AppImage));
        Assert.False(installer.Supports(InstallKind.Flatpak));
        Assert.False(installer.Supports(InstallKind.Portable));
    }

    /// <summary>Records every launch; <see cref="Next"/> models one that could not be started.</summary>
    private sealed class RecordingLauncher : IUpdateProcessLauncher
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
