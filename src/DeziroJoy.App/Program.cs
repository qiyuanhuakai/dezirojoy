using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using DeziroJoy.Platform.Windows.Elevation;
using DeziroJoy.Platform.Windows.Gaming;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DeziroJoy.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var performanceReporter = PerformanceReporter.StartIfEnabled(args);

        if (PerformanceControl.TryRun(args, out var performanceControlExit))
        {
            return performanceControlExit;
        }

        // The registered ms-gamebar no-op handler. It answers before anything that starts a
        // window, the daemon or the instance lock: the shell launches the app with the link
        // and expects it to go away quietly, so this path parses its one argument and exits.
        if (GameBarProtocol.TryRunNoop(args, out var gameBarNoopExit))
        {
            return gameBarNoopExit;
        }

        // The headless daemon must never touch Avalonia: no App object, no window, no UI
        // stack. Dispatch before BuildAvaloniaApp() so those assemblies stay unloaded and
        // the resident process stays near the bare .NET memory floor.
        if (HasFlag(args, "--daemon"))
        {
            return DaemonHost.Run();
        }

        // The elevated recovery entry: an elevated copy of this product, asked for by the
        // supervisor when the daemon that stopped answering runs with a token the GUI
        // does not have and no planned task owns it. It ends exactly the daemon the
        // caller identified and then becomes its replacement.
        if (HasFlag(args, "--recover-daemon"))
        {
            return ElevatedDaemonRecovery.Run(args);
        }

        // The elevated stop-only entry, asked for by the update's stop chain for the same
        // daemon: it ends exactly the identified daemon and exits instead of becoming the
        // replacement, because the caller is an update that is about to replace the install
        // directory and has no use for a daemon it would have to stop again.
        if (HasFlag(args, ElevatedDaemonRecovery.StopOnlyFlag))
        {
            return ElevatedDaemonRecovery.Run(args, stopOnly: true);
        }

        if (HasFlag(args, "--install-daemon"))
        {
            return InstallDaemon();
        }

        if (HasFlag(args, "--uninstall-daemon"))
        {
            return UninstallDaemon();
        }

        if (HasFlag(args, "--install-gamebar-handler"))
        {
            return InstallGameBarHandler();
        }

        if (HasFlag(args, "--uninstall-gamebar-handler"))
        {
            return UninstallGameBarHandler();
        }

        if (HasFlag(args, "--install-autostart"))
        {
            return InstallAutostart();
        }

        if (HasFlag(args, "--uninstall-autostart"))
        {
            return UninstallAutostart();
        }

        // The default entry is the render process. The independent --tray host stays resident
        // for overlays and shows its icon only while no render window is up.
        if (HasFlag(args, "--tray"))
        {
            return TrayHost.Run();
        }

        return DesktopLauncher.Launch(args);
    }

    /// <summary>
    /// Builds the render window's application. The rendering backend is resolved here instead
    /// of being pinned in the chain: the persisted selection, the <c>DEZIROJOY_RENDER</c>
    /// variable and the <c>--render=</c> switch choose between the platform's own detection and
    /// the Win32 software mode, and the choice is fixed for the process - a live window cannot
    /// swap its renderer, which is why the settings card asks for a restart rather than
    /// applying the switch on the spot. Every source degrades to the platform default, so a
    /// launch that names nothing, a settings file that names something unreadable, and a
    /// platform where the software mode does not exist all start exactly as they did before
    /// the switch.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp(string[]? args = null)
        => BuildGui(args, new ThemeService().RenderMode);

    /// <summary>
    /// Builds the resident presentation host's application - the <c>--tray</c> process that
    /// owns the tray icon and the floating layer-hint and screen-keyboard surfaces for the
    /// whole session. Its baseline is <see cref="AppRenderModes.ResidentHostBaseline"/> rather
    /// than the persisted selection: a host that never closes would otherwise keep a GPU stack
    /// alive for the entire logon session on behalf of an icon and two small overlays, and the
    /// user's rendering preference belongs to the window they work in, not to this process.
    /// <para>
    /// The one thing that can still move it is an explicit <c>DEZIROJOY_RENDER</c> override,
    /// which exists for diagnostics only - comparing the two renderers on the tray surfaces -
    /// and is never read from the settings file. Off Windows the pin is inert and the platform
    /// default runs, exactly as before.
    /// </para>
    /// </summary>
    public static AppBuilder BuildTrayAvaloniaApp(string[]? args = null)
        => BuildGui(args, AppRenderModes.ResidentHostBaseline);

    private static AppBuilder BuildGui(string[]? args, AppRenderMode baseline)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

        var (renderMode, ignored) = AppRenderModes.Resolve(
            baseline,
            CommandLineValue(args, AppRenderModes.CommandLineSwitch),
            Environment.GetEnvironmentVariable(AppRenderModes.EnvironmentVariable),
            OperatingSystem.IsWindows());

        if (ignored is { } note)
        {
            // An override that was not honoured is the one part of an A/B run that cannot be
            // seen from outside the process; the trace is where the run records what it
            // actually started with.
            Trace.WriteLine($"[DeziroJoy] 渲染模式：{note}");
        }

        if (renderMode == AppRenderMode.Software)
        {
            Trace.WriteLine("[DeziroJoy] 渲染模式：software（Win32 软件渲染）。");
        }

        var options = AppRenderModes.Win32Options(renderMode);

        // Default keeps the chain exactly as it was: no Win32PlatformOptions is registered,
        // so Avalonia applies its own platform detection untouched on every platform.
        return options is null ? builder : builder.With(options);
    }

    /// <summary>The value of a <c>--name=value</c> command-line switch, or null when absent.</summary>
    private static string? CommandLineValue(string[]? args, string name)
    {
        if (args is null)
        {
            return null;
        }

        var prefix = name + "=";

        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static int InstallDaemon()
    {
        if (OperatingSystem.IsLinux())
        {
            // Same-user model: a systemd user unit needs no administrator token, so there is no
            // elevated relaunch to hand off to - the work happens right here.
            return Report(new LinuxDaemonService().InstallDaemon());
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--install-daemon 仅支持 Windows 与 Linux。");
            return 2;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || IsDotNetHost(executable))
        {
            // Under "dotnet run" the process path is the muxer, which would register a
            // task that never starts the daemon.
            Console.Error.WriteLine("请直接运行构建产物 DeziroJoy.exe --install-daemon，不要通过 dotnet run。");
            return 2;
        }

        // schtasks /Create refuses to run unelevated even without /RL HIGHEST, so an
        // elevated task can only be registered by an elevated caller. Hand the work to
        // a UAC-elevated copy of ourselves instead of failing here.
        if (!WindowsProcessElevation.IsElevated)
        {
            return RelaunchElevatedFor("--install-daemon");
        }

        var installed = DaemonInstaller.Install(executable);

        Console.WriteLine(installed
            ? $"已注册登录时启动的提权计划任务 \"{DaemonInstaller.TaskName}\"：{executable} --daemon"
            : $"注册计划任务 \"{DaemonInstaller.TaskName}\" 失败，请检查上面的 schtasks 输出。");

        return installed ? 0 : 1;
    }

    private static int UninstallDaemon()
    {
        if (OperatingSystem.IsLinux())
        {
            return Report(new LinuxDaemonService().UninstallDaemon());
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--uninstall-daemon 仅支持 Windows 与 Linux。");
            return 2;
        }

        if (!WindowsProcessElevation.IsElevated)
        {
            return RelaunchElevatedFor("--uninstall-daemon");
        }

        var removed = DaemonInstaller.Uninstall();

        Console.WriteLine(removed
            ? $"已移除计划任务 \"{DaemonInstaller.TaskName}\"."
            : $"移除计划任务 \"{DaemonInstaller.TaskName}\" 失败（可能本来就不存在）。");

        return removed ? 0 : 1;
    }

    /// <summary>
    /// Registers the per-user no-op ms-gamebar protocol handler the settings card installs,
    /// for scripted or command-line use. Per-user by design - no elevation, nothing written
    /// outside the user's own classes key - and --uninstall-gamebar-handler puts back
    /// whatever command the protocol had before.
    /// </summary>
    private static int InstallGameBarHandler()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--install-gamebar-handler 仅支持 Windows。");
            return 2;
        }

        try
        {
            new GameBarPopupGuard().RegisterProtocolHandler();
            Console.WriteLine(
                "已为本用户注册 ms-gamebar 静默处理程序（仅当前用户；ms-gamebar URI 由 DeziroJoy "
                + "静默处理，其他启动方式不主动拦截；不修改系统录制设置；只覆盖 ms-gamebar，"
                + "不保证抑制所有弹窗，部分系统组件经其他上下文触发时仍可能弹窗，"
                + "此时请用系统设置或安装/修复 Xbox Game Bar；可用 --uninstall-gamebar-handler 恢复系统默认）。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"注册 ms-gamebar 静默处理程序失败：{exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Removes only a registration this app owns, restoring the command the protocol had
    /// before - or removing the key when it had none. A registration another owner has
    /// taken over is reported and left alone.
    /// </summary>
    private static int UninstallGameBarHandler()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--uninstall-gamebar-handler 仅支持 Windows。");
            return 2;
        }

        try
        {
            var guard = new GameBarPopupGuard();
            var owned = guard.OwnsProtocolHandler;
            guard.RestoreProtocolHandler();
            Console.WriteLine(owned
                ? "已移除本应用注册的 ms-gamebar 静默处理程序，并恢复此前的系统默认。"
                : "没有本应用注册的 ms-gamebar 处理程序，未做任何改动。");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"移除 ms-gamebar 静默处理程序失败：{exception.Message}");
            return 1;
        }
    }

    private static int InstallAutostart()
    {
        if (OperatingSystem.IsLinux())
        {
            // Per-user by design: an XDG autostart entry, no administrator token, no relaunch.
            var linuxExecutable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(linuxExecutable) || IsDotNetHost(linuxExecutable))
            {
                Console.Error.WriteLine("请直接运行构建产物 DeziroJoy --install-autostart，不要通过 dotnet run。");
                return 2;
            }

            var linuxInstalled = AutostartInstaller.Install(linuxExecutable);

            Console.WriteLine(linuxInstalled
                ? $"已启用登录自启动（{AutostartInstaller.LinuxDesktopPath}）"
                : "启用登录自启动失败，请检查 ~/.config/autostart 是否可写。");

            return linuxInstalled ? 0 : 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--install-autostart 仅支持 Windows 与 Linux。");
            return 2;
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable) || IsDotNetHost(executable))
        {
            Console.Error.WriteLine("请直接运行构建产物 DeziroJoy.exe --install-autostart，不要通过 dotnet run。");
            return 2;
        }

        // schtasks /Create needs an administrator token even without /RL HIGHEST, so an
        // unelevated caller is handed to a UAC-elevated copy of itself.
        if (!WindowsProcessElevation.IsElevated)
        {
            return RelaunchElevatedFor("--install-autostart");
        }

        var installed = AutostartInstaller.Install(executable);

        Console.WriteLine(installed
            ? $"已注册登录自启动计划任务 \"{AutostartInstaller.TaskName}\"（登录时触发器）：{executable}"
            : $"注册计划任务 \"{AutostartInstaller.TaskName}\" 失败，请检查上面的 schtasks 输出。");

        return installed ? 0 : 1;
    }

    private static int UninstallAutostart()
    {
        if (OperatingSystem.IsLinux())
        {
            var linuxRemoved = AutostartInstaller.Uninstall();

            Console.WriteLine(linuxRemoved
                ? $"已移除登录自启动（{AutostartInstaller.LinuxDesktopPath}）。"
                : "移除登录自启动失败（可能本来就不存在）。");

            return linuxRemoved ? 0 : 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--uninstall-autostart 仅支持 Windows 与 Linux。");
            return 2;
        }

        if (!WindowsProcessElevation.IsElevated)
        {
            return RelaunchElevatedFor("--uninstall-autostart");
        }

        var removed = AutostartInstaller.Uninstall();

        Console.WriteLine(removed
            ? $"已移除计划任务 \"{AutostartInstaller.TaskName}\"."
            : $"移除计划任务 \"{AutostartInstaller.TaskName}\" 失败（可能本来就不存在）。");

        return removed ? 0 : 1;
    }

    private static int RelaunchElevatedFor(string flag)
    {
        Console.WriteLine($"需要管理员权限才能{Describe(flag)}，正在请求提权…");

        if (WindowsProcessElevation.RelaunchElevated(flag))
        {
            // The elevated copy owns the result from here; it prints into its own window.
            return 0;
        }

        Console.Error.WriteLine("未获得管理员权限（可能取消了 UAC 提示）。请以管理员身份运行后重试。");
        return 1;
    }

    /// <summary>
    /// Prints a service operation's message and maps it to an exit code: 0 completed, 1 failed,
    /// 2 the platform does not support it. The Linux CLI path shares the service's wording with
    /// the settings card, so the two never disagree about what happened.
    /// </summary>
    private static int Report(DaemonActionResult result)
    {
        switch (result.Result)
        {
            case DaemonOp.Completed:
                Console.WriteLine(result.Message);
                return 0;

            case DaemonOp.Unsupported:
                Console.Error.WriteLine(result.Message);
                return 2;

            default:
                Console.Error.WriteLine(result.Message);
                return 1;
        }
    }

    private static string Describe(string flag) => flag switch
    {
        "--uninstall-daemon" => "移除计划任务",
        "--install-autostart" => "启用登录自启动",
        "--uninstall-autostart" => "关闭登录自启动",
        _ => "注册计划任务",
    };

    internal static bool IsDotNetHost(string executable)
        => string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The elevated recovery entry. An elevated copy of this product, asked for by the
