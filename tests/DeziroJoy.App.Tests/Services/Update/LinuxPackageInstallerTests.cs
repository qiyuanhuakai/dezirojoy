using System.Diagnostics;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The Linux package installer: the whole install-and-relaunch chain is handed to a detached
/// shell script that pkexec runs as root, and everything after the install - the daemon's
/// systemd user unit and the relaunched app - runs as the invoking user through
/// runuser/setpriv, with the session environment carried explicitly. The text assertions pin
/// what the generated scripts contain; the stub simulation (Linux only) executes those scripts
/// against fake pkexec / runuser / setpriv / systemctl / apt-get binaries, so the shell itself
/// is proven rather than assumed. Neither half ever reaches a real polkit dialog, a real
/// package manager or a real privilege drop.
/// </summary>
public sealed class LinuxPackageInstallerTests : IDisposable
{
    private const string DebPath = "/tmp/dezirojoy-update/DeziroJoy_0.0.2_amd64.deb";

    private const string RpmPath = "/tmp/dezirojoy-update/DeziroJoy-0.0.2-x86_64.rpm";

    private const string User = "qiyuaner";

    private const string App = "/opt/dezirojoy/DeziroJoy";

    /// <summary>A pid no process on any Linux box holds, so the chain's wait exits at once.</summary>
    private const int OldProcessId = 4194304;

