using System.Text.Json;
using System.Text.Json.Serialization;
using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Ipc;

/// <summary>
/// Version-1 wire contract: the pipe identity and the protocol version. The pipe and
/// the injection mutex are two different kernel objects with two different names
/// (section 4.3); only the pipe name belongs here.
/// </summary>
internal static class IpcSession
{
    public static int Current => System.Diagnostics.Process.GetCurrentProcess().SessionId;
}

public static class IpcContract
{
    /// <summary>The protocol version this assembly speaks; bumped only on a breaking change.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The .NET pipe name: no <c>Global\</c> prefix and no <c>\\.\pipe\</c> path. This is
    /// what the named-pipe client and server APIs take.
    /// </summary>
    public const string PipeName = "DeziroJoy.Mapping";

    /// <summary>
    /// The full Windows path of the same pipe, for display and diagnostics only. Never
    /// hand this to the .NET pipe APIs - they take <see cref="PipeName"/>.
    /// </summary>
    public const string PipeWindowsPath = @"\\.\pipe\DeziroJoy.Mapping";
}

/// <summary>Wire error codes carried by <see cref="MutationAckMessage"/> and <see cref="ErrorMessage"/>.</summary>
public static class IpcErrorCodes
{
    /// <summary>
    /// The daemon refused the mutation because the expected revision no longer matches
    /// its current one. The ack carries the current revision; the GUI re-reads state
    /// with <see cref="GetStateMessage"/> and decides - it never replays the mutation.
    /// </summary>
    public const string RevisionMismatch = "revision-mismatch";
}

