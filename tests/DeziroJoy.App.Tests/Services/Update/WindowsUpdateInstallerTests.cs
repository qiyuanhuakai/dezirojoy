using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The Windows installer: launch setup.exe with the workflow's silent flags, treat a non-zero
/// exit as a failure with the log tail, and hand the relaunch to the setup's conditional [Run]
/// entry. Every process goes through the injected fake runner and the log through a known path,
/// so no real setup.exe runs here.
/// </summary>
public sealed class WindowsUpdateInstallerTests : IDisposable
{
    private readonly string _log;
    private readonly FakeRunner _runner;

    public WindowsUpdateInstallerTests()
    {
        _log = Path.Combine(Path.GetTempPath(), $"dezirojoy-setup-test-{Guid.NewGuid():N}.log");
        _runner = new FakeRunner();
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

    [Fact]
    public async Task A_Clean_Exit_Is_A_Success_And_The_Setup_Relaunches_The_App()
    {
        _runner.Next = new ProcessResult(0, string.Empty, string.Empty);
        var installer = new WindowsUpdateInstaller(_runner, () => _log);

        var result = await installer.InstallAsync(
            Request("C:\\updates\\DeziroJoy-0.0.2-win-x64-setup.exe"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The relaunch comes from the setup's conditional [Run] entry, not the updater: the
        // Restart Manager closes the updater mid-install.
        Assert.True(result.RelaunchHandled);

        var call = Assert.Single(_runner.Calls);
        Assert.Equal("C:\\updates\\DeziroJoy-0.0.2-win-x64-setup.exe", call.FileName);
        Assert.Contains("/VERYSILENT", call.Arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", call.Arguments);
        Assert.Contains("/NORESTART", call.Arguments);
        Assert.Contains($"/LOG=\"{_log}\"", call.Arguments);
    }

    [Fact]
    public async Task A_Non_Zero_Exit_Is_A_Failure_With_The_Log_Tail()
    {
        File.WriteAllText(_log, "lots of preamble\n...\nError: Could not copy files. Access denied.\n");
        _runner.Next = new ProcessResult(2, string.Empty, string.Empty);
        var installer = new WindowsUpdateInstaller(_runner, () => _log);

        var result = await installer.InstallAsync(
            Request("C:\\updates\\setup.exe"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("退出码 2", result.ErrorMessage);
        Assert.Contains("Could not copy files", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Failure_Without_A_Log_Still_Reports_The_Exit_Code()
    {
        _runner.Next = new ProcessResult(3, string.Empty, string.Empty);
        var installer = new WindowsUpdateInstaller(_runner, () => _log);

        var result = await installer.InstallAsync(
            Request("C:\\updates\\setup.exe"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("退出码 3", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Setup_That_Never_Started_Names_The_Reason()
    {
        _runner.Next = new ProcessResult(-1, string.Empty, "No such file or directory");
        var installer = new WindowsUpdateInstaller(_runner, () => _log);

        var result = await installer.InstallAsync(
            Request("C:\\updates\\setup.exe"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("无法启动", result.ErrorMessage);
        Assert.Contains("No such file or directory", result.ErrorMessage);
    }

    [Fact]
    public void It_Supports_Only_The_Windows_Install_Form()
    {
        var installer = new WindowsUpdateInstaller(_runner, () => _log);

        Assert.True(installer.Supports(InstallKind.WindowsInstall));
        Assert.False(installer.Supports(InstallKind.Deb));
        Assert.False(installer.Supports(InstallKind.Rpm));
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
}
