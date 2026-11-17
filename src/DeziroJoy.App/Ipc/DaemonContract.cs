using System.Text.Json.Serialization;
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

    /// <summary>Another connection already holds the input-capture lease on a device.</summary>
    public const string InputCaptureActive = "input-capture-active";
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

    /// <summary>
    /// Takes or releases this connection's input-capture lease on one device - the mapping
    /// editor's way of saying "the user is binding a button on this pad, so stop running its
    /// mappings while they press it". The host keeps polling and publishing the device's
    /// samples; only its mapping dispatch stops. A null device id releases the lease, which is
    /// what a cancel, a timeout and an editor close send.
    /// <para>
    /// Defaulted to a no-op so a host that predates the capture - or a fake in a test - still
    /// compiles: the request is acked and simply suppresses nothing.
    /// </para>
    /// </summary>
    void SetInputCaptureDevice(string? deviceId) { }

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

    /// <summary>
    /// The input backend the daemon is actually reading gamepads through. Defaults to the
    /// default backend (SDL3 since S3) so a host that predates the selection - or a fake in
    /// a test - still serves valid snapshots; every snapshot carries it so a window shows the
    /// backend the daemon really runs instead of the one it was last asked for.
    /// </summary>
    InputBackend ActiveInputBackend => InputBackendText.Default;

    /// <summary>
    /// Why the daemon's selected backend could not start, or null when it did. Carried into
    /// the snapshot so a window can report a failed opt-in selection instead of leaving the
    /// user to guess which backend is in charge.
    /// </summary>
    string? InputBackendError => null;
}

/// <summary>
/// One connected device as the wire carries it: stable identity, display name and the logical
/// layout the UI groups it by.
/// </summary>
/// <param name="Controls">
/// Generic descriptions of the controls this device declares, in the fixed zone order, or null
/// when its platform has written no description yet. The U1 generic panel builds itself from
/// these, which is what lets an unknown model be configured without a per-model drawing. A
/// description is a declaration, not an observation - it says what the device's mapping claims,
/// never what has been pressed - and an absent description is "not described yet", never "no
/// controls". Omitted from the JSON entirely when null, so a daemon on a platform that
/// describes nothing writes exactly the bytes it always did.
/// </param>
public sealed record ConnectedDeviceSnapshot(
    string DeviceId,
    string Name,
    GamepadLayout Layout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GamepadControlDescriptor>? Controls = null);

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