/// supervisor when the daemon that stopped answering runs with a token the GUI does not
/// have and no planned task owns it - the one-off elevated relaunch on Windows, where an
/// unelevated caller can neither read the daemon's executable nor end it.
/// <para>
/// The copy does the end and the start itself, in that order and never the other way
/// around: it re-verifies the identity the caller handed over - the PID a snapshot named,
/// the same daemon executable, started no later than the moment it was last seen serving -
/// and only then ends the old process, waits for the daemon instance slot and the
/// injection mutex to be released, and finally becomes the replacement daemon through the
/// ordinary <see cref="DaemonHost.Run"/> path. A PID whose identity cannot be confirmed is
/// never ended: a reused number or a foreign executable is reported and the copy exits
/// without touching anything, because a kill is only ever issued against a daemon this
/// product can prove.
/// </para>
/// </summary>
internal static class ElevatedDaemonRecovery
{
    /// <summary>The command-line flag that dispatches into this entry.</summary>
    public const string Flag = "--recover-daemon";

    /// <summary>
    /// The command-line flag that asks for the same identity-verified end without the
    /// replacement. The update's stop chain uses it: an update replaces the install directory,
    /// so a daemon that is merely replaced is a daemon the update has to stop again, and the
    /// only token that can end an elevated daemon no planned task owns is an elevated one.
    /// </summary>
    public const string StopOnlyFlag = "--stop-daemon";

