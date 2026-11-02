using Avalonia;
using DeziroJoy.App.Services;
using DeziroJoy.Platform.Windows.Elevation;

namespace DeziroJoy.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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

        if (HasFlag(args, "--install-autostart"))
        {
            return InstallAutostart();
        }

        if (HasFlag(args, "--uninstall-autostart"))
        {
            return UninstallAutostart();
        }

        // Kept behind its own type so the JIT never has to resolve an Avalonia type while
        // compiling Main: the daemon path then loads no Avalonia assembly at all.
        return DesktopLauncher.Launch(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    private static bool HasFlag(string[] args, string flag)
        => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static int InstallDaemon()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--install-daemon 仅支持 Windows（依赖 schtasks 计划任务）。");
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
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--uninstall-daemon 仅支持 Windows（依赖 schtasks 计划任务）。");
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

    private static int InstallAutostart()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--install-autostart 仅支持 Windows（依赖 schtasks 计划任务）。");
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
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("--uninstall-autostart 仅支持 Windows（依赖 schtasks 计划任务）。");
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

    private static string Describe(string flag) => flag switch
    {
        "--uninstall-daemon" => "移除计划任务",
        "--install-autostart" => "启用登录自启动",
        "--uninstall-autostart" => "关闭登录自启动",
        _ => "注册计划任务",
    };

    private static bool IsDotNetHost(string executable)
        => string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Avalonia desktop lifetime. Isolated from <see cref="Program.Main"/> so the headless
/// daemon never causes an Avalonia assembly to be loaded.
/// </summary>
internal static class DesktopLauncher
{
    public static int Launch(string[] args)
    {
        // GUI single-instance arbitration runs before any Avalonia or runtime construction:
        // a second launch only asks the first GUI to show itself and then exits, so the loser
        // never builds a window, an engine or a daemon controller. A launch that cannot reach
        // the winner exits too - a failed activation is never a reason to start a second GUI.
        var identity = GuiInstanceIdentity.ForCurrentProcess();
        var arbitration = GuiInstanceGuard
            .ArbitrateAsync(identity, cancellationToken: CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (!arbitration.IsWinner)
        {
            return arbitration.ExitCode;
        }

        // This process is the one GUI: serve the activation channel for its whole life and
        // hold the instance lease until the process ends.
        using var lease = arbitration.Lease!;
        using var activation = GuiActivationServer.StartActive(identity);

        Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }
}
