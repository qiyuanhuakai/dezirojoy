using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeziroJoy.Platform.Windows.Elevation;

/// <summary>
/// Elevation primitives for the running process.
///
/// The application manifest stays at asInvoker, so an ordinary launch never prompts.
/// Elevation is requested per action through the shell "runas" verb, which is the standard
/// user-initiated UAC path; the daemon is instead registered once as a logon scheduled
/// task with /rl HIGHEST so it runs elevated in the user session without further prompts.
/// </summary>
public static class WindowsProcessElevation
{
    private const uint TokenQuery = 0x0008;

    private const int TokenElevation = 20;

    private const string RunasVerb = "runas";

    /// <summary>True when the current process holds a full (high-integrity) admin token.</summary>
    public static bool IsElevated
    {
        get
        {
            var token = IntPtr.Zero;

            try
            {
                if (!ProcessTokenNative.OpenProcessToken(
                        ProcessTokenNative.GetCurrentProcess(), TokenQuery, out token))
                {
                    return false;
                }

                // TOKEN_ELEVATION is a single BOOL; read as a 4-byte value so the buffer
                // size and the marshalled type agree exactly.
                return ProcessTokenNative.GetTokenInformation(
                    token, TokenElevation, out var elevated, sizeof(uint), out _) && elevated != 0;
            }
            finally
            {
                if (token != IntPtr.Zero)
                {
                    ProcessTokenNative.CloseHandle(token);
                }
            }
        }
    }

    /// <summary>
    /// Starts a second, elevated copy of this executable. Returns false when the user
    /// declines the UAC prompt or the shell refuses the relaunch.
    /// </summary>
    public static bool RelaunchElevated()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                Verb = RunasVerb,
                UseShellExecute = true,
            });

            return process is not null;
        }
        catch (Win32Exception)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt.
            return false;
        }
    }
}

internal static partial class ProcessTokenNative
{
    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);
}
