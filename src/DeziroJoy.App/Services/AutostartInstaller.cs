using System.ComponentModel;
using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Registers the GUI as a logon autostart entry so the app starts when the user signs in.
///
/// This is deliberately a separate registration from the headless daemon (<see cref="DeziroJoy.Platform.Windows.Elevation.DaemonInstaller"/>,
/// task name "DeziroJoy" on Windows; <see cref="LinuxDaemonService.UnitName"/> on Linux): the daemon runs
/// <c>--daemon</c> in the background for mapping, while this one launches the ordinary windowed
/// app, so the two can be toggled independently.
///
/// The trigger is <c>ONLOGON</c>, never a boot/service trigger: a pre-logon process runs in
/// Session 0 and cannot inject input into the interactive desktop (Session 0 isolation), so it
/// would be useless for a tool whose whole job is synthesizing input. No <c>/RL HIGHEST</c> is
/// used because the windowed app stays asInvoker and can relaunch itself elevated on demand.
///
/// Linux has no scheduled-task concept to misuse: the equivalent is an XDG autostart
/// <c>.desktop</c> entry under <c>~/.config/autostart</c>, which every mainstream desktop
/// environment honours at login. No administrator token is involved on either platform.
/// </summary>
public static class AutostartInstaller
{
    public const string TaskName = "DeziroJoy-Autostart";

    /// <summary>The XDG autostart file the Linux path writes.</summary>
    public const string LinuxDesktopFileName = "DeziroJoy.desktop";

    /// <summary>
    /// Where the Linux autostart entry lives (<c>~/.config/autostart</c>). Overridable so tests
    /// never touch a real home directory.
    /// </summary>
    internal static Func<string> LinuxAutostartDirectory { get; set; }
        = static () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "autostart");

    /// <summary>Full path of the XDG autostart entry on Linux.</summary>
    public static string LinuxDesktopPath => Path.Combine(LinuxAutostartDirectory(), LinuxDesktopFileName);

    /// <summary>The schtasks command line that registers the GUI to start at logon.</summary>
    public static string BuildCreateCommandLine(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return $"schtasks /Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\"\" /SC ONLOGON /F";
    }

    /// <summary>The schtasks command line that removes the logon autostart task.</summary>
    public static string BuildDeleteCommandLine() => $"schtasks /Delete /TN \"{TaskName}\" /F";

    /// <summary>
    /// The XDG desktop entry the Linux path installs. A plain <c>Type=Application</c> entry:
    /// the login session starts the GUI, and the daemon's own systemd unit decides whether
    /// background mapping runs - the two registrations stay independent, as on Windows.
    /// </summary>
    public static string BuildLinuxDesktopFile(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return $$"""
            [Desktop Entry]
            Type=Application
            Name=DeziroJoy
            Comment=手柄映射：连接手柄后在设置中配置
            Exec="{{executablePath}}"
            Icon=dezirojoy
            Terminal=false
            Categories=Utility;Game;
            X-GNOME-Autostart-enabled=true
            """;
    }

    /// <summary>Registers the logon autostart entry. Returns false when the platform call fails.</summary>
    public static bool Install(string executablePath)
        => OperatingSystem.IsLinux()
            ? InstallLinuxDesktop(executablePath)
            : Run(BuildCreateCommandLine(executablePath));

    /// <summary>Removes the logon autostart entry. Returns false when the platform call fails.</summary>
    public static bool Uninstall()
        => OperatingSystem.IsLinux() ? UninstallLinuxDesktop() : Run(BuildDeleteCommandLine());

    /// <summary>True when the logon autostart entry is currently registered.</summary>
    public static bool IsInstalled()
        => OperatingSystem.IsLinux() ? File.Exists(LinuxDesktopPath) : Query();

    private static bool InstallLinuxDesktop(string executablePath)
    {
        try
        {
            Directory.CreateDirectory(LinuxAutostartDirectory());
            File.WriteAllText(LinuxDesktopPath, BuildLinuxDesktopFile(executablePath));

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool UninstallLinuxDesktop()
    {
        try
        {
            if (File.Exists(LinuxDesktopPath))
            {
                File.Delete(LinuxDesktopPath);
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Query()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c schtasks /Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            process?.WaitForExit();

            return process is not null && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Run(string commandLine)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {commandLine}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            process?.WaitForExit();

            return process is not null && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
