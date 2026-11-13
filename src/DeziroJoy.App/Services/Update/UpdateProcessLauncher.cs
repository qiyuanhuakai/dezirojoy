using System.ComponentModel;
using System.Diagnostics;

namespace DeziroJoy.App.Services;

/// <summary>
/// Starts a process the caller does not wait on. The Linux package installer hands its whole
/// install-and-relaunch chain to one of these - this process holds the binary being replaced
/// (dpkg and rpm refuse to overwrite a mapped file, ETXTBSY) and is about to exit, so the chain
/// has to outlive it - and the AppImage installer starts the copy it renamed into place. The
/// Windows installer starts the Inno setup the same way, and for the same reason from the other
/// side: the setup's Restart Manager has to close this GUI to replace the files it holds open,
/// so a GUI that waited on the setup would keep them locked and the setup's DeleteFile would
/// fail with access denied. Production starts a real process; tests record the launch instead,
/// so no real process - and no polkit dialog - is ever spawned by a test.
/// </summary>
internal interface IUpdateProcessLauncher
{
    /// <summary>Starts <paramref name="startInfo"/> and returns whether it came up.</summary>
    bool Start(ProcessStartInfo startInfo);
}

/// <summary>The production launcher: a real OS process, started and left running.</summary>
internal sealed class UpdateProcessLauncher : IUpdateProcessLauncher
{
    public bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // The executable is missing or cannot be started: the caller turns this into an
            // honest failure rather than assuming the relaunch happened.
            return false;
        }
    }
}
