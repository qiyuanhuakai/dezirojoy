using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DeziroJoy.App.Services;

/// <summary>
/// Installs a .deb or .rpm update on Linux through a detached chain that outlives the GUI
/// being restarted. The daemon is deliberately left running: replacing package files does
/// not require stopping processes that still map the old inode. The chain has two halves
/// with two different identities, and keeping them apart is the whole point:
/// <list type="number">
/// <item>The package manager runs as root - <c>pkexec</c> runs the generated chain script,
/// so one authentication covers the install, the user-unit work and the relaunch.</item>
/// <item>Everything after the install - the daemon's systemd <em>user</em> unit and the
/// relaunched app - runs as the invoking user, through <c>runuser</c> (or
/// <c>setpriv</c>), with the session environment carried explicitly.</item>
/// </list>
/// This installer reports success the moment the chain is launched and
/// <see cref="UpdateInstallResult.RelaunchHandled"/> is true, because the chain - not the
/// host - restarts the app.
/// </summary>
/// <remarks>
/// <para>
/// Why the second half must drop back to the invoking user. Both privilege tools reset the
/// environment: pkexec hands the program "a minimal known and safe environment" in which
/// DISPLAY and XAUTHORITY are not set at all (its own man page), and runuser/setpriv do not
/// carry the caller's variables either. A <c>systemctl --user</c> without
/// DBUS_SESSION_BUS_ADDRESS/XDG_RUNTIME_DIR cannot reach the user's bus, and an app without
/// DISPLAY/XAUTHORITY cannot open a window - the update then "succeeds" while leaving the
/// user with a root-context unit that does not exist, root's config directory, and no GUI.
/// So HOME/USER/LOGNAME, the XDG paths and the display/session variables travel with the
/// command through <c>env</c>, and every value is single-quote escaped on the way in.
/// </para>
/// <para>
/// The unit re-engagement is deliberately not a gate: <c>systemctl --user start</c> answers
/// non-zero for a machine that never installed the unit, and the relaunched GUI's own
/// supervisor starts the daemon either way, so the unit step is logged and never blocks.
/// </para>
/// <para>
/// The chain keeps two artifacts beside each other under the per-user
/// <c>UpdateLogs</c> directory: the generated <c>.sh</c> (what actually ran, like the
/// Windows watchdog's script) and the <c>.log</c> it appends to. A failure after this
/// process is gone - a dismissed polkit dialog, a failed install, a missing drop tool - is
/// recorded there, and the outer wrapper relaunches the app as the invoking user anyway, so
/// the user is never left without one. The rpm path degrades to a copyable command before
/// launching anything, exactly the way <see cref="DeviceAccessFixer"/> degrades: a chain
/// that fails after this process is gone has no way to report it.
/// </para>
/// </remarks>
public sealed class LinuxPackageInstaller : IUpdateInstaller
{
    /// <summary>Where the .deb and the .rpm both install the application; the relaunch targets it.</summary>
    private const string InstallDirectory = "/opt/dezirojoy";

    private const string ExecutableName = "DeziroJoy";

    /// <summary>
    /// The daemon's systemd user unit, started without its <c>.service</c> suffix (systemctl adds
    /// it). It re-engages the unit when the user opted into the resident daemon and is a no-op
    /// otherwise: a missing unit, or a user bus that cannot be reached, is logged and never
    /// blocks the relaunch - the relaunched GUI's own supervisor starts the daemon either way.
    /// </summary>
    private const string DaemonUnit = "dezirojoy-daemon";

    /// <summary>The per-user directory the chain's script and log are kept in.</summary>
    private const string ApplicationDirectoryName = "DeziroJoy";

    /// <summary>The subdirectory of the per-user config directory the chain artifacts live in.</summary>
    private const string LogDirectoryName = "UpdateLogs";

    /// <summary>Prefix of the chain script and its log, so one attempt's files sort together.</summary>
    private const string ArtifactPrefix = "dezirojoy-update";

    /// <summary>Pause between the chain's "is the old process gone?" polls.</summary>
    private const string OldProcessPollPause = "0.2";

    /// <summary>
    /// The environment the user context needs and the root context does not have. Only the
    /// variables that are actually set travel; the rest of the caller's environment is not
    /// inherited past pkexec anyway, so an absent variable is simply absent.
    /// </summary>
    private static readonly string[] PreservedVariables =
    [
        "HOME",
        "USER",
        "LOGNAME",
        "XDG_CONFIG_HOME",
        "XDG_DATA_HOME",
        "XDG_RUNTIME_DIR",
        "DISPLAY",
        "XAUTHORITY",
        "WAYLAND_DISPLAY",
        "DBUS_SESSION_BUS_ADDRESS",
    ];

