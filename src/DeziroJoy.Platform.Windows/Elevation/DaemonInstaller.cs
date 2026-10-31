using System.ComponentModel;
using System.Diagnostics;

namespace DeziroJoy.Platform.Windows.Elevation;

/// <summary>
/// Registers the headless daemon as a logon scheduled task running at the highest
/// privilege level.
///
/// A Windows Service is deliberately NOT used: since Vista, Session 0 isolation keeps a
/// service out of the interactive desktop, and a Session 0 process cannot inject input
/// into the user's session at all. The daemon therefore has to live in the user session,
/// and a logon task with /rl HIGHEST is the minimal correct way to get it there elevated:
/// one UAC consent at install time, then no further prompts (see
/// docs/ELEVATION-AND-DAEMON.md).
/// </summary>
public static class DaemonInstaller
{
    public const string TaskName = "DeziroJoy";

    private const string DaemonArgument = "--daemon";

    /// <summary>The schtasks command line that registers the daemon at logon, elevated.</summary>
    public static string BuildCreateCommandLine(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        // /TR takes the whole command line, so the executable path is escaped-quoted to
        // survive spaces in "Program Files".
        return $"schtasks /Create /TN \"{TaskName}\" /TR \"\\\"{executablePath}\\\" {DaemonArgument}\" " +
            "/SC ONLOGON /RL HIGHEST /F";
    }

    /// <summary>The schtasks command line that removes the daemon's scheduled task.</summary>
    public static string BuildDeleteCommandLine() => $"schtasks /Delete /TN \"{TaskName}\" /F";

    /// <summary>Registers the daemon. Returns false when schtasks reports a failure.</summary>
    public static bool Install(string executablePath) => Run(BuildCreateCommandLine(executablePath));

    /// <summary>Removes the daemon's scheduled task. Returns false when schtasks fails.</summary>
    public static bool Uninstall() => Run(BuildDeleteCommandLine());

    private static bool Run(string commandLine)
    {
        try
        {
            // Output is inherited so the user sees why schtasks refused.
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
