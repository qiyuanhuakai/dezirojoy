using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace DeziroJoy.App.Services;

/// <summary>
/// Decides which <see cref="InstallKind"/> this copy of DeziroJoy is. The probes run in a
/// fixed order and the first match wins, because the signals overlap: an AppImage unpacked
/// and run from a directory that happens to sit under <c>/opt</c> must still be reported as
/// an AppImage, and a Flatpak build is also a Linux build.
/// </summary>
/// <remarks>
/// Every probe is a seam, mirroring <see cref="LinuxDaemonService"/>'s systemctl seam and
/// <see cref="DeviceAccessFixer"/>'s runner seam: production reads the real filesystem,
/// registry and package databases, tests hand in fakes so the detection logic is decided by
/// the probe answers rather than by the machine running the tests.
/// </remarks>
public static class InstallKindDetector
{
    /// <summary>The marker file the Flatpak runtime places at the filesystem root.</summary>
    private const string FlatpakInfoPath = "/.flatpak-info";

    /// <summary>Where the .deb and the .rpm both install the application.</summary>
    private const string LinuxInstallDirectory = "/opt/dezirojoy";

    /// <summary>The dpkg package name the .deb registers.</summary>
    private const string DebPackageName = "dezirojoy";

    /// <summary>The rpm package name the .rpm registers.</summary>
    private const string RpmPackageName = "dezirojoy";

    /// <summary>
    /// The Inno Setup uninstall key of the per-user Windows install. The GUID is the AppId
    /// pinned by <c>DeziroJoy.iss</c>; <c>_is1</c> is the suffix Inno appends for the single
    /// install of that AppId, so the key exists exactly when this user installed DeziroJoy
    /// through the setup program.
    /// </summary>
    private const string WindowsUninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E565E61E-24C0-4A2C-9DFF-4B8D02DD7382}_is1";

    /// <summary>Detects the install form of this copy, reading the real system.</summary>
    public static InstallKindInfo Detect() => Detect(
        flatpakInfoExists: null,
        appImagePath: null,
        processPath: null,
        isWindows: null,
        windowsUninstallKeyExists: null,
        packageInstalled: null,
        runtimeIdentifier: null);

    /// <summary>
    /// Detects the install form from injected probes. The order is the contract:
    /// <list type="number">
    /// <item>Flatpak - <c>/.flatpak-info</c> exists.</item>
    /// <item>AppImage - <c>$APPIMAGE</c> names a file that exists (the type-2 runtime sets it).</item>
    /// <item>WindowsInstall - the HKCU uninstall key of the setup program exists.</item>
    /// <item>Deb / Rpm - the executable lives under <c>/opt/dezirojoy</c> and the matching
    /// package database reports the package as installed. The two share that directory, so
    /// the database - not the path - is what tells them apart.</item>
    /// <item>Portable - everything else: unpacked, zipped or "dotnet run".</item>
    /// </list>
    /// </summary>
    internal static InstallKindInfo Detect(
        Func<bool>? flatpakInfoExists = null,
        Func<string?>? appImagePath = null,
        Func<string?>? processPath = null,
        Func<bool>? isWindows = null,
        Func<bool>? windowsUninstallKeyExists = null,
        Func<LinuxPackageFormat, bool>? packageInstalled = null,
        Func<string>? runtimeIdentifier = null)
    {
        flatpakInfoExists ??= static () => File.Exists(FlatpakInfoPath);
        appImagePath ??= static () => Environment.GetEnvironmentVariable("APPIMAGE");
        processPath ??= static () => Environment.ProcessPath;
        isWindows ??= static () => OperatingSystem.IsWindows();
        windowsUninstallKeyExists ??= WindowsUninstallKeyExists;
        packageInstalled ??= PackageInstalled;
        runtimeIdentifier ??= RuntimeIdentifier;

        var rid = runtimeIdentifier();

        if (flatpakInfoExists())
        {
            return new InstallKindInfo(InstallKind.Flatpak, rid);
        }

        var appImage = appImagePath();
        if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
        {
            return new InstallKindInfo(InstallKind.AppImage, rid);
        }

        if (isWindows())
        {
            // A Windows copy that the setup program never registered is a portable one: it
            // has no uninstall entry, so nothing can be replaced in place either.
            return windowsUninstallKeyExists()
                ? new InstallKindInfo(InstallKind.WindowsInstall, rid)
                : new InstallKindInfo(InstallKind.Portable, rid);
        }

        if (LivesInLinuxInstallDirectory(processPath()))
        {
            if (packageInstalled(LinuxPackageFormat.Deb))
            {
                return new InstallKindInfo(InstallKind.Deb, rid);
            }

            if (packageInstalled(LinuxPackageFormat.Rpm))
            {
                return new InstallKindInfo(InstallKind.Rpm, rid);
            }
        }

        return new InstallKindInfo(InstallKind.Portable, rid);
    }

    /// <summary>
    /// True when the running executable sits inside the Linux install directory. The path is
    /// normalized first, so a symlink-free but differently spelled path still compares
    /// equal; a symlinked launcher outside the directory stays Portable, which is the honest
    /// answer - the package database check below is what confirms a package install.
    /// </summary>
    private static bool LivesInLinuxInstallDirectory(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(processPath);
            return full.StartsWith(
                LinuxInstallDirectory + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            // A path that cannot be resolved proves nothing about where the app lives.
            return false;
        }
    }

    /// <summary>
    /// Reads the uninstall key of the per-user Windows install. Only meaningful on Windows;
    /// anywhere else there is no such registry and the answer is plainly false.
    /// </summary>
    private static bool WindowsUninstallKeyExists()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsUninstallKeyPath);
            return key is not null;
        }
        catch (Exception exception) when (exception
            is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A registry that cannot be read is not evidence of an install: stay honest about
            // what is known instead of guessing the user into a package update.
            return false;
        }
    }

    /// <summary>
    /// Asks the package databases whether DeziroJoy is installed. A query that cannot run at
    /// all - no dpkg-query, no rpm on this machine - is a false, not an error: the caller
    /// falls through to Portable, which only ever leads to a link.
    /// </summary>
    private static bool PackageInstalled(LinuxPackageFormat format)
    {
        if (format == LinuxPackageFormat.Deb)
        {
            // dpkg-query answers 0 even for a package that is only half-configured
            // ("deinstall ok config-files"), so the status field is what is compared.
            var deb = new ProcessRunner().Run("dpkg-query", $"-W -f=${{db:Status-Status}} {DebPackageName}");
            return deb.ExitCode == 0
                && string.Equals(deb.StandardOutput.Trim(), "installed", StringComparison.Ordinal);
        }

        // rpm -q answers 0 only for a package that is installed.
        var rpm = new ProcessRunner().Run("rpm", $"-q {RpmPackageName}");
        return rpm.ExitCode == 0;
    }

    /// <summary>
    /// The runtime identifier the release assets are named for. A self-contained publish
    /// carries it; a framework-dependent or unpacked run may not, so the OS and architecture
    /// of this process are the fallback.
    /// </summary>
    private static string RuntimeIdentifier()
    {
        var published = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(published))
        {
            return published;
        }

        var os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : "any";

        var architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        return $"{os}-{architecture}";
    }
}

/// <summary>The two Linux package databases that share the <c>/opt/dezirojoy</c> layout.</summary>
public enum LinuxPackageFormat
{
    Deb,
    Rpm,
}