    /// <summary>The session environment a desktop-launched GUI carries, minus Wayland.</summary>
    private readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal)
    {
        ["HOME"] = "/home/qiyuaner",
        ["USER"] = User,
        ["LOGNAME"] = User,
        ["XDG_CONFIG_HOME"] = "/home/qiyuaner/.config",
        ["XDG_DATA_HOME"] = "/home/qiyuaner/.local/share",
        ["XDG_RUNTIME_DIR"] = "/run/user/1000",
        ["DISPLAY"] = ":0",
        ["XAUTHORITY"] = "/home/qiyuaner/.Xauthority",
        ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus",
    };

    private readonly FakeRunner _runner = new();
    private readonly FakeLauncher _launcher = new();
    private readonly string _logDirectory;

    public LinuxPackageInstallerTests()
        => _logDirectory = Path.Combine(
            Path.GetTempPath(),
            $"dezirojoy-linux-update-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }

    private LinuxPackageInstaller Installer(
        Func<string>? userName = null,
        Func<string>? logDirectory = null,
        Func<string>? executable = null)
        => new(
            _runner,
            _launcher,
            userName ?? (static () => User),
            name => _environment.TryGetValue(name, out var value) ? value : null,
            logDirectory ?? (() => _logDirectory),
            static () => OldProcessId,
            executable ?? (static () => App));

    private static UpdateInstallRequest Request(InstallKind kind, string path)
        => new(
            kind,
            "linux-x64",
            SemanticVersion.Parse("0.0.2"),
            "v0.0.2",
            path,
            Path.GetFileName(path));

    /// <summary>The env prefix the chain is expected to carry, in the installer's own order.</summary>
    private string ExpectedEnvironment(params string[] names)
        => string.Join(' ', names.Select(name => $"'{name}={_environment[name]}'"));

    /// <summary>
    /// Runs one install through the real chain: the installer writes its script and log into
    /// the simulation's directory, and the simulation executes what the launcher recorded.
    /// </summary>
    private async Task<ChainRun> RunChainAsync(
        ChainSimulation simulation,
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        var installer = Installer(
            logDirectory: () => simulation.LogDirectory,
            executable: () => simulation.AppPath);

        var result = await installer.InstallAsync(request, cancellationToken);
        Assert.True(result.Succeeded, result.ErrorMessage);

        return await simulation.ExecuteAsync(
            Assert.Single(_launcher.Calls),
            cancellationToken);
    }

    private static readonly string[] AllEnvironmentNames =
    [
        "HOME", "USER", "LOGNAME", "XDG_CONFIG_HOME", "XDG_DATA_HOME",
        "XDG_RUNTIME_DIR", "DISPLAY", "XAUTHORITY", "DBUS_SESSION_BUS_ADDRESS",
    ];

    private string ScriptPath() => Assert.Single(Directory.GetFiles(_logDirectory, "*.sh"));

    private string LogPath() => Assert.Single(Directory.GetFiles(_logDirectory, "*.log"));

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

        // The chain script and its log are both kept, beside each other, for a user reporting
        // a failed update - exactly the shape the Windows watchdog leaves behind.
        var scriptPath = ScriptPath();
        var logPath = LogPath();
        Assert.Contains("dezirojoy-update-", Path.GetFileName(scriptPath));
        Assert.Equal(
            Path.GetFileNameWithoutExtension(scriptPath),
            Path.GetFileNameWithoutExtension(logPath));

        var call = Assert.Single(_launcher.Calls);
        Assert.Equal("sh", call.FileName);
        Assert.Equal(2, call.ArgumentList.Count);
        Assert.Equal("-c", call.ArgumentList[0]);

        var outer = call.ArgumentList[1];
        var script = File.ReadAllText(scriptPath);

        // One pkexec for the whole root side, so one authentication covers the install, the
        // user-unit work and the relaunch; the script it runs is the file the installer wrote.
        Assert.StartsWith("pkexec ", outer, StringComparison.Ordinal);
        Assert.Contains($"pkexec '{scriptPath}'", outer, StringComparison.Ordinal);

        // The root half: the package manager runs as root, with the package path single-quote
        // escaped so nothing a path can carry is parsed as shell.
        Assert.Contains($"pkg='{DebPath}'", script, StringComparison.Ordinal);
        Assert.Contains("out=$(apt-get install -y \"$pkg\" 2>&1)", script, StringComparison.Ordinal);

        // The user half: both the unit and the relaunch go through the same privilege drop,
        // as the invoking user, with the session environment carried explicitly - pkexec,
        // runuser and setpriv all reset it.
        var environment = ExpectedEnvironment(AllEnvironmentNames);
        Assert.Contains($"user={LinuxPackageInstaller.ShellQuote(User)}", script, StringComparison.Ordinal);
        Assert.Contains("as_user() { runuser -u \"$user\" -- \"$@\"; }", script, StringComparison.Ordinal);
        Assert.Contains("as_user() { setpriv --reuid \"$uid\" --regid \"$gid\" --init-groups -- \"$@\"; }", script, StringComparison.Ordinal);
        Assert.Contains(
            $"as_user env {environment} systemctl --user start \"$unit\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains($"as_user env {environment} \"$app\" &", script, StringComparison.Ordinal);

        // The old process is gone before the successor starts: the two arbitrate for the
        // instance lease, and the earlier one wins.
        Assert.Contains($"kill -0 {OldProcessId}", script, StringComparison.Ordinal);

        // The outer half lands every way the root side can fail without leaving the user
        // without an app: as the invoking user, which this shell already is.
        Assert.Contains(
            $"exec env {environment} {LinuxPackageInstaller.ShellQuote(App)}",
            outer,
            StringComparison.Ordinal);

        // The deb form never probes for dnf: apt-get is a given on a dpkg-registered machine.
        Assert.Empty(_runner.Calls);

        // The log names the user, the package and the script that ran.
        var log = File.ReadAllText(logPath);
        Assert.Contains(User, log, StringComparison.Ordinal);
        Assert.Contains(DebPath, log, StringComparison.Ordinal);
        Assert.Contains(scriptPath, log, StringComparison.Ordinal);
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

        var script = File.ReadAllText(ScriptPath());
        Assert.Contains($"pkg='{RpmPath}'", script, StringComparison.Ordinal);
        Assert.Contains("out=$(dnf install -y \"$pkg\" 2>&1)", script, StringComparison.Ordinal);
        Assert.Contains("as_user env ", script, StringComparison.Ordinal);
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

        // Nothing is launched and nothing is written: a chain that would fail after this
        // process is gone has no way to report it, so the copyable command is handed over now.
        Assert.Empty(_launcher.Calls);
        Assert.False(Directory.Exists(_logDirectory));
        Assert.Contains("未找到 dnf", result.ErrorMessage);
        Assert.Contains($"pkexec dnf install -y '{RpmPath}'", result.ErrorMessage);
    }

    [Fact]
    public async Task A_User_Name_That_Cannot_Be_Determined_Degrades_To_A_Copyable_Command()
    {
        var installer = Installer(userName: static () => string.Empty);

        var result = await installer.InstallAsync(
            Request(InstallKind.Deb, DebPath),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(_launcher.Calls);
        Assert.False(Directory.Exists(_logDirectory));
        Assert.Contains("无法确定当前用户身份", result.ErrorMessage);
        Assert.Contains($"pkexec apt-get install -y '{DebPath}'", result.ErrorMessage);
    }

    [Fact]
    public async Task A_Chain_That_Cannot_Be_Prepared_Fails_Before_Anything_Is_Launched()
    {
        // A file where the log directory belongs: nothing was stopped or closed, so the
        // failure lands before the launch rather than after it.
        var blocked = Path.Combine(Path.GetTempPath(), $"dezirojoy-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(blocked, "not a directory");
        var installer = Installer(logDirectory: () => blocked);

        try
        {
            var result = await installer.InstallAsync(
                Request(InstallKind.Deb, DebPath),
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Empty(_launcher.Calls);
            Assert.Contains("无法准备 Linux 更新进程", result.ErrorMessage);
        }
        finally
        {
            File.Delete(blocked);
        }
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
    public async Task A_Package_Path_With_Shell_Metacharacters_Is_Escaped_Into_The_Chain()
    {
        var hostile = "/tmp/dezirojoy-update/a b'c$(id)`d.deb";
        var installer = Installer();

        var result = await installer.InstallAsync(
            Request(InstallKind.Deb, hostile),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        // The escaped form is the whole defense: close, escape and reopen the single-quoted
        // string around the embedded quote, so the rest of the value stays literal.
        var script = File.ReadAllText(ScriptPath());
        Assert.Contains(
            $"pkg='/tmp/dezirojoy-update/a b'\\''c$(id)`d.deb'",
            script,
            StringComparison.Ordinal);
        Assert.Contains($"apt-get install -y \"$pkg\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_The_Session_Variables_That_Are_Set_Are_Carried()
    {
        _environment.Remove("DISPLAY");
        _environment.Remove("XAUTHORITY");
        _environment.Remove("WAYLAND_DISPLAY");
        var installer = Installer();

        var result = await installer.InstallAsync(
            Request(InstallKind.Deb, DebPath),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);

        var script = File.ReadAllText(ScriptPath());

        // An unset variable is left out rather than passed empty: a headless session's chain
        // must not hand the relaunched app a DISPLAY= that resolves to nothing.
        Assert.DoesNotContain("DISPLAY=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("XAUTHORITY=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("WAYLAND_DISPLAY=", script, StringComparison.Ordinal);
        Assert.Contains(
            $"as_user env {ExpectedEnvironment("HOME", "USER", "LOGNAME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS")} systemctl --user start",
            script,
            StringComparison.Ordinal);
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

    [Fact]
    public void The_Log_Directory_Resolves_Like_The_Apps_Own_Config_Directory()
    {
        var withConfigHome = LinuxPackageInstaller.ResolveLogDirectory(name => name switch
        {
            "XDG_CONFIG_HOME" => "/data/config",
            _ => null,
        });
        Assert.Equal(
            Path.Combine("/data/config", "DeziroJoy", "UpdateLogs"),
            withConfigHome);

        var withHome = LinuxPackageInstaller.ResolveLogDirectory(name => name switch
        {
            "HOME" => "/home/qiyuaner",
            _ => null,
        });
        Assert.Equal(
            Path.Combine("/home/qiyuaner", ".config", "DeziroJoy", "UpdateLogs"),
            withHome);

        var withNothing = LinuxPackageInstaller.ResolveLogDirectory(static _ => null);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "DeziroJoy", "UpdateLogs"),
            withNothing);
    }

    [Fact]
    public async Task The_Generated_Chain_Installs_As_Root_Then_Restarts_The_Unit_And_The_App_As_The_User()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        using var simulation = new ChainSimulation();
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, DebPath), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);

        // The package manager ran as root with the package path intact.
        Assert.Equal(
            ["install", "-y", DebPath],
            run.Arguments("apt-get"));

        // Both user-side steps went through the drop, as the invoking user, with the session
        // environment in front of the command.
        Assert.Equal(
            ["-u", User, "--", "env", .. ChainSimulation.ExpectedEnvironment(), "systemctl", "--user", "start", "dezirojoy-daemon"],
            run.Invocation("runuser", 0));
        Assert.Equal(
            ["-u", User, "--", "env", .. ChainSimulation.ExpectedEnvironment(), simulation.AppPath],
            run.Invocation("runuser", 1));

        // The relaunched app saw the session environment, not root's.
        Assert.Equal(":0", run.AppEnvironment["DISPLAY"]);
        Assert.Equal("/home/qiyuaner", run.AppEnvironment["HOME"]);
        Assert.Equal(User, run.AppEnvironment["USER"]);
        Assert.Equal("unix:path=/run/user/1000/bus", run.AppEnvironment["DBUS_SESSION_BUS_ADDRESS"]);
        Assert.Equal("/run/user/1000", run.AppEnvironment["XDG_RUNTIME_DIR"]);

        // The log records the whole sequence with exit codes.
        Assert.Contains("apt-get install -y 退出码 0", run.Log, StringComparison.Ordinal);
        Assert.Contains("systemctl --user start dezirojoy-daemon（以用户 qiyuaner 身份）退出码 0", run.Log, StringComparison.Ordinal);
        Assert.Contains($"已以用户 qiyuaner 身份重启 {simulation.AppPath}", run.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Missing_User_Unit_Never_Blocks_The_Relaunch()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        // systemd's own answer for a unit nobody installed: non-zero, and the relaunch has to
        // happen anyway - the relaunched GUI's supervisor starts the daemon either way.
        using var simulation = new ChainSimulation(systemCtlExit: 4);
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, DebPath), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains(
            "systemctl --user start dezirojoy-daemon（以用户 qiyuaner 身份）退出码 4",
            run.Log,
            StringComparison.Ordinal);
        Assert.Contains($"已以用户 qiyuaner 身份重启 {simulation.AppPath}", run.Log, StringComparison.Ordinal);
        Assert.Contains("app", run.Calls);
    }

    [Fact]
    public async Task A_Failed_Install_Is_Logged_And_The_App_Is_Restarted_Anyway()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        using var simulation = new ChainSimulation(installExit: 1);
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, DebPath), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);

        // The install failed, so the old version is what comes back - but it comes back, and
        // the log says why the update did not.
        Assert.Contains("apt-get install -y 退出码 1", run.Log, StringComparison.Ordinal);
        Assert.Contains("安装失败：保留现有版本，仍以原用户身份重启应用", run.Log, StringComparison.Ordinal);
        Assert.Contains("app", run.Calls);
    }

    [Fact]
    public async Task A_Dismissed_Polkit_Dialog_Still_Relaunches_The_App_As_The_User()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        // 126 is pkexec's own exit code for a dismissed authentication dialog. The root half
        // never ran, so the user has to get an app back - the old version, from the outer
        // wrapper, which already runs as the invoking user.
        using var simulation = new ChainSimulation(pkExecExit: 126);
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, DebPath), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("apt-get", run.Calls);
        Assert.Contains("更新链未完成（退出码 126", run.Log, StringComparison.Ordinal);
        Assert.Contains("app", run.Calls);
        Assert.Equal("/home/qiyuaner", run.AppEnvironment["HOME"]);
        Assert.Equal(":0", run.AppEnvironment["DISPLAY"]);
    }

    [Fact]
    public async Task A_Chain_Without_A_Drop_Tool_Leaves_The_Relaunch_To_The_Outer_Fallback()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        // Neither runuser nor setpriv: the root half cannot act as the user, so it says so in
        // the log and exits non-zero, and the outer wrapper - which is the user already -
        // performs the relaunch itself.
        using var simulation = new ChainSimulation(withRunUser: false, withSetPriv: false);
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, DebPath), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("runuser 与 setpriv 均不可用", run.Log, StringComparison.Ordinal);
        Assert.Equal(["install", "-y", DebPath], run.Arguments("apt-get"));
        Assert.DoesNotContain("systemctl", run.Calls);
        Assert.Contains("app", run.Calls);
        Assert.Equal("/home/qiyuaner", run.AppEnvironment["HOME"]);
    }

    [Fact]
    public async Task The_SetPriv_Fallback_Switches_To_The_User_With_Its_Groups()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        // A machine with setpriv but no runuser: the drop resolves the uid and gid and
        // initializes the supplementary groups, so the user's input group survives.
        using var simulation = new ChainSimulation(withRunUser: false);
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, DebPath), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);

        var drop = run.Invocation("setpriv", 0);
        Assert.Contains("--reuid", drop);
        Assert.Contains("--regid", drop);
        Assert.Contains("--init-groups", drop);
        Assert.Contains("systemctl", drop);
        Assert.Contains("app", run.Calls);
        Assert.Equal("/home/qiyuaner", run.AppEnvironment["HOME"]);
    }

    [Fact]
    public async Task A_Package_Path_With_Metacharacters_Reaches_The_Installer_Intact()
    {
        Assert.SkipWhen(!OperatingSystem.IsLinux(), "the generated chain is a POSIX shell script");

        var hostile = "/tmp/dezirojoy-update/a b'c$(id)`d.deb";
        using var simulation = new ChainSimulation();
        var run = await RunChainAsync(simulation, Request(InstallKind.Deb, hostile), TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);

        // The stub saw exactly one argument with exactly that value: the quoting held, and
        // nothing in the value ran.
        Assert.Equal(["install", "-y", hostile], run.Arguments("apt-get"));
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