    private const string PidArgument = "--pid";

    private const string SeenAtArgument = "--seen-at";

    /// <summary>The identity did not check out; nothing was ended and nothing was started.</summary>
    public const int IdentityRejected = 2;

    /// <summary>The end or the release failed; nothing was started, so no second injector exists.</summary>
    public const int EndFailed = 3;

    /// <summary>How long the copy waits for the ended daemon's slots before giving up.</summary>
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ReleaseProbeInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs the elevated recovery. The seams exist so no test ever ends a real process or
    /// starts a real daemon: production passes the defaults, a test passes recorders.
    /// <para>
    /// With <paramref name="stopOnly"/> the copy ends the daemon and exits instead of becoming
    /// the replacement: the caller is an update that is about to replace the install directory,
    /// so a replacement would be a daemon it has to stop again - and one that could re-lock the
    /// files mid-install. Nothing is started either way until the identity is confirmed and the
    /// old process is gone, so a stop-only run can never be a second injector.
    /// </para>
    /// </summary>
    public static int Run(
        string[] args,
        IDaemonRecovery? recovery = null,
        Func<int>? runDaemon = null,
        TimeSpan? releaseWait = null,
        bool stopOnly = false)
    {
        recovery ??= new SystemDaemonRecovery();
        runDaemon ??= static () => DaemonHost.Run();
        var releaseBudget = releaseWait ?? ReleaseWait;

        var processId = ParsePid(args);
        var seenAtTicks = ParseSeenAt(args);

        if (processId is null || processId.Value == 0 || seenAtTicks is null)
        {
            Console.Error.WriteLine($"{Flag} 需要 {PidArgument} 与 {SeenAtArgument} 参数（守护进程身份），已拒绝执行。");
            return IdentityRejected;
        }

        var seenAt = new DateTime(seenAtTicks.Value, DateTimeKind.Utc);

        var startTime = recovery.TryGetStartTime(processId.Value);
        if (startTime is null)
        {
            // The daemon is already gone - it died between the request and this copy.
            // There is nothing to end and nothing to verify, so the copy goes straight to
            // being the replacement.
            Console.WriteLine($"[DeziroJoy] 提权恢复：守护进程 (PID {processId.Value}) 已退出，无需结束。");
        }
        else
        {
            if (startTime > seenAt)
            {
                // The process started after the snapshot that named this PID: the daemon
                // died and the number was handed to something else. Ending it would kill a
                // process this product has no authority over.
                Console.Error.WriteLine(
                    $"[DeziroJoy] 提权恢复：PID {processId.Value} 已属于启动时间更晚的进程（PID 复用），拒绝结束。");
                return IdentityRejected;
            }

            if (!recovery.IsDaemonExecutable(processId.Value))
            {
                Console.Error.WriteLine(
                    $"[DeziroJoy] 提权恢复：PID {processId.Value} 不再运行本产品的守护进程可执行文件，拒绝结束。");
                return IdentityRejected;
            }

            if (!recovery.EndProcess(processId.Value))
            {
                Console.Error.WriteLine($"[DeziroJoy] 提权恢复：无法结束 PID {processId.Value}（权限不足？）。");
                return EndFailed;
            }

            Console.WriteLine($"[DeziroJoy] 提权恢复：已按身份确认结束 PID {processId.Value}。");
        }

        // Wait for the instance slot and the injection mutex to be released before
        // becoming the daemon: a replacement that starts beside a holder is the second
        // injector the slot exists to prevent. This copy holds neither yet, so a free
        // answer here is meaningful.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var processGone = recovery.TryGetStartTime(processId.Value) is null;
            if (processGone && recovery.IsDaemonInstanceSlotFree() && recovery.IsInjectionSlotFree())
            {
                break;
            }

            if (elapsed.Elapsed >= releaseBudget)
            {
                Console.Error.WriteLine(
                    $"[DeziroJoy] 提权恢复：实例或注入槽位未在 {releaseBudget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} 秒内释放，放弃启动以避免双重注入。");
                return EndFailed;
            }

            Thread.Sleep(ReleaseProbeInterval);
        }

