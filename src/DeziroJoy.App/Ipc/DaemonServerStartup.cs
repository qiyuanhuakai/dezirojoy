using System.Text.Json;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Ipc;

/// <summary>
/// The startup half of <see cref="DaemonServer"/>: restoring the desired state a previous
/// daemon persisted, so a restart resumes the mapping the user asked for instead of
/// reverting to first-run defaults.
/// </summary>
public sealed partial class DaemonServer
{
    /// <summary>
    /// Applies the persisted desired state: re-applies the stored profile to connected
    /// devices and starts mapping when it was left enabled. Called once at daemon startup,
    /// before the pipe is served.
    /// </summary>
    public void ResumeDesiredState()
    {
        lock (_sync)
        {
            if (_restored is not null && !string.IsNullOrEmpty(_restored.ProfileJson))
            {
                try
                {
                    var profile = ProfileSerializer.Deserialize(_restored.ProfileJson);
                    _profileStore.Save(profile);
                    ReloadTargetDevice(profile);
                }
                catch (JsonException)
                {
                    // A corrupt persisted profile must not stop the daemon from starting.
                }
            }

            if (_desiredEnabled && !_host.IsRunning)
            {
                TryStartMapping();
            }
        }
    }
}