    /// <summary>The mode the chain script needs: it is executed through pkexec.</summary>
    private static readonly UnixFileMode ExecutableScriptMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly IProcessRunner _process;
    private readonly IUpdateProcessLauncher _launcher;
    private readonly Func<string> _userName;
    private readonly Func<string, string?> _environmentVariable;
    private readonly Func<string> _logDirectory;
    private readonly Func<int> _processId;
    private readonly Func<string> _executable;

    /// <summary>
    /// Internal so the seams stay an implementation detail: production and the update host
    /// use the defaults - the real launcher, this process, this user, this session - while
    /// tests inject a recording launcher, a known user, a fixture environment and known
    /// paths, so no pkexec dialog and no real shell is ever involved.
    /// </summary>
    internal LinuxPackageInstaller(
        IProcessRunner? process = null,
        IUpdateProcessLauncher? launcher = null,
        Func<string>? userName = null,
        Func<string, string?>? environmentVariable = null,
        Func<string>? logDirectory = null,
        Func<int>? processId = null,
        Func<string>? executable = null)
    {
        _process = process ?? new ProcessRunner();
        _launcher = launcher ?? new UpdateProcessLauncher();
        _userName = userName ?? (static () => Environment.UserName);
        _environmentVariable = environmentVariable ?? Environment.GetEnvironmentVariable;
        _logDirectory = logDirectory ?? (static () => ResolveLogDirectory());
        _processId = processId ?? (static () => Environment.ProcessId);
        _executable = executable ?? (static () => $"{InstallDirectory}/{ExecutableName}");
    }

    public bool Supports(InstallKind kind) => kind is InstallKind.Deb or InstallKind.Rpm;

    public Task<UpdateInstallResult> InstallAsync(
        UpdateInstallRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Kind == InstallKind.Rpm && !DnfIsAvailable())
        {
            // An rpm system without dnf (a machine the package manager never made it onto): the
            // detached chain's pkexec dnf would fail silently once this process is gone, so the
            // copyable command is handed over now instead.
            return Task.FromResult(UpdateInstallResult.Failed(
                $"未找到 dnf，无法自动安装 rpm 更新。请手动执行：pkexec dnf install -y {ShellQuote(request.PackagePath)}"));
        }

        // The user the chain has to drop back to after the install: the one running this GUI,
        // whose unit, config and desktop session the relaunch has to land in.
        var user = _userName();
        if (string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(UpdateInstallResult.Failed(
                $"无法确定当前用户身份，更新链无法以原用户身份重启应用。请手动执行：pkexec {PackageManager(request)} install -y {ShellQuote(request.PackagePath)}，然后重新打开 DeziroJoy。"));
        }

        var installer = PackageManager(request);

        // Resolved once, so the two halves of the chain can never disagree about which
        // process to wait for or which binary to relaunch.
        var executable = _executable();
        var processId = _processId();

        string scriptPath;
        string logPath;

