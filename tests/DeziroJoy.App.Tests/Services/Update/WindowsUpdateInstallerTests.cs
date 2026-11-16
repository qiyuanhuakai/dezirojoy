using System.Diagnostics;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The Windows installer: the install is handed to an external PowerShell watchdog that
/// outlives this process, and every value it needs is an argument rather than part of the
/// script. Every launch goes through the injected recording launcher and the script lands in
/// a known directory, so no real setup.exe, no Restart Manager and no PowerShell is ever
/// involved here.
/// </summary>
/// <remarks>
/// What is under test is the shape of the handover, because that is what alpha.2 got wrong.
/// A GUI that ran the setup itself kept its own <c>DeziroJoy.exe</c> and the libraries beside
/// it open for the whole install, while the setup's Restart Manager
/// (<c>CloseApplications=yes</c> in DeziroJoy.iss) needed that GUI closed to replace them -
/// the access-denied rollback - and a relaunch delegated to the setup's own
/// <c>[Run]</c> entry fired at a moment the old GUI could still hold the instance lease, so
/// the successor lost the arbitration and exited again with nothing on screen to say so. The
/// watchdog therefore has to be a separate process, it has to wait for this one to be gone,
/// it has to read the setup's exit code itself, and it has to report a failure through a log
/// it opens, because the GUI that could have shown it is already gone.
/// </remarks>
public sealed class WindowsUpdateInstallerTests : IDisposable
{
    /// <summary>The pid the watchdog is told to wait for, injected so the launch is assertable.</summary>
    private const int OldProcessId = 4242;

    private readonly string _logDirectory;
    private readonly string _installDirectory;
    private readonly RecordingLauncher _launcher;

