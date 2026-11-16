using Microsoft.Win32;
using System.Security;

namespace DeziroJoy.App.Services;

/// <summary>
/// Where the per-user Windows install lives and which executable an update has to restart.
/// The directory is the one the setup program itself recorded, not a guess from the running
/// process: a copy of the app started from somewhere else would otherwise tell the installer
/// to write into that copy and leave the registered install orphaned.
/// </summary>
internal static class WindowsUpdateInstallLocation
{
    /// <summary>The executable the setup installs and the watchdog restarts.</summary>
    internal const string ExecutableName = "DeziroJoy.exe";

    /// <summary>
    /// The install directory: the setup program's own registration when it names a directory
    /// that is still there, and this process's own directory otherwise. The fallback is the
    /// honest answer for a self-contained publish - <c>DeziroJoy.exe</c> and its libraries sit
    /// side by side, and the app runs from where it was installed.
    /// </summary>
    internal static string ResolveInstallDirectory(
        Func<string?>? registeredLocation = null,
        Func<string>? baseDirectory = null)
    {
        registeredLocation ??= ReadRegisteredLocation;
        baseDirectory ??= static () => AppContext.BaseDirectory;

        var registered = registeredLocation();
        if (!string.IsNullOrWhiteSpace(registered) && Directory.Exists(registered))
        {
            return registered;
        }

        return baseDirectory();
    }

    /// <summary>The full path of the installed executable inside <paramref name="installDirectory"/>.</summary>
    internal static string ResolveExecutable(string installDirectory)
        => Path.Combine(installDirectory, ExecutableName);

    /// <summary>
    /// Reads the <c>InstallLocation</c> the setup wrote into the uninstall key of the per-user
    /// install - the same key <see cref="InstallKindDetector"/> decides
    /// <see cref="InstallKind.WindowsInstall"/> from, so a machine that reaches this installer
    /// has the key. A registry that cannot be read proves nothing and answers null, which
    /// sends the caller to the process directory.
    /// </summary>
    private static string? ReadRegisteredLocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallKindDetector.WindowsUninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch (Exception exception) when (exception
            is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
