using System.ComponentModel;
using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Registers the GUI as a logon scheduled task so the app starts when the user signs in.
///
/// This is deliberately a separate task from the headless daemon (<see cref="DeziroJoy.Platform.Windows.Elevation.DaemonInstaller"/>,
/// task name "DeziroJoy"): the daemon runs <c>--daemon</c> elevated for background mapping, while
/// this one launches the ordinary windowed app, so the two can be toggled independently.
///
/// The trigger is <c>ONLOGON</c>, never a boot/service trigger: a pre-logon process runs in
/// Session 0 and cannot inject input into the interactive desktop (Session 0 isolation), so it
/// would be useless for a tool whose whole job is synthesizing input. No <c>/RL HIGHEST</c> is
/// used because the windowed app stays asInvoker and can relaunch itself elevated on demand.
/// </summary>
public static class AutostartInstaller
{
    public const string TaskName = "DeziroJoy-Autostart";

    /// <summary>The schtasks command line that registers the GUI to start at logon.</summary>
    public static string BuildCreateCommandLine(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        return $"schtasks /Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\"\" /SC ONLOGON /F";
    }

    /// <summary>The schtasks command line that removes the logon autostart task.</summary>
    public static string BuildDeleteCommandLine() => $"schtasks /Delete /TN \"{TaskName}\" /F";

    /// <summary>Registers the logon autostart task. Returns false when schtasks reports a failure.</summary>
    public static bool Install(string executablePath) => Run(BuildCreateCommandLine(executablePath));

    /// <summary>Removes the logon autostart task. Returns false when schtasks fails.</summary>
    public static bool Uninstall() => Run(BuildDeleteCommandLine());

    /// <summary>True when the logon autostart task is currently registered.</summary>
    public static bool IsInstalled() => Query();

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