        if (stopOnly)
        {
            return StopOnlyDone();
        }

        Console.WriteLine("[DeziroJoy] 提权恢复：实例与注入槽位已释放，启动替代守护进程。");
        return runDaemon();
    }

    /// <summary>
    /// The stop-only landing: the daemon is gone and the slots are free, so this copy reports
    /// success and exits. Starting a replacement here would be the one thing the caller does not
    /// want - it asked for an end, and the install directory it is about to replace is the reason.
    /// </summary>
    private static int StopOnlyDone()
    {
        Console.WriteLine("[DeziroJoy] 提权停止：守护进程已结束，槽位已释放，不启动替代进程。");
        return 0;
    }

    private static int? ParsePid(string[] args)
        => ParseArgument(args, PidArgument, static value => int.TryParse(value, out var pid) ? pid : (int?)null);

    private static long? ParseSeenAt(string[] args)
        => ParseArgument(args, SeenAtArgument, static value => long.TryParse(value, out var ticks) ? ticks : (long?)null);

    private static T? ParseArgument<T>(string[] args, string name, Func<string, T?> parse) where T : struct
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return parse(args[index + 1]);
            }
        }

        return null;
    }
}

/// <summary>
/// The render process. It arbitrates the instance lock, serves the activation channel, starts
/// the daemon and shows the window. The render lifetime coordinator either acknowledges a tray
/// successor or stops the daemon for a full exit before this owner releases its resources.
/// </summary>
internal static class DesktopLauncher
{
    private static GuiInstanceOwner? _instance;
    private static GuiActivationServer? _activation;
    private static bool _serving;

