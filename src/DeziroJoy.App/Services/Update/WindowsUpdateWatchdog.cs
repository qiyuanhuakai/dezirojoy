using System.Diagnostics;
using System.Globalization;

namespace DeziroJoy.App.Services;

/// <summary>
/// The external watchdog the Windows installer hands its install to. The watchdog is a
/// temporary Windows PowerShell script started detached from the updating GUI: it waits for
/// that GUI to be gone, runs the Inno setup with a visible progress window, reads its exit
/// code, starts the installed <c>DeziroJoy.exe</c> on success and, on every failure path,
/// writes - and opens - a diagnostic log and best-effort restarts the version that is still
/// on disk, because the daemon has already been stopped and a user must not be left with no
/// app at all.
/// </summary>
/// <remarks>
/// <para>
/// Why an external process at all. The GUI is one of the applications the setup's Restart
/// Manager closes (<c>CloseApplications=yes</c> in DeziroJoy.iss), and it holds its own
/// <c>DeziroJoy.exe</c> and the libraries beside it open for exactly as long as it lives - so
/// an updater that waited on the setup kept the very files the setup was about to replace
/// locked, and the setup's DeleteFile failed with access denied (error 5) and rolled back.
/// The install therefore has to outlive the GUI, and the only process that can do that here
/// without holding the install directory open is a separate one.
/// </para>
/// <para>
/// Why PowerShell rather than a second copy of this app. The watchdog runs
/// <c>System32\WindowsPowerShell\v1.0\powershell.exe</c> - an OS binary that is already
/// running in every session - so it loads no DLL from the install directory, keeps no handle
/// on the files the setup replaces, and cannot be confused with the app it is updating. It is
/// also gone the moment its work is done.
/// </para>
/// <para>
/// Why every value is a parameter. The script is a fixed text; the setup path, the install
/// directory, the log paths and the old process id are handed over as
/// <see cref="ProcessStartInfo.ArgumentList"/> entries, so the runtime - not this code -
/// quotes them and nothing a path can contain is ever parsed as script. A command line built
/// by string concatenation is how an update becomes an injection.
/// </para>
/// <para>
/// Why <c>/LOG</c> and <c>/DIR</c> carry their own quotes. Windows PowerShell 5.1 joins
/// <c>-ArgumentList</c> entries with spaces and adds no quoting of its own, and both values
/// live under a per-user path that contains a space on any machine whose profile name does
/// (<c>C:\Users\John Doe\AppData\Local\DeziroJoy\UpdateLogs\...</c>). The script therefore
/// builds them as <c>/LOG="..."</c> and <c>/DIR="..."</c> itself, which survives 5.1's bare
/// join and is also what a quoting newer PowerShell produces once the setup's own command-line
/// parser strips the quotes. An unquoted value would truncate the log path at the first space
/// and the setup would write its log somewhere nobody looks.
/// </para>
/// <para>
/// The script file is kept after the run, beside the two logs it writes: a user reporting a
/// failed update can send all three, and they are the only record of what the install did.
/// </para>
/// </remarks>
internal static class WindowsUpdateWatchdog
{
    /// <summary>
    /// The Windows PowerShell every Windows install carries, resolved from the system
    /// directory: never from PATH (a stray powershell.exe earlier on PATH would run the
    /// watchdog) and never from the install directory (which would lock the very files the
    /// setup is about to replace).
    /// </summary>
    internal static string PowerShellPath => Path.Combine(
        Environment.SystemDirectory,
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    /// <summary>Where the watchdog script and its logs are kept, beside each other.</summary>
    internal const string LogDirectoryName = "UpdateLogs";

    /// <summary>The subdirectory of the per-user data directory the logs live in.</summary>
    internal const string ApplicationDirectoryName = "DeziroJoy";

    /// <summary>Prefix of the watchdog script file, so one attempt's files sort together.</summary>
    internal const string ScriptFileNamePrefix = "dezirojoy-update-watchdog";

    /// <summary>Prefix of the watchdog's own log - the one a failure opens.</summary>
    internal const string WatchdogLogPrefix = "dezirojoy-update";

    /// <summary>Prefix of the Inno setup log, passed to the setup as <c>/LOG</c>.</summary>
    internal const string SetupLogPrefix = "dezirojoy-setup";

    /// <summary>The stamp every file of one update attempt shares, so they sort together.</summary>
    internal static string Stamp => DateTimeOffset.Now.ToString(
        "yyyyMMdd-HHmmss",
        CultureInfo.InvariantCulture);

    internal const string SetupPathParameter = "-SetupPath";

    internal const string InstallDirectoryParameter = "-InstallDirectory";

    internal const string AppExecutableParameter = "-AppExecutable";

    internal const string SetupLogParameter = "-SetupLog";

    internal const string WatchdogLogParameter = "-WatchdogLog";

    internal const string OldProcessIdParameter = "-OldProcessId";

    /// <summary>
    /// How long the watchdog gives the old GUI to exit before it starts the setup anyway.
    /// The GUI is asked to exit as the watchdog is launched, so this only ever covers a
    /// process that is slow to unwind; the setup's own Restart Manager closes anything still
    /// standing when the budget runs out.
    /// </summary>
    internal const int OldProcessWaitSeconds = 90;

    /// <summary>
    /// How long the setup gets to finish. It shows a progress window and needs no input, so
    /// this is a hang detector rather than a normal path: on expiry the setup is left alone
    /// and the failure is written to the log.
    /// </summary>
    internal const int InstallTimeoutSeconds = 900;

    /// <summary>
    /// The watchdog itself. Deliberately a fixed text with no interpolated value: everything
    /// the script needs arrives as a parameter (see <see cref="BuildStartInfo"/>), so the
    /// script a user's paths end up in is always the one reviewed here.
    /// </summary>
    internal const string Script =
        """
        # DeziroJoy Windows 更新守护进程（watchdog）。
        # 由 DeziroJoy.exe 在安装前生成并启动，运行的是 System32 下的 powershell.exe：它不加载
        # 安装目录中的任何 DLL，不常驻，工作结束后自行退出。所有路径都以参数传入，脚本本身不含
        # 任何来自用户环境的数据。
        param(
          [Parameter(Mandatory)][string]$SetupPath,
          [Parameter(Mandatory)][string]$InstallDirectory,
          [Parameter(Mandatory)][string]$AppExecutable,
          [Parameter(Mandatory)][string]$SetupLog,
          [Parameter(Mandatory)][string]$WatchdogLog,
          [Parameter(Mandatory)][int]$OldProcessId,
          [int]$OldProcessWaitSeconds = 90,
          [int]$InstallTimeoutSeconds = 900
        )

        $ErrorActionPreference = 'Stop'

        # 任何未被 try/catch 吃掉的终止性错误都要留下痕迹：这个脚本一旦异常退出，用户看到的就是
        # "应用关了，什么都没发生"——alpha.2 的原样。所以这里把错误写进日志并打开它。
        trap {
          Write-WatchdogLine "watchdog failed unexpectedly: $_"
          Show-WatchdogLog
          Start-InstalledApp '守护进程异常退出'
          exit 1
        }

        function Write-WatchdogLine([string]$Message) {
          # 日志写不进去时不能让脚本死掉：调用点几乎都在失败路径上，抛异常只会让"打开日志"和
          # "拉起旧版本"这些补救一步都不做。目录由 DeziroJoy 侧先建好，这里只是兜底。
          try {
            "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message |
              Out-File -FilePath $WatchdogLog -Append -Encoding utf8
          }
          catch {
          }
        }

        function Append-SetupLogTail {
          # Inno 自己的日志是这个脚本之外唯一的现场记录：把它并进诊断日志，失败才有据可查。
          if (Test-Path -LiteralPath $SetupLog) {
            Write-WatchdogLine "--- setup log (tail): $SetupLog ---"
            Get-Content -LiteralPath $SetupLog -Tail 60 | ForEach-Object { Write-WatchdogLine $_ }
            Write-WatchdogLine "--- end setup log ---"
          }
          else {
            Write-WatchdogLine "the setup produced no log at $SetupLog"
          }
        }

        function Show-WatchdogLog {
          # GUI 已经退出，没有人再能读到这个文件：失败时直接把它打开。
          try {
            Start-Process -FilePath $WatchdogLog
          }
          catch {
            Write-WatchdogLine "the diagnostic log could not be opened: $($_.Exception.Message)"
          }
        }

        function Start-InstalledApp([string]$Reason) {
          # 尽力把现有版本拉起来：安装失败时 daemon 已被停止，用户不该面对一个没有应用的桌面。
          if (-not (Test-Path -LiteralPath $AppExecutable)) {
            Write-WatchdogLine "$Reason，但 $AppExecutable 不存在，无法重新启动。"
            return
          }
          try {
            Start-Process -FilePath $AppExecutable -WorkingDirectory $InstallDirectory
            Write-WatchdogLine "$Reason；已重新启动现有版本。"
          }
          catch {
            Write-WatchdogLine "$Reason；重新启动现有版本失败：$($_.Exception.Message)"
          }
        }

        foreach ($directory in @((Split-Path -Parent $SetupLog), (Split-Path -Parent $WatchdogLog))) {
          if (-not (Test-Path -LiteralPath $directory)) {
            New-Item -ItemType Directory -Path $directory -Force | Out-Null
          }
        }
        Write-WatchdogLine "watchdog started (powershell pid $PID, script $PSCommandPath)"
        Write-WatchdogLine "setup: $SetupPath"
        Write-WatchdogLine "install directory: $InstallDirectory"
        Write-WatchdogLine "waiting for the old app (pid $OldProcessId) to exit"

        # 1) 等旧 GUI 退出。它持有安装目录里文件的句柄和单实例租约：安装程序要在这些句柄释放后
        #    才能替换文件，重启也要等租约释放，否则新进程会再次输掉仲裁而退出。
        $deadline = (Get-Date).AddSeconds($OldProcessWaitSeconds)
        while ((Get-Date) -lt $deadline) {
          if (-not (Get-Process -Id $OldProcessId -ErrorAction SilentlyContinue)) { break }
          Start-Sleep -Milliseconds 250
        }
        if (Get-Process -Id $OldProcessId -ErrorAction SilentlyContinue) {
          Write-WatchdogLine "the old app (pid $OldProcessId) is still running after $OldProcessWaitSeconds s; starting the setup anyway"
        }
        foreach ($leftover in @(Get-Process -Name 'DeziroJoy' -ErrorAction SilentlyContinue)) {
          Write-WatchdogLine "a DeziroJoy process is still running (pid $($leftover.Id)); the setup will close it"
        }

        if (-not (Test-Path -LiteralPath $SetupPath)) {
          Write-WatchdogLine "the update package is missing: $SetupPath"
          Show-WatchdogLog
          Start-InstalledApp '更新包不存在'
          exit 1
        }

        # 2) 运行安装程序。/SILENT 显示安装进度，而不是无反馈的 /VERYSILENT；/NORESTART 不让安装
        #    程序自己决定重启，重启由本脚本在确认退出码之后完成，避免与安装程序内的重启竞争。
        #
        #    /LOG 与 /DIR 的值必须自己带上双引号。Windows PowerShell 5.1 的 Start-Process
        #    -ArgumentList 只把数组元素用空格拼成一行、不替含空格的值加引号，而默认安装目录
        #    {localappdata}\Programs\DeziroJoy 和日志目录都在含空格的用户配置文件路径下
        #    （C:\Users\John Doe\...）：不加引号时安装程序只会看到 /LOG=C:\Users\John，路径被
        #    截断，日志写到别处或干脆写不出来。这里的 `" 是 PowerShell 字符串里的转义引号，
        #    生成的是 /LOG="..." 这样一个参数，5.1 的空格拼接和更新版本的自动加引号都能被
        #    安装程序的命令行解析器正确还原。
        $setupArguments = @(
          '/SILENT'
          '/SUPPRESSMSGBOXES'
          '/NORESTART'
          "/LOG=`"$SetupLog`""
          "/DIR=`"$InstallDirectory`""
        )
        Write-WatchdogLine "starting setup: $SetupPath $($setupArguments -join ' ')"

        $exitCode = -1
        try {
          $setup = Start-Process -FilePath $SetupPath -ArgumentList $setupArguments -PassThru
          if (-not $setup.WaitForExit($InstallTimeoutSeconds * 1000)) {
            # 和其他每一条失败路径一样：daemon 已经停了，把现有版本尽力拉起来，再留下日志。
            Write-WatchdogLine "the setup did not exit within $InstallTimeoutSeconds s; leaving its progress window open"
            Append-SetupLogTail
            Show-WatchdogLog
            Start-InstalledApp '安装超时'
            exit 1
          }
          $exitCode = $setup.ExitCode
        }
        catch {
          Write-WatchdogLine "the setup could not be started: $($_.Exception.Message)"
          $exitCode = -1
        }
        Write-WatchdogLine "setup exit code: $exitCode"

        if ($exitCode -ne 0) {
          Write-WatchdogLine "install failed with exit code $exitCode"
          Append-SetupLogTail
          Show-WatchdogLog
          Start-InstalledApp '安装失败'
          exit 1
        }

        # 3) 成功：确认新的可执行文件在位后启动它。旧进程已经退出，单实例仲裁这次归它。
        if (-not (Test-Path -LiteralPath $AppExecutable)) {
          Write-WatchdogLine "the setup reported success but $AppExecutable is missing"
          Append-SetupLogTail
          Show-WatchdogLog
          Start-InstalledApp '安装程序报告成功但找不到新的可执行文件'
          exit 1
        }

        try {
          $app = Start-Process -FilePath $AppExecutable -WorkingDirectory $InstallDirectory -PassThru
        }
        catch {
          Write-WatchdogLine "the new app could not be started: $($_.Exception.Message)"
          Show-WatchdogLog
          exit 1
        }

        # 新进程起来即算完成；三秒后还在，说明它没有被单实例仲裁打掉。
        Start-Sleep -Seconds 3
        if ($app.HasExited) {
          Write-WatchdogLine "the new app exited immediately with code $($app.ExitCode); the install itself succeeded"
          Show-WatchdogLog
        }
        else {
          Write-WatchdogLine "the new app is running (pid $($app.Id)); watchdog done"
        }
        exit 0
        """;

    /// <summary>
    /// Where the watchdog keeps its script and its two logs. Under the per-user local data
    /// directory, never the roaming profile and never the install directory: these files
    /// outlive the update and a user has to be able to find them, and a log written into the
    /// install directory would be deleted by the next install.
    /// </summary>
    internal static string ResolveLogDirectory(Func<string?>? localApplicationData = null)
    {
        localApplicationData ??= static () => Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        var root = localApplicationData();
        if (string.IsNullOrWhiteSpace(root))
        {
            // No per-user directory to write to (a stripped profile): the temporary directory
            // is the fallback, which is where the update package itself already lives.
            root = Path.GetTempPath();
        }

        return Path.Combine(root, ApplicationDirectoryName, LogDirectoryName);
    }

    /// <summary>The path this attempt's watchdog script is written to.</summary>
    internal static string ScriptPath(string logDirectory, string? stamp = null)
        => Path.Combine(logDirectory, $"{ScriptFileNamePrefix}-{stamp ?? Stamp}.ps1");

    /// <summary>The Inno setup log this attempt passes as <c>/LOG</c>.</summary>
    internal static string SetupLogPath(string logDirectory, string? stamp = null)
        => Path.Combine(logDirectory, $"{SetupLogPrefix}-{stamp ?? Stamp}.log");

    /// <summary>The watchdog's own log - the one a failure opens.</summary>
    internal static string WatchdogLogPath(string logDirectory, string? stamp = null)
        => Path.Combine(logDirectory, $"{WatchdogLogPrefix}-{stamp ?? Stamp}.log");

    /// <summary>
    /// Builds the launch of the watchdog. Every value is its own
    /// <see cref="ProcessStartInfo.ArgumentList"/> entry, so a path containing a space, a
    /// quote or a semicolon reaches PowerShell as one argument instead of being parsed as
    /// script - and the command line this process leaves behind carries no quoting that has
    /// to be undone.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string scriptPath,
        string setupPath,
        string installDirectory,
        string appExecutable,
        string setupLogPath,
        string watchdogLogPath,
        int oldProcessId)
    {
        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,

            // The temporary directory, not the install directory: the watchdog's working
            // directory has no reason to sit where the files it is about to replace live.
            WorkingDirectory = Path.GetTempPath(),
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(SetupPathParameter);
        startInfo.ArgumentList.Add(setupPath);
        startInfo.ArgumentList.Add(InstallDirectoryParameter);
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add(AppExecutableParameter);
        startInfo.ArgumentList.Add(appExecutable);
        startInfo.ArgumentList.Add(SetupLogParameter);
        startInfo.ArgumentList.Add(setupLogPath);
        startInfo.ArgumentList.Add(WatchdogLogParameter);
        startInfo.ArgumentList.Add(watchdogLogPath);
        startInfo.ArgumentList.Add(OldProcessIdParameter);
        startInfo.ArgumentList.Add(oldProcessId.ToString(CultureInfo.InvariantCulture));

        return startInfo;
    }
}
