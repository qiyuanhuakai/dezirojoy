using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Ipc;

/// <summary>Wire error codes the daemon emits beyond <see cref="IpcErrorCodes"/>.</summary>
public static class DaemonErrorCodes
{
    /// <summary>Another process already holds the injection mutex; the start was refused.</summary>
    public const string InjectionActive = "injection-active";

    /// <summary>The submitted profile JSON could not be parsed.</summary>
    public const string InvalidProfile = "invalid-profile";
}

/// <summary>
/// The resident mapping runtime the daemon drives: engine control plus the injector
/// identity and counters the snapshot must surface. Production implements it over
/// <see cref="EngineComposition"/>; tests implement it with a fake, which is what makes
/// the IPC service testable cross-platform without a real engine, mutex or pipe.
/// </summary>
public interface IDaemonHost
{
    bool IsRunning { get; }

    bool IsMappingEnabled { get; }

    bool HasController { get; }

    int InjectorPid { get; }

    string? InjectorIntegrity { get; }

    long InjectedEvents { get; }

    long BlockedEvents { get; }

    IReadOnlyList<string> ConnectedDeviceIds { get; }

    GamepadProfile? GetProfile(string deviceId);

    /// <summary>
    /// Starts the mapping loop, acquiring the injection mutex. Throws
    /// <see cref="InjectionAlreadyActiveException"/> when another process holds the slot.
    /// </summary>
    void Start();

    void StopMapping();

    void SetMappingEnabled(bool enabled);

    void SetInjectionSuspended(bool suspended);

    /// <summary>Swaps the active profile of one connected device.</summary>
    void Reload(string deviceId, GamepadProfile profile);
}

/// <summary>Time source, injected so the daemon instance id is deterministic under test.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The system clock; the production default.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// The daemon's desired state, persisted so a restart resumes the mapping the user asked
/// for. The profile travels as its serialized body, never a path.
/// </summary>
public sealed record DaemonState(
    bool MappingEnabled,
    string ProfileName,
    string ProfileJson,
    long Revision);
