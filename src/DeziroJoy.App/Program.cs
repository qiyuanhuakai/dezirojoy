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

    private static string Describe(string flag)
        => flag == "--uninstall-daemon" ? "移除计划任务" : "注册计划任务";

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
        Program.BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }
}