    public WindowsUpdateInstallerTests()
    {
        _logDirectory = Path.Combine(
            Path.GetTempPath(), "dezirojoy-watchdog-tests", Guid.NewGuid().ToString("N"));
        _installDirectory = Path.Combine(
            Path.GetTempPath(), "dezirojoy-install-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_installDirectory);
        _launcher = new RecordingLauncher();
    }

    public void Dispose()
    {
        TryDelete(_logDirectory);
        TryDelete(_installDirectory);
        GC.SuppressFinalize(this);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string SetupPath = "C:\\updates\\DeziroJoy-0.0.3-win-x64-setup.exe";

    private static UpdateInstallRequest Request(string packagePath) => new(
        InstallKind.WindowsInstall,
        "win-x64",
        SemanticVersion.Parse("0.0.3"),
        "v0.0.3",
        packagePath,
        Path.GetFileName(packagePath));

    private WindowsUpdateInstaller Installer(
        Func<string>? logDirectory = null,
        Func<string>? installDirectory = null)
        => new(
            _launcher,
            () => OldProcessId,
            installDirectory ?? (() => _installDirectory),
            logDirectory ?? (() => _logDirectory));

    /// <summary>The one script this attempt wrote; the launch's <c>-File</c> argument names it.</summary>
    private string WrittenScript() => File.ReadAllText(Assert.Single(
        Directory.GetFiles(_logDirectory, "*.ps1")));

    /// <summary>The value the launch handed over behind <paramref name="parameter"/>.</summary>
    private static string ValueOf(ProcessStartInfo start, string parameter)
    {
        var index = start.ArgumentList.IndexOf(parameter);
        Assert.True(index >= 0, $"the launch passes {parameter}");
        Assert.True(index + 1 < start.ArgumentList.Count, $"{parameter} carries a value");
        return start.ArgumentList[index + 1];
    }

    [Fact]
    public async Task The_Install_Is_Handed_To_A_Detached_Process_Rather_Than_Run_Here()
    {
        var result = await Installer().InstallAsync(
            Request(SetupPath),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The watchdog restarts the app once it has seen exit code 0 itself, so the host
        // starts no successor - and, more importantly, must not wait for the install.
        Assert.True(result.RelaunchHandled);

        var start = Assert.Single(_launcher.Calls);

        // The setup is not started by this process: a GUI that waited on it kept the files
        // the setup was about to replace locked.
        Assert.NotEqual(SetupPath, start.FileName);
        Assert.Equal("powershell.exe", Path.GetFileName(start.FileName));
        Assert.Contains(ValueOf(start, "-File"), start.ArgumentList);

        // Detached: no pipe the watchdog could block on, no window, no shell.
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.False(start.RedirectStandardInput);
    }

    [Fact]
    public async Task The_Watchdog_Runs_The_Operating_Systems_PowerShell_And_Loads_Nothing_From_The_Install_Directory()
    {
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var start = Assert.Single(_launcher.Calls);

        // System32's own PowerShell, never a copy beside the app: a watchdog that ran from
        // the install directory would hold the very DLLs the setup has to replace.
        Assert.Equal("powershell.exe", Path.GetFileName(start.FileName));
        Assert.DoesNotContain(_installDirectory, start.FileName);
        Assert.NotEqual(_installDirectory, start.WorkingDirectory);

        // ...and the script it runs loads no assembly from anywhere: it starts processes and
        // writes a log, nothing else.
        var script = WrittenScript();
        Assert.DoesNotContain("Add-Type", script);
        Assert.DoesNotContain("Reflection.Assembly", script);
    }

    [Fact]
    public async Task The_Watchdog_Waits_For_This_Process_Then_Runs_The_Setup_With_A_Visible_Progress_Window()
    {
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var script = WrittenScript();

        // The old GUI has to be gone before the setup starts: it holds the install
        // directory's files open, and a successor started while it still lives loses the
        // instance arbitration and exits again.
        Assert.Contains("Get-Process -Id $OldProcessId", script);

        // The setup is waited on, so its exit code decides the outcome instead of a launch
        // that reports success the moment the process exists.
        Assert.Contains("WaitForExit", script);
        Assert.Contains("$setup.ExitCode", script);

        // /SILENT, never the fully silent flag: once this GUI is gone the progress window is
        // the only feedback the user has, and a silent install looks exactly like a hung
        // one. The quoted forms are the entries of the setup's own argument list.
        Assert.Contains("'/SILENT'", script);
        Assert.DoesNotContain("'/VERYSILENT'", script);
        Assert.Contains("'/NORESTART'", script);

        // The Inno log and the install directory are both named, so a failure can be
        // diagnosed and the setup never picks a directory of its own.
        Assert.Contains("/LOG=", script);
        Assert.Contains("/DIR=", script);
    }

    [Fact]
    public async Task The_Setup_Log_And_Directory_Arguments_Carry_Their_Own_Quotes()
    {
        // Windows PowerShell 5.1 joins -ArgumentList entries with spaces and quotes nothing
        // itself, and both values sit under a per-user path that contains a space on any
        // machine whose profile name does (C:\Users\John Doe\AppData\Local\...). The script
        // has to hand the setup /LOG="..." and /DIR="..." itself, or the log path is truncated
        // at the first space and the setup writes its log where nobody looks.
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var script = WrittenScript();

        // The backtick-escaped quotes are PowerShell string syntax, so the entries the array
        // is built from really are /LOG="..." and /DIR="..." - not a C# escaping artifact.
        Assert.Contains("\"/LOG=`\"$SetupLog`\"\"", script);
        Assert.Contains("\"/DIR=`\"$InstallDirectory`\"\"", script);

        // ...and neither is left unquoted, which is what truncates the path.
        Assert.DoesNotContain("\"/LOG=$SetupLog\"", script);
        Assert.DoesNotContain("\"/DIR=$InstallDirectory\"", script);
    }

    [Fact]
    public async Task The_Watchdog_Restarts_The_Installed_Executable_After_Exit_Code_Zero()
    {
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var start = Assert.Single(_launcher.Calls);
        var script = WrittenScript();

        Assert.Contains("Start-Process -FilePath $AppExecutable", script);

        // The successor is the executable of the install directory - the one the setup just
        // replaced - started from there as its working directory.
        Assert.Equal(
            Path.Combine(_installDirectory, "DeziroJoy.exe"),
            ValueOf(start, WindowsUpdateWatchdog.AppExecutableParameter));
        Assert.Equal(_installDirectory, ValueOf(start, WindowsUpdateWatchdog.InstallDirectoryParameter));
        Assert.Contains("-WorkingDirectory $InstallDirectory", script);
    }

    [Fact]
    public async Task A_Failure_Is_Written_To_A_Log_The_Watchdog_Opens()
    {
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var script = WrittenScript();

        // The GUI has already exited by the time anything can fail, so the log is written
        // and opened by the process that saw the failure - a message nobody can read is not
        // a failure report.
        Assert.Contains("Out-File -FilePath $WatchdogLog", script);
        Assert.Contains("Start-Process -FilePath $WatchdogLog", script);

        // Inno's own log is folded in, because it is the only record of what the setup did.
        Assert.Contains("Append-SetupLogTail", script);
        Assert.Contains("Get-Content -LiteralPath $SetupLog", script);
    }

    [Fact]
    public async Task Every_Failure_Path_Brings_The_Existing_Version_Back_Up()
    {
        // The daemon is already stopped by the time any of these run, so a user who is left
        // with a closed app and a stopped daemon has nothing at all. Every give-up path - the
        // package missing, the setup refusing to start, a nonzero exit code, an exit code 0
        // that left no executable, and the install timing out - therefore restarts what is
        // still on disk, best effort, before it exits.
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var script = WrittenScript();

        Assert.Equal(5, CountOf(script, "Start-InstalledApp "));
        Assert.Contains("Start-InstalledApp '更新包不存在'", script);
        Assert.Contains("Start-InstalledApp '安装失败'", script);
        Assert.Contains("Start-InstalledApp '安装超时'", script);
        Assert.Contains("Start-InstalledApp '安装程序报告成功但找不到新的可执行文件'", script);
        Assert.Contains("Start-InstalledApp '守护进程异常退出'", script);

        // The timeout path folds the setup's own log in as well, so a hung install is
        // diagnosable and not just a note that it never finished.
        Assert.Contains("Append-SetupLogTail", script);
    }

    private static int CountOf(string text, string marker)
    {
        var count = 0;
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [Fact]
    public async Task Every_Value_Is_Its_Own_Argument_So_A_Path_Can_Not_Become_Script()
    {
        // A path is a path, and an update package name is not a trusted input: the download
        // cache, the user's profile and the temporary directory are all on it.
        var hostile = "C:\\updates\\DeziroJoy \"0.0.3\" & whoami; setup.exe";

        var result = await Installer().InstallAsync(
            Request(hostile),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var start = Assert.Single(_launcher.Calls);

        // The value survives as one argument, behind the parameter that names it.
        Assert.Equal(hostile, ValueOf(start, WindowsUpdateWatchdog.SetupPathParameter));

        // ...and it is nowhere in the script: nothing was concatenated into the text
        // PowerShell is about to run.
        var script = WrittenScript();
        Assert.DoesNotContain(hostile, script);
        Assert.DoesNotContain("whoami", script);

        // The whole command line is the runtime's quoting, not this code's.
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public async Task The_Script_Declares_Every_Parameter_The_Launch_Passes()
    {
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var start = Assert.Single(_launcher.Calls);
        var script = WrittenScript();

        var parameters = new[]
        {
            WindowsUpdateWatchdog.SetupPathParameter,
            WindowsUpdateWatchdog.InstallDirectoryParameter,
            WindowsUpdateWatchdog.AppExecutableParameter,
            WindowsUpdateWatchdog.SetupLogParameter,
            WindowsUpdateWatchdog.WatchdogLogParameter,
            WindowsUpdateWatchdog.OldProcessIdParameter,
        };

        foreach (var parameter in parameters)
        {
            // Every parameter the launch passes has to be declared by the script, or
            // PowerShell would drop the value on the floor.
            Assert.Contains(parameter, start.ArgumentList);
            Assert.Contains($"${parameter.TrimStart('-')}", script);
        }

        // The old process id is the one this GUI reports, so the watchdog waits for this
        // process rather than for whatever happens to be called DeziroJoy.
        Assert.Equal(
            OldProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ValueOf(start, WindowsUpdateWatchdog.OldProcessIdParameter));
    }

    [Fact]
    public async Task The_Script_The_Setup_Log_And_The_Watchdog_Log_Share_One_Stamp_In_One_Directory()
    {
        await Installer().InstallAsync(Request(SetupPath), TestContext.Current.CancellationToken);

        var start = Assert.Single(_launcher.Calls);
        var scriptPath = ValueOf(start, "-File");
        var setupLog = ValueOf(start, WindowsUpdateWatchdog.SetupLogParameter);
        var watchdogLog = ValueOf(start, WindowsUpdateWatchdog.WatchdogLogParameter);

        var stamp = Path.GetFileNameWithoutExtension(scriptPath)
            .Replace($"{WindowsUpdateWatchdog.ScriptFileNamePrefix}-", string.Empty);

        // One attempt, one stamp, one directory: a user - or a developer reading a report -
        // finds all three together, and none of them is written into the install directory
        // the next install would replace.
        Assert.Equal(_logDirectory, Path.GetDirectoryName(scriptPath));
        Assert.Equal(
            $"{WindowsUpdateWatchdog.SetupLogPrefix}-{stamp}.log",
            Path.GetFileName(setupLog));
        Assert.Equal(
            $"{WindowsUpdateWatchdog.WatchdogLogPrefix}-{stamp}.log",
            Path.GetFileName(watchdogLog));
        Assert.Equal(_logDirectory, Path.GetDirectoryName(setupLog));
        Assert.Equal(_logDirectory, Path.GetDirectoryName(watchdogLog));
    }

    [Fact]
    public async Task A_Watchdog_That_Could_Not_Start_Names_What_Failed()
    {
        _launcher.Next = false;

        var result = await Installer().InstallAsync(
            Request(SetupPath),
            TestContext.Current.CancellationToken);

        // Nothing has been stopped and nothing has closed yet, so this is a refusal the user
        // can still act on - and it names the package it could not run.
        Assert.False(result.Succeeded);
        Assert.Contains("无法启动", result.ErrorMessage);
        Assert.Contains(SetupPath, result.ErrorMessage);
    }

    [Fact]
    public async Task A_Log_Directory_That_Cannot_Be_Prepared_Fails_Before_Anything_Is_Stopped()
    {
        // A file where the log directory belongs: the honest answer is a refusal, because the
        // watchdog cannot report a failure it has nowhere to write.
        var blocked = Path.Combine(_logDirectory, "not-a-directory");
        File.WriteAllText(blocked, "not a directory");

        var result = await Installer(logDirectory: () => Path.Combine(blocked, "logs")).InstallAsync(
            Request(SetupPath),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("无法准备", result.ErrorMessage);
        Assert.Empty(_launcher.Calls);
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