/// <summary>
/// One message on the mapping control channel. Each message carries only the fields its
/// own semantics need; the <c>kind</c> discriminator is written by the serializer.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(HelloAckMessage), "hello-ack")]
[JsonDerivedType(typeof(GetStateMessage), "get-state")]
[JsonDerivedType(typeof(ApplyConfigurationMessage), "apply-configuration")]
[JsonDerivedType(typeof(StartMessage), "start")]
[JsonDerivedType(typeof(StopMessage), "stop")]
[JsonDerivedType(typeof(SetMappingEnabledMessage), "set-mapping-enabled")]
[JsonDerivedType(typeof(SetInjectionSuspendedMessage), "set-injection-suspended")]
[JsonDerivedType(typeof(SetNativeGamepadPausedMessage), "set-native-gamepad-paused")]
[JsonDerivedType(typeof(SwitchPresetSlotMessage), "switch-preset-slot")]
[JsonDerivedType(typeof(DeletePresetSlotMessage), "delete-preset-slot")]
[JsonDerivedType(typeof(ShutdownDaemonMessage), "shutdown-daemon")]
[JsonDerivedType(typeof(MutationAckMessage), "mutation-ack")]
[JsonDerivedType(typeof(StateChangedMessage), "state-changed")]
[JsonDerivedType(typeof(InputSampleMessage), "input-sample")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record IpcMessage;

/// <summary>
/// GUI to daemon: handshake. <see cref="ProtocolVersion"/> is what the client speaks,
/// so the daemon can refuse a version it does not implement instead of guessing;
/// <see cref="ClientInstanceId"/> identifies this GUI run, and <see cref="SessionId"/>
/// lets the Windows daemon reject a client from another session. Input samples are opt-in:
/// older clients omit the flag and therefore never receive a message kind they do not know.
/// </summary>
public sealed record HelloMessage(
    string ClientId,
    int ProtocolVersion,
    string ClientInstanceId,
    int SessionId,
    bool SubscribeInputSamples = false) : IpcMessage;

/// <summary>
/// Daemon to GUI: handshake answer. <see cref="Accepted"/> is false when the versions
/// do not match, with <see cref="RejectReason"/> explaining why; <see cref="Revision"/>
/// is the daemon's current configuration revision. <see cref="DaemonInstanceId"/>
/// changes whenever the daemon restarts, so the GUI can tell its session is stale.
/// </summary>
public sealed record HelloAckMessage(
    int ProtocolVersion,
    bool Accepted,
    string DaemonInstanceId,
    long Revision,
    string? RejectReason = null) : IpcMessage;

/// <summary>GUI to daemon: pull one full snapshot.</summary>
public sealed record GetStateMessage(string RequestId) : IpcMessage;

/// <summary>
/// GUI to daemon: submit a configuration change. <see cref="ProfileJson"/> is the
/// serialized profile body itself, never a file path - the daemon must not have to
/// read a location only the GUI can read, and "did it change" must be decidable from
/// the message alone. <see cref="ProfileName"/> is the profile's display name, carried
/// so the daemon can report which profile is active without parsing the body.
/// <see cref="ExpectedRevision"/> is the revision the GUI believes is current; a
/// mismatch is refused with <see cref="IpcErrorCodes.RevisionMismatch"/>.
/// </summary>
public sealed record ApplyConfigurationMessage(
    string RequestId,
    long ExpectedRevision,
    string ProfileName,
    string ProfileJson) : IpcMessage;

/// <summary>GUI to daemon: request mapping start, valid only at <see cref="ExpectedRevision"/>.</summary>
public sealed record StartMessage(string RequestId, long ExpectedRevision) : IpcMessage;

/// <summary>GUI to daemon: request mapping stop. The daemon itself keeps running.</summary>
public sealed record StopMessage(string RequestId) : IpcMessage;

/// <summary>GUI to daemon: pause or resume dispatch without stopping the mapping loop.</summary>
public sealed record SetMappingEnabledMessage(string RequestId, bool Enabled) : IpcMessage;

/// <summary>GUI to daemon: suspend dispatch while this connection's explicit hijack lease owns input.</summary>
public sealed record SetInjectionSuspendedMessage(string RequestId, bool Suspended) : IpcMessage;

/// <summary>
/// GUI to daemon: pause or resume keyboard and mouse mapping so the program in front reads the
/// gamepad itself. Deliberately a third pause, distinct from both of the others:
/// <see cref="SetMappingEnabledMessage"/> is the user's own pause and
/// <see cref="SetInjectionSuspendedMessage"/> is this connection's hijack lease, and the three
/// must never lift one another. It is what a foreground rule for a native-gamepad program asks
/// for, so the daemon - which owns mapping in daemon mode - can honour the rule the GUI's
/// watcher resolved. The mapping loop keeps running and the injection mutex is untouched, so
/// the resident model survives it exactly as it survives the other two.
/// </summary>
public sealed record SetNativeGamepadPausedMessage(string RequestId, bool Paused) : IpcMessage;

/// <summary>
/// GUI to daemon: switch the active preset slot. The slot is app-wide rather than
/// per-device: the daemon reloads every connected device that holds a stored profile
/// in the target slot, and the one active slot the whole app reads moves with it. Slots
/// have no upper bound - the initial four and any slot the user added since are ordinary
/// targets - but a slot below <see cref="DeziroJoy.Core.Profiles.PresetStore.MinSlot"/>
/// is refused with an <see cref="ErrorMessage"/> instead of a mutation ack, and so is a
/// switch when devices are connected but none of them holds the target slot.
/// <para>
/// <see cref="DeviceId"/> still travels on the wire but the daemon ignores it: a
/// switch no longer names a device. It is kept for soft compatibility with existing
/// clients, so the protocol version does not bump.
/// </para>
/// </summary>
public sealed record SwitchPresetSlotMessage(string RequestId, string DeviceId, int Slot) : IpcMessage;

/// <summary>
/// GUI to daemon: delete one device's preset slot. Like a switch, the slot is app-wide in
/// effect but stored per device, so <see cref="DeviceId"/> names the device whose slot is
/// removed and <see cref="Slot"/> the slot number. The daemon deletes the slot's stored
/// profile, repairs any Leader mapping that referenced it, and - when the deleted slot was
/// the active one - moves the whole app to a surviving slot before acknowledging, so no
/// running engine or leader key is ever left pointing at a slot that is gone. Slot 1 (the
/// device's own profile) and the device's last remaining preset are refused. The refusal is
/// a mutation ack or error message, never a silent success.
/// </summary>
public sealed record DeletePresetSlotMessage(string RequestId, string DeviceId, int Slot) : IpcMessage;

/// <summary>GUI/tray to daemon: release held input and exit the resident process.</summary>
public sealed record ShutdownDaemonMessage(string RequestId) : IpcMessage;

/// <summary>
/// Daemon to GUI: a mutation has been settled. <see cref="RequestId"/> echoes the
/// request it answers; on refusal <see cref="ErrorCode"/> says why,
/// <see cref="ErrorMessage"/> is the human-readable form for the UI, and
/// <see cref="Revision"/> carries the daemon's current revision.
/// </summary>
public sealed record MutationAckMessage(
    string RequestId,
    bool Accepted,
    long Revision,
    long Sequence,
    string? ErrorCode = null,
    string? ErrorMessage = null) : IpcMessage;

/// <summary>
/// Daemon to GUI: a full snapshot, either pushed on change or answering a
/// <see cref="GetStateMessage"/> - <see cref="RequestId"/> echoes that request and is
/// null for a push. <see cref="DaemonInstanceId"/> travels with every snapshot so the
/// GUI can tell a restarted daemon from the one it shook hands with, without relying
/// on the connection that delivered it. Snapshots are never incremental.
/// </summary>
public sealed record StateChangedMessage(
    string DaemonInstanceId,
    StateSnapshot Snapshot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RequestId = null)
    : IpcMessage;

/// <summary>
/// Daemon to opted-in GUI: one changed physical input state. Sample sequence numbers are
/// independent of snapshot revisions and may skip when a slow connection coalesces frames.
/// </summary>
public sealed record InputSampleMessage(
    string DaemonInstanceId,
    long Sequence,
    string DeviceId,
    GamepadButtonMask PressedButtons,
    float LeftStickX,
    float LeftStickY,
    float RightStickX,
    float RightStickY,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? ExtraButtons = null)
    : IpcMessage
{
    public GamepadState ToGamepadState() => GamepadState.Create(
        PressedButtons,
        LeftStickX,
        LeftStickY,
        RightStickX,
        RightStickY,
        ExtraButtons);

    public bool Matches(IGamepadState state) => PressedButtons == state.PressedButtons
        && LeftStickX == state.LeftStickX
        && LeftStickY == state.LeftStickY
        && RightStickX == state.RightStickX
        && RightStickY == state.RightStickY
        && (ExtraButtons ?? Array.Empty<int>()).SequenceEqual(state.ExtraButtons);
}

/// <summary>Daemon to GUI: the request failed; <see cref="RequestId"/> echoes the request.</summary>
public sealed record ErrorMessage(string RequestId, string ErrorCode, string Message) : IpcMessage;

/// <summary>
/// The complete daemon state, carried whole by every snapshot. The surface is small
/// (running or not, the active revision, injector identity, injection counters, the
/// active preset slot), so a full snapshot is cheaper to get right than a delta.
/// </summary>
public sealed record StateSnapshot(
    long Sequence,
    long Revision,
    bool Running,
    bool MappingEnabled,
    int InjectorPid,
    string? InjectorIntegrity,
    string ProfileName,
    string? LastError,
    long InjectedEvents,
    long BlockedEvents,
    bool HasController,
    int ActivePresetSlot = 1)
{
    /// <summary>The active profile body at <see cref="Revision"/>, used to rebase GUI drafts.</summary>
    public string? ProfileJson { get; init; }

    /// <summary>Real devices currently owned by the daemon, in stable provider order.</summary>
    public IReadOnlyList<ConnectedDeviceSnapshot> Devices { get; init; } = [];

    /// <summary>
    /// The temporary layer each connected device is running, with the mappings that layer puts
    /// in effect - one entry per device, in the same order as <see cref="Devices"/>. A device
    /// running its base mappings alone reports layer 0. The mapping ids resolve against the
    /// profile body this snapshot already carries.
    /// </summary>
    public IReadOnlyList<ActiveLayerSnapshot> Layers { get; init; } = [];

    /// <summary>
    /// Whether keyboard and mouse mapping is paused so the program in front reads the gamepad
    /// itself - the third pause, beside <see cref="MappingEnabled"/> and the per-connection
    /// hijack lease. A foreground rule sets it, so every connected GUI shows the same answer
    /// about what the program in front is being handed.
    /// </summary>
    public bool NativeGamepadPaused { get; init; }
}

/// <summary>What the GUI should do with a snapshot that just arrived.</summary>
public enum SnapshotDisposition
{
    /// <summary>The expected next snapshot: adopt and render it.</summary>
    Fresh,

    /// <summary>Already seen or superseded: drop it.</summary>
    Stale,

    /// <summary>Sequence numbers were skipped: re-read state with <see cref="GetStateMessage"/>.</summary>
    Gap,
}

/// <summary>
/// Tracks the daemon's monotonic snapshot sequence so the GUI can detect dropped and
/// reordered snapshots. The baseline is the highest sequence seen and only moves
/// forward, so a late frame can never drag the GUI's state backwards.
/// </summary>
public sealed class SnapshotSequenceTracker
{
    private long _lastSequence;

    private bool _started;

    /// <summary>The highest sequence seen so far; -1 before the first snapshot.</summary>
    public long LastSequence => _started ? _lastSequence : -1;

    /// <summary>
    /// Classifies an arriving snapshot. The first snapshot always establishes the
    /// baseline whatever its sequence, because the GUI connects mid-stream.
    /// </summary>
    public SnapshotDisposition Evaluate(long sequence)
    {
        if (!_started || sequence == _lastSequence + 1)
        {
            _started = true;
            _lastSequence = sequence;
            return SnapshotDisposition.Fresh;
        }

        if (sequence <= _lastSequence)
        {
            return SnapshotDisposition.Stale;
        }

        _lastSequence = sequence;
        return SnapshotDisposition.Gap;
    }

    /// <summary>
    /// Adopts a sequence unconditionally, for the authoritative snapshot that answers a
    /// re-sync <see cref="GetStateMessage"/> - or the first snapshot after a daemon
    /// restart, whose sequence restarted. Without this the re-sync answer would be
    /// refused as stale against the pre-gap baseline.
    /// </summary>
    public void Adopt(long sequence)
    {
        _started = true;
        _lastSequence = sequence;
    }
}

/// <summary>
/// JSON (de)serialization for the wire: compact, with property names as declared, so a
/// <see cref="ApplyConfigurationMessage.ProfileJson"/> body produced by the profile
/// serializer passes through as an opaque string with its semantics untouched.
/// Polymorphism rides on the <c>kind</c> discriminator declared on
/// <see cref="IpcMessage"/>.
/// </summary>
public static class IpcSerializer
{
    /// <summary>The options every writer and reader must share.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    /// <summary>Serializes one message, discriminator included.</summary>
    public static string Serialize(IpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return JsonSerializer.Serialize(message, Options);
    }

    /// <summary>
    /// Deserializes one message. An unknown <c>kind</c>, a missing discriminator or a
    /// JSON null all fail loudly with <see cref="JsonException"/> - a message the GUI
    /// cannot classify is never silently dropped.
    /// </summary>
    public static IpcMessage Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return JsonSerializer.Deserialize<IpcMessage>(json, Options)
            ?? throw new JsonException("IPC message deserialized to null.");
    }
}
