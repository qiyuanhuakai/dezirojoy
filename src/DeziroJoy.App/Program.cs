using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DeziroJoy.App.Hosting;
using DeziroJoy.App.Services;
using DeziroJoy.Platform.Windows.Elevation;
using DeziroJoy.Platform.Windows.Gaming;
using System.Diagnostics;
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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

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
            Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
