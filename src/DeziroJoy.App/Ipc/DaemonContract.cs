using DeziroJoy.App.Services;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Ipc;

/// <summary>Wire error codes the daemon emits beyond <see cref="IpcErrorCodes"/>.</summary>
public static class DaemonErrorCodes
{
    /// <summary>Another process already holds the injection mutex; the start was refused.</summary>
    public const string InjectionActive = "injection-active";

    /// <summary>The submitted profile JSON could not be parsed.</summary>
    public const string InvalidProfile = "invalid-profile";

    /// <summary>The screen keyboard asked to type nothing at all.</summary>
    public const string EmptyInjection = "empty-injection";

    /// <summary>The screen keyboard asked to type more than the Core's own text bound carries.</summary>
    public const string OversizedInjection = "oversized-injection";

    /// <summary>No connected device, so the daemon has no output channel to inject through.</summary>
    public const string NoInjectionRuntime = "no-injection-runtime";
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

    IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices { get; }

    event Action? DevicesChanged;

    /// <summary>Raised for both running ticks and stopped-state presence samples.</summary>
    event Action<string, IGamepadState>? InputSampled;

    GamepadProfile? GetProfile(string deviceId);

    /// <summary>Polls current input without advancing mapping, also used to prime a new subscriber.</summary>
    void SamplePresence();

    /// <summary>
    /// Starts the mapping loop, acquiring the injection mutex. Throws
    /// <see cref="InjectionAlreadyActiveException"/> when another process holds the slot.
    /// </summary>
    void Start();

    void StopMapping();

    void SetMappingEnabled(bool enabled);

    void SetInjectionSuspended(bool suspended);

    /// <summary>Whether mapping is paused so the program in front reads the gamepad itself.</summary>
    bool IsNativeGamepadPaused { get; }

    /// <summary>
    /// Pauses or resumes keyboard and mouse mapping for native gamepad input. Independent of
    /// <see cref="SetMappingEnabled"/> and <see cref="SetInjectionSuspended"/>: the three pauses
    /// never lift one another, and none of them stops the mapping loop or the injection mutex.
    /// </summary>
    void SetNativeGamepadPaused(bool paused);

    /// <summary>Swaps the active profile of one connected device.</summary>
    void Reload(string deviceId, GamepadProfile profile);

    /// <summary>Atomically moves every supplied runtime to the same app-wide slot.</summary>
    void SwitchPresetSlot(int slot, IReadOnlyDictionary<string, GamepadProfile> profiles);

    /// <summary>
    /// Types one short string into whatever has keyboard focus, through the daemon's own
    /// output path. The floating screen keyboard's way out: the daemon is the sole injection
    /// authority, so the window asks and the daemon injects. Bounded by the Core's own text
    /// bound, and refused - rather than silently dropped - when the daemon holds no runtime
    /// to inject through.
    /// </summary>
    void InjectText(string text);

    /// <summary>
    /// Taps one key - press, then release, modifiers around it - into whatever has keyboard
    /// focus, through the daemon's own output path. What the screen keyboard's action keys
    /// need: 退格 sends a Backspace, 清空 sends Ctrl+A and then Delete.
    /// </summary>
    void TapKey(GamepadKey key, KeyModifiers modifiers);
}

public sealed record ConnectedDeviceSnapshot(string DeviceId, string Name, GamepadLayout Layout);

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
    long Revision,
    int ActivePresetSlot = PresetStore.MinSlot);