    internal static DaemonSupervisor? Daemon { get; private set; }

    public static int Launch(string[] args)
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Render);
        var arbitration = GuiInstanceGuard
            .ArbitrateAsync(identity, null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!arbitration.IsWinner)
        {
            // The loser already sent Show to the lock holder and must not construct a second GUI.
            return arbitration.ExitCode;
        }

        var activation = GuiActivationServer.StartActive(identity);
        var owner = new GuiInstanceOwner(identity, arbitration.Lease!, activation);
        _instance = owner;
        _activation = activation;

        activation.ShowRequested += () =>
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (window is null)
            {
                throw new InvalidOperationException("the render window is not available");
            }

            return Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }

                window.Activate();
            }).GetTask();
        };

        // The daemon is the mapping authority from the first window on: the render process
        // starts it (elevated on Windows) and supervises nothing else.
        var daemon = new DaemonSupervisor();
        Daemon = daemon;
        daemon.EnsureRunning();
        EnsureTrayRunning();

        try
        {
            Program.BuildAvaloniaApp(args).StartWithClassicDesktopLifetime(args);
            return 0;
        }
        finally
        {
            _activation = null;
            _instance = null;
            Daemon = null;
            owner.Dispose();
            daemon.Dispose();
        }
    }

    /// <summary>
    /// The safe restart the input-backend switch asks for: the supervisor stops the daemon
    /// gracefully - which releases every held key - and brings the replacement up through
    /// the same start path the window opened with, so the new daemon reads the saved
    /// selection. Off the UI thread because the stop waits on the daemon's shutdown budget.
    /// </summary>
    internal static Task<bool> RestartDaemonAsync()
        => Task.Run(() => Daemon?.Restart() ?? false);

    /// <summary>
    /// The supervised recovery the reconnect button asks for: the supervisor ends the
    /// daemon that stopped answering - by identity, or through the owner's own service
    /// (the systemd user unit, the Windows planned task) - waits for the mapping pipe and
    /// the injection slot to be released, and brings a replacement up before reporting
    /// success. Every wait is bounded but real, so this runs off the UI thread.
    /// </summary>
    internal static Task<DaemonRecoveryReport> RecoverDaemonAsync()
        => Task.Run(() => Daemon?.Recover() ?? new DaemonRecoveryReport(
            false,
            [],
            "没有运行中的守护进程监督器"));

    /// <summary>
    /// The update's stop: the one the installer may be started behind. It is not the exit
    /// path's stop, because the exit path only needs the pipe gone while the process is on
    /// its way out anyway - an update needs the daemon *process* gone, since a daemon that
    /// is still resident when the setup begins holds the install directory's files open and
    /// the setup's DeleteFile fails with access denied (error 5) and rolls back.
    /// <para>
    /// The supervisor therefore proves the end instead of requesting it: the graceful stop,
    /// then the recovery's own escalation (the systemd user unit's stop, the planned task's
    /// end, the identity-confirmed process end, or a stop-only elevated copy that verifies
    /// the same identity with its own token and starts no replacement), then the proof - and
    /// a daemon nobody could prove gone is a failed stop, so the update is cancelled with
    /// mapping still working rather than started against a live daemon. Off the UI thread
    /// because every wait in it is bounded but real.
    /// </para>
    /// </summary>
    internal static Task<bool> StopDaemonForUpdateAsync()
        => Task.Run(() => Daemon?.StopForUpdate() ?? true);

    internal static bool EnsureTrayRunning()
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Tray);
        var probeOptions = new GuiActivationOptions(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250));
        try
        {
            if (GuiActivationClient.TryProbeAsync(identity, GuiInstanceRole.Tray, probeOptions, CancellationToken.None)
                .GetAwaiter().GetResult())
            {
                return true;
            }

            using var tray = ProcessLauncher.Start(ProcessLauncher.TrayStartInfo());
            return tray is not null;
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"[DeziroJoy] presentation host launch failed: {exception.Message}");
            return false;
        }
    }

    internal static async Task<bool> RequestTrayExitAsync()
    {
        var identity = GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Tray);
        var probeOptions = new GuiActivationOptions(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250));
        if (!await GuiActivationClient.TryProbeAsync(
                identity, GuiInstanceRole.Tray, probeOptions, CancellationToken.None).ConfigureAwait(false))
        {
            return true;
        }

        return await GuiActivationClient.TryExitAsync(
            identity,
            new GuiActivationOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal static void NotifyRenderStopped()
    {
        _ = GuiActivationClient.NotifyRenderStoppedAsync(
            GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Tray),
            new GuiActivationOptions(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Called once the render window is on screen: serving starts only then, so a Show arriving
    /// during startup is answered by a live window instead of a half-built one.
    /// </summary>
    internal static void NotifyWindowShown()
    {
        if (!_serving)
        {
            _serving = true;
            _activation?.BeginServing();
            if (_activation is not null)
            {
                _ = Task.Run(() => GuiActivationClient.NotifyRenderStartedAsync(
                    GuiInstanceIdentity.ForCurrentProcess(GuiInstanceRole.Tray),
                    Environment.ProcessId,
                    new GuiActivationOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)),
                    CancellationToken.None));
            }
        }
    }
}