        try
        {
            var logDirectory = _logDirectory();
            Directory.CreateDirectory(logDirectory);

            // One stamp shared by the script and the log, so a user reporting a failed update
            // can send both and they sort together.
            var stamp = Stamp;

            // Absolute on purpose: pkexec runs the script with root's home as the working
            // directory, so a relative path would name a file that is not there.
            scriptPath = Path.GetFullPath(
                Path.Combine(logDirectory, $"{ArtifactPrefix}-{stamp}.sh"));
            logPath = Path.GetFullPath(
                Path.Combine(logDirectory, $"{ArtifactPrefix}-{stamp}.log"));

            File.WriteAllText(scriptPath, BuildScript(request, user, logPath, installer, executable, processId));

            if (OperatingSystem.IsLinux())
            {
                // pkexec executes the script directly, so the shebang needs the execute bit.
                File.SetUnixFileMode(scriptPath, ExecutableScriptMode);
            }

            File.WriteAllText(logPath, BuildLogHeader(request, user, scriptPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing has been stopped or closed yet: the package is still on disk and the
            // user is told where to get the update by hand.
            return Task.FromResult(UpdateInstallResult.Failed(
                $"无法准备 Linux 更新进程（{exception.Message}）。更新未开始，请前往发布页手动下载安装。"));
        }

        // The chain is passed to sh verbatim through ArgumentList, so the shell - not .NET's
        // argument parser - does the quoting: the single-quoted script path survives intact.
        var startInfo = new ProcessStartInfo("sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(BuildOuterScript(scriptPath, logPath, executable, processId));

        if (!_launcher.Start(startInfo))
        {
            return Task.FromResult(UpdateInstallResult.Failed(
                "无法启动安装进程，更新未开始。请前往发布页手动下载安装。"));
        }

        // From here the chain owns the rest: install, re-engage the user unit and relaunch the
        // app as the invoking user - or log why it could not and relaunch anyway.
        return Task.FromResult(UpdateInstallResult.Ok(relaunchHandled: true));
    }

    /// <summary>
    /// The root half of the chain, written to a file pkexec executes. Deliberately a fixed
    /// skeleton with single-quote-escaped values: a command line built by string
    /// concatenation is how an update becomes an injection.
    /// </summary>
    private string BuildScript(
        UpdateInstallRequest request,
        string user,
        string logPath,
        string installer,
        string executable,
        int processId)
    {
        var environment = PreservedEnvironment();
        var waitForOldProcess = WaitForOldProcess(processId);

        var lines = new List<string>
        {
            "#!/bin/sh",
            "# DeziroJoy Linux deb/rpm 更新链（由安装器生成）。root 完成安装后，用户单元操作与",
            "# 应用重启都以原用户身份执行：pkexec/runuser/setpriv 都会重置环境，故会话环境经 env",
            "# 显式携带（DISPLAY/XAUTHORITY/DBUS/XDG_* 等）。所有内联值均已单引号转义。",
            $"pkg={ShellQuote(request.PackagePath)}",
            $"user={ShellQuote(user)}",
            $"log={ShellQuote(logPath)}",
            $"app={ShellQuote(executable)}",
            $"unit={ShellQuote(DaemonUnit)}",
            string.Empty,
            "log_line() {",
            "    printf '%s\\n' \"$*\" >>\"$log\" 2>/dev/null || true",
            "}",
            string.Empty,
            "log_line '===== DeziroJoy 更新链开始 ====='",
            $"log_line \"安装：{installer} install -y（root）；目标用户：$user\"",
            string.Empty,
            "code=0",
            $"out=$({installer} install -y \"$pkg\" 2>&1) || code=$?",
            $"log_line \"{installer} install -y 退出码 $code\"",
            "[ -n \"$out\" ] && log_line \"$out\"",
            "[ \"$code\" -eq 0 ] || log_line '安装失败：保留现有版本，仍以原用户身份重启应用'",
            string.Empty,
            "# 以原用户身份执行后续步骤：优先 runuser（按用户名切换）；setpriv 需要数字 uid/gid，",
            "# 并显式初始化附加组（如 input），否则丢失用户组成员身份。",
            "switch=0",
            "if command -v runuser >/dev/null 2>&1; then",
            "    as_user() { runuser -u \"$user\" -- \"$@\"; }",
            "    switch=1",
            "elif command -v setpriv >/dev/null 2>&1; then",
            "    uid=$(id -u \"$user\" 2>/dev/null)",
            "    gid=$(id -g \"$user\" 2>/dev/null)",
            "    if [ -n \"$uid\" ] && [ -n \"$gid\" ]; then",
            "        as_user() { setpriv --reuid \"$uid\" --regid \"$gid\" --init-groups -- \"$@\"; }",
            "        switch=1",
            "    fi",
            "fi",
            string.Empty,
            "if [ \"$switch\" -ne 1 ]; then",
            "    log_line 'runuser 与 setpriv 均不可用，无法以原用户身份完成后续步骤'",
            "    exit 1",
            "fi",
            string.Empty,
            "# 用户单元：安装过才启动；单元缺失或用户总线不可达都不阻断重启。",
            $"unit_code=0",
            $"unit_out=$(as_user env {string.Join(' ', environment)} systemctl --user start \"$unit\" 2>&1) || unit_code=$?",
            "log_line \"systemctl --user start $unit（以用户 $user 身份）退出码 $unit_code\"",
            "[ -n \"$unit_out\" ] && log_line \"$unit_out\"",
            string.Empty,
            "# 旧 GUI 退出后再重启：新旧进程争用实例租约，先起者胜。",
            waitForOldProcess,
            $"as_user env {string.Join(' ', environment)} \"$app\" &",
            "log_line \"已以用户 $user 身份重启 $app（pid $!）\"",
            "log_line '===== 更新链结束 ====='",
            "exit 0",
        };

        return string.Join('\n', lines) + '\n';
    }

    /// <summary>
    /// The outer half of the chain: one pkexec for the whole root side, and the landing for
    /// every way that side can fail before it relaunches anything. A dismissed polkit dialog
    /// (pkexec exits 126) or a chain that could not drop back to the user (exit 1) must not
    /// leave the user without an app, so the relaunch happens here too - as the invoking
    /// user, which this shell already is, after the old process is gone.
    /// </summary>
    private string BuildOuterScript(string scriptPath, string logPath, string executable, int processId)
    {
        var environment = PreservedEnvironment();

        var lines = new List<string>
        {
            $"pkexec {ShellQuote(scriptPath)}",
            "code=$?",
            "if [ \"$code\" -ne 0 ]; then",
            $"    printf '更新链未完成（退出码 %s；126=取消认证对话框，127=无法提权或执行失败）。若安装未成功，以下以原用户身份重启现有版本。\\n' \"$code\" >>{ShellQuote(logPath)} 2>/dev/null",
            $"    {WaitForOldProcess(processId)}",
            $"    exec env {string.Join(' ', environment)} {ShellQuote(executable)}",
            "fi",
        };

        return string.Join('\n', lines) + '\n';
    }

    /// <summary>
    /// The wait that has to happen before a successor starts: the old GUI still holds the
    /// per-user instance lease, and a successor that loses that arbitration exits again - so
    /// the chain waits for the process it is replacing to be gone. A pid of zero or less
    /// names nothing to wait for, and polling one would hang the chain.
    /// </summary>
    private static string WaitForOldProcess(int processId)
        => processId > 0
            ? $"while kill -0 {processId.ToString(CultureInfo.InvariantCulture)} 2>/dev/null; do sleep {OldProcessPollPause}; done"
            : ":";

    /// <summary>
    /// The <c>VAR=value</c> arguments the user context is handed, single-quote escaped so no
    /// value a path or a session can carry is ever parsed as shell. Variables that are unset
    /// are left out rather than passed empty.
    /// </summary>
    private string[] PreservedEnvironment()
        => PreservedVariables
            .Select(name => (name, value: _environmentVariable(name)))
            .Where(pair => !string.IsNullOrEmpty(pair.value))
            .Select(pair => ShellQuote($"{pair.name}={pair.value}"))
            .ToArray();

    /// <summary>What the log says before the chain runs, so a report carries the facts.</summary>
    private static string BuildLogHeader(UpdateInstallRequest request, string user, string scriptPath)
    {
        var form = request.Kind == InstallKind.Rpm ? "rpm" : "deb";

        return string.Join(
            '\n',
            $"===== DeziroJoy Linux 更新链（{form}）=====",
            $"时间：{Stamp}",
            $"用户：{user}",
            $"更新包：{request.PackagePath}",
            $"更新链脚本：{scriptPath}（与日志同目录，保留供诊断）",
            string.Empty);
    }

    private static string PackageManager(UpdateInstallRequest request)
        => request.Kind == InstallKind.Rpm ? "dnf" : "apt-get";

    /// <summary>The stamp every artifact of one update attempt shares, so they sort together.</summary>
    private static string Stamp => DateTimeOffset.Now.ToString(
        "yyyyMMdd-HHmmss",
        CultureInfo.InvariantCulture);

    /// <summary>
    /// Where the chain's script and log live, resolved the way the app resolves its own
    /// config directory: <c>$XDG_CONFIG_HOME/DeziroJoy/UpdateLogs</c> when that variable is
    /// set, otherwise <c>$HOME/.config/DeziroJoy/UpdateLogs</c>. Deliberately not
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)"/>:
    /// that returns an empty string when the directory does not exist yet, which would turn
    /// the log path into a CWD-relative one. With no home directory either, the temporary
    /// directory is the fallback - which is where the update package itself already lives.
    /// </summary>
    internal static string ResolveLogDirectory(Func<string, string?>? environmentVariable = null)
    {
        var environment = environmentVariable ?? Environment.GetEnvironmentVariable;
        var configHome = environment("XDG_CONFIG_HOME");

        if (!string.IsNullOrEmpty(configHome))
        {
            return Path.Combine(configHome, ApplicationDirectoryName, LogDirectoryName);
        }

        var home = environment("HOME");

        // No home directory either: the temporary directory is the fallback, which is where
        // the update package itself already lives.
        var root = string.IsNullOrEmpty(home)
            ? Path.GetTempPath()
            : Path.Combine(home, ".config");

        return Path.Combine(root, ApplicationDirectoryName, LogDirectoryName);
    }

    /// <summary>
    /// Whether <c>dnf</c> is on PATH. Checked only for the rpm form, whose installer command is
    /// dnf; the deb form's apt-get is a given on any machine whose dpkg database reports the
    /// package installed (which is what made this an <see cref="InstallKind.Deb"/> in the first
    /// place).
    /// </summary>
    private bool DnfIsAvailable()
        => _process.Run("sh", "-c \"command -v dnf\"").ExitCode == 0;

    /// <summary>
    /// Single-quote escapes a value for a POSIX shell: the only quoting that survives every
    /// character a path or an environment value can carry, because nothing inside single
    /// quotes is expanded. An embedded single quote closes, escapes and reopens the quoted
    /// string.
    /// </summary>
    internal static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";
}
