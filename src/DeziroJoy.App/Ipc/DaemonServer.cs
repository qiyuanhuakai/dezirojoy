using System.Text;
using System.Text.Json;
using DeziroJoy.App.Services;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Ipc;

/// <summary>
/// The version-1 mapping control service: the daemon-side half of section 4.4. It owns the
/// configuration revision, the monotonic snapshot sequence and the daemon instance id, and
/// turns each request into exactly one response. State changes are published through
/// <see cref="StateChanged"/> so every connected GUI receives a full snapshot.
///
/// The protocol logic is transport-free and platform-free: <see cref="ProcessAsync"/> takes a
/// message and returns a message, so the whole contract is testable with a fake
/// <see cref="IDaemonHost"/> and no pipe. <see cref="ServeConnectionAsync"/> is the thin
/// framed transport wrapped around it.
/// </summary>
public sealed partial class DaemonServer
{
    private readonly IDaemonHost _host;
    private readonly IProfileStore _profileStore;
    private readonly PresetStore _presetStore;
    private readonly DaemonStateStore _stateStore;
    private const string DirectConnectionId = "direct";
    private const string InvalidPresetSlotErrorCode = "invalid-slot";
    private const string NoPresetConfigurationErrorCode = "no-configuration";
    private const string PrimaryPresetSlotErrorCode = "primary-preset";
    private const string LastPresetErrorCode = "last-preset";
    private const string LeaderPresetSwitchRequestId = "leader-key";
    private readonly Lock _sync = new();
    private readonly Lock _inputGate = new();
    private readonly Dictionary<string, InputSampleMessage> _lastInputSamples = new(StringComparer.Ordinal);
    private readonly DaemonState? _restored;
    private readonly HashSet<string> _injectionSuspensions = new(StringComparer.Ordinal);

    private long _revision;
    private long _sequence;
    private long _inputSequence;
    private string _profileName = string.Empty;
    private string _profileJson = string.Empty;
    private string? _lastError;
    private bool _desiredEnabled;
    private bool _injectionSuspended;

    /// <summary>The one persisted, app-wide preset slot every connected device follows.</summary>
    private int _activePresetSlot = 1;

    /// <summary>Raised after every state change, carrying the full snapshot to publish.</summary>
    public event Action<StateChangedMessage>? StateChanged;

    /// <summary>Raised only when physical input changes; consumers must not block the engine.</summary>
    public event Action<InputSampleMessage>? InputSampled;

    /// <summary>Raised by the transport after the shutdown acknowledgement is written.</summary>
    public event Action? ShutdownRequested;

    internal void RequestShutdown() => ShutdownRequested?.Invoke();

    /// <summary>Changes on every daemon start, so a GUI can tell a restarted daemon apart.</summary>
    public string DaemonInstanceId { get; }

    public DaemonServer(
        IDaemonHost host,
        IProfileStore profileStore,
        DaemonStateStore stateStore,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(stateStore);

        _host = host;
        _profileStore = profileStore;
        _presetStore = new PresetStore(profileStore);
        _stateStore = stateStore;
        DaemonInstanceId = $"daemon-{(clock ?? new SystemClock()).UtcNow.Ticks:x16}";
        _host.DevicesChanged += OnDevicesChanged;
        _host.InputSampled += OnInputSampled;

        _restored = stateStore.Load();
        if (_restored is not null)
        {
            _desiredEnabled = _restored.MappingEnabled;
            _profileName = _restored.ProfileName;
            _profileJson = _restored.ProfileJson;
            _revision = _restored.Revision;
            _activePresetSlot = _restored.ActivePresetSlot >= PresetStore.MinSlot
                ? _restored.ActivePresetSlot
                : PresetStore.MinSlot;
        }
        else
        {
            // First run: map per the existing configuration, exactly as the daemon always has.
            _desiredEnabled = true;
        }
    }

    /// <summary>The desired mapping-enabled state the daemon will resume on the next start.</summary>
    public bool DesiredMappingEnabled
    {
        get { lock (_sync) { return _desiredEnabled; } }
    }

    /// <summary>
    /// Handles one request and returns its single response. Mutations publish a
    /// <see cref="StateChanged"/> push; <see cref="GetStateMessage"/> and
    /// <see cref="HelloMessage"/> never do, so a connecting or polling GUI sees no
    /// spurious state.
    /// </summary>
    public Task<IpcMessage> ProcessAsync(IpcMessage request)
        => ProcessAsync(request, DirectConnectionId);

    internal Task<IpcMessage> ProcessAsync(IpcMessage request, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        return Task.FromResult(Process(request, connectionId));
    }

    internal void ReleaseConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_sync)
        {
            UpdateInjectionSuspension(connectionId, suspended: false);
        }
    }

    private IpcMessage Process(IpcMessage request, string connectionId)
    {
        switch (request)
        {
            case HelloMessage hello:
                return HandleHello(hello);

            case GetStateMessage getState:
                return HandleGetState(getState);

            case ApplyConfigurationMessage apply:
                return HandleApply(apply);

            case StartMessage start:
                return HandleStart(start);

            case StopMessage stop:
                return HandleStop(stop);

            case SetMappingEnabledMessage setEnabled:
                return HandleSetMappingEnabled(setEnabled);

            case SetInjectionSuspendedMessage setSuspended:
                return HandleSetInjectionSuspended(setSuspended, connectionId);

            case SwitchPresetSlotMessage switchSlot:
                return HandleSwitchPresetSlot(switchSlot);

            case DeletePresetSlotMessage deleteSlot:
                return HandleDeletePresetSlot(deleteSlot);

            case ShutdownDaemonMessage shutdown:
                return new MutationAckMessage(shutdown.RequestId, true, CurrentRevision(), CurrentSequence());

            default:
                // The union is closed and the deserializer rejects unknown kinds, so this is
                // a response message arriving at the daemon: a protocol violation, not a crash.
                return new ErrorMessage(string.Empty, "unexpected-message", $"the daemon cannot handle {request.GetType().Name}");
        }
    }

    private HelloAckMessage HandleHello(HelloMessage hello)
    {
        if (hello.ProtocolVersion != IpcContract.ProtocolVersion)
        {
            return new HelloAckMessage(
                IpcContract.ProtocolVersion,
                false,
                DaemonInstanceId,
                CurrentRevision(),
                $"the client speaks protocol version {hello.ProtocolVersion}, the daemon speaks {IpcContract.ProtocolVersion}");
        }

        if (!IsSessionAccepted(hello.SessionId, OperatingSystem.IsWindows(), IpcSession.Current))
        {
            return new HelloAckMessage(
                IpcContract.ProtocolVersion,
                false,
                DaemonInstanceId,
                CurrentRevision(),
                "the client belongs to a different Windows session");
        }

        return new HelloAckMessage(IpcContract.ProtocolVersion, true, DaemonInstanceId, CurrentRevision());
    }

    internal static bool IsSessionAccepted(int clientSessionId, bool isWindows, int daemonSessionId)
        => !isWindows || clientSessionId == daemonSessionId;

    private StateChangedMessage HandleGetState(GetStateMessage getState)
    {
        lock (_sync)
        {
            return new StateChangedMessage(DaemonInstanceId, BuildSnapshot(), getState.RequestId);
        }
    }

    private MutationAckMessage HandleApply(ApplyConfigurationMessage apply)
    {
        GamepadProfile profile;
        try
        {
            profile = ProfileSerializer.Deserialize(apply.ProfileJson);
        }
        catch (JsonException exception)
        {
            lock (_sync)
            {
                _lastError = exception.Message;
            }

            return new MutationAckMessage(
                apply.RequestId,
                false,
                CurrentRevision(),
                CurrentSequence(),
                DaemonErrorCodes.InvalidProfile,
                exception.Message);
        }

        lock (_sync)
        {
            if (apply.ExpectedRevision != _revision)
            {
                return RevisionMismatch(apply.RequestId, apply.ExpectedRevision);
            }

            // An apply lands in the device's active preset slot, not the bare device key:
            // slot 1 keeps the legacy key, every other slot is stored under its preset key.
            var applyDeviceId = SnapshotDeviceFor(profile.DeviceId);
            var rebound = !string.IsNullOrWhiteSpace(profile.DeviceId)
                && applyDeviceId is not null
                && applyDeviceId != profile.DeviceId;
            if (rebound)
            {
                profile = profile with { DeviceId = applyDeviceId };
            }

            if (applyDeviceId is null)
            {
                _profileStore.Save(profile);
            }
            else
            {
                _presetStore.SaveSlot(
                    applyDeviceId,
                    _activePresetSlot,
                    profile);
            }

            ReloadTargetDevice(profile);

            _profileName = apply.ProfileName;
            _profileJson = rebound ? ProfileSerializer.Serialize(profile) : apply.ProfileJson;
            _lastError = null;
            _revision++;
            PublishStateChange();

            return new MutationAckMessage(apply.RequestId, true, _revision, _sequence);
        }
    }

    /// <summary>
    /// The current apply target. A connected profile device keeps ownership; if it disconnected,
    /// the edit moves to the first remaining device instead of succeeding against absent hardware.
    /// With no connected device the named device remains available for offline persistence.
    /// </summary>
    private string? SnapshotDeviceFor(string? profileDeviceId)
    {
        var connected = _host.ConnectedDeviceIds;
        if (!string.IsNullOrWhiteSpace(profileDeviceId)
            && (connected.Count == 0 || connected.Contains(profileDeviceId, StringComparer.Ordinal)))
        {
            return profileDeviceId;
        }

        return connected.Count > 0 ? connected[0] : null;
    }

    private void ReloadTargetDevice(GamepadProfile profile)
    {
        if (SnapshotDeviceFor(profile.DeviceId) is { } target
            && _host.ConnectedDeviceIds.Contains(target, StringComparer.Ordinal))
        {
            _host.Reload(target, profile);
        }
    }

    /// <summary>
    /// Switches the app-wide active preset slot: every connected device holding a stored
    /// profile in the target slot is reloaded with it, a device holding nothing there
    /// keeps running its current profile, and the revision is bumped once so every GUI
    /// sees the new slot. A slot below 1 is refused without touching any state, and so is
    /// a switch no connected device can honor - with no pad connected there is nothing to
    /// reload and no running configuration to contradict, so the slot moves.
    /// </summary>
    private IpcMessage HandleSwitchPresetSlot(SwitchPresetSlotMessage request)
    {
        if (request.Slot < PresetStore.MinSlot)
        {
            return new ErrorMessage(
                request.RequestId,
                InvalidPresetSlotErrorCode,
                $"preset slots run from {PresetStore.MinSlot} upward");
        }

        lock (_sync)
        {
            var connected = _host.ConnectedDeviceIds;
            var holders = new List<(string DeviceId, GamepadProfile Profile)>();
            foreach (var deviceId in connected)
            {
                if (_presetStore.LoadSlot(deviceId, request.Slot) is { } profile)
                {
                    holders.Add((deviceId, profile));
                }
            }

            if (holders.Count == 0 && connected.Count > 0)
            {
                return new ErrorMessage(
                    request.RequestId,
                    NoPresetConfigurationErrorCode,
                    "no configuration");
            }

            // The snapshot body must follow the slot the engine now runs, or a GUI re-adopting on the revision bump would overwrite the new slot with the old one. The body keeps the bare device id: the GUI keys its drafts and applies by it. The slot is app-wide, so the body describes the requesting device when it holds the slot, else the first connected holder, else - no pad connected - whatever the store still holds for the named device.
            var bodyDeviceId = request.DeviceId;
            GamepadProfile? bodyProfile = null;
            foreach (var (deviceId, profile) in holders)
            {
                if (deviceId == request.DeviceId)
                {
                    bodyDeviceId = deviceId;
                    bodyProfile = profile;
                    break;
                }
            }

            if (bodyProfile is null && holders.Count > 0)
            {
                (bodyDeviceId, bodyProfile) = holders[0];
            }

            bodyProfile ??= _presetStore.LoadSlot(request.DeviceId, request.Slot);

            try
            {
                _host.SwitchPresetSlot(
                    request.Slot,
                    holders.ToDictionary(holder => holder.DeviceId, holder => holder.Profile, StringComparer.Ordinal));
            }
            catch (Exception exception)
            {
                return new MutationAckMessage(
                    request.RequestId,
                    false,
                    _revision,
                    _sequence,
                    "preset-switch-failed",
                    $"preset switch rolled back: {exception.Message}");
            }

            _activePresetSlot = request.Slot;
            if (bodyProfile is not null)
            {
                _profileName = bodyProfile.Name;
                _profileJson = ProfileSerializer.Serialize(bodyProfile with { DeviceId = bodyDeviceId });
            }

            _revision++;
            PublishStateChange();

            return new MutationAckMessage(request.RequestId, true, _revision, _sequence);
        }
    }

    /// <summary>
    /// Deletes one device's preset slot as one safe transaction: <see cref="PresetStore.DeleteSlot"/>
    /// repairs every Leader reference to the slot and removes it, then the affected runtimes are
    /// reloaded with the repaired profiles and the snapshot body follows them - so neither a live
    /// runtime nor a published profile is ever left carrying the deleted-slot reference, and a
    /// later apply cannot write it back. When the deleted slot was the app-wide active one the
    /// daemon moves every device that holds a surviving slot onto the highest survivor below it;
    /// a delete of any other slot leaves the active slot in place but still reloads the in-scope
    /// device when its active profile was repaired. A reload failure rolls the whole thing back -
    /// the store is restored and no revision is bumped. The revision is bumped and a snapshot
    /// published on success, so every connected GUI reconciles its selector. Slot 1 (the device's
    /// own profile) and the device's last remaining preset are refused without touching any state.
    /// </summary>
    private IpcMessage HandleDeletePresetSlot(DeletePresetSlotMessage request)
    {
        if (request.Slot < PresetStore.MinSlot)
        {
            return new ErrorMessage(
                request.RequestId,
                InvalidPresetSlotErrorCode,
                $"preset slots run from {PresetStore.MinSlot} upward");
        }

        lock (_sync)
        {
            var connected = _host.ConnectedDeviceIds;
            var deviceId = !string.IsNullOrWhiteSpace(request.DeviceId)
                ? request.DeviceId
                : connected.Count > 0 ? connected[0] : null;
            if (deviceId is null)
            {
                return new ErrorMessage(
                    request.RequestId,
                    NoPresetConfigurationErrorCode,
                    "没有可删除预设的设备。");
            }

            var inUse = _presetStore.SlotsInUse(deviceId);
            if (request.Slot == PresetStore.MinSlot)
            {
                return new ErrorMessage(
                    request.RequestId,
                    PrimaryPresetSlotErrorCode,
                    "不能删除设备的主预设（槽位 1）。");
            }

            if (inUse.Count <= 1)
            {
                return new ErrorMessage(
                    request.RequestId,
                    LastPresetErrorCode,
                    "不能删除最后一套预设。");
            }

            if (!inUse.Contains(request.Slot))
            {
                return new ErrorMessage(
                    request.RequestId,
                    NoPresetConfigurationErrorCode,
                    "该槽位没有配置。");
            }

            var wasActive = request.Slot == _activePresetSlot;

            // Capture a rollback snapshot of the device's current slots. The repair rewrites
            // surviving profiles and the delete removes one; if the runtime reload below does not
            // land, both must be undone together, so the whole device is restored from here.
            var rollback = new Dictionary<int, GamepadProfile>();
            foreach (var slot in inUse)
            {
                if (_presetStore.LoadSlot(deviceId, slot) is { } profile)
                {
                    rollback[slot] = profile;
                }
            }

            // Repair + delete on the store first, so every profile the runtimes reload below is
            // already free of the deleted-slot reference.
            _presetStore.DeleteSlot(deviceId, request.Slot);

            // Whether the in-scope device's still-active profile was repaired by the delete.
            var activeAfter = _presetStore.LoadSlot(deviceId, _activePresetSlot);
            var activeRepaired = !wasActive
                && rollback.TryGetValue(_activePresetSlot, out var activeBefore)
                && activeAfter is not null
                && activeBefore != activeAfter;

            var newActive = _activePresetSlot;
            try
            {
                if (wasActive)
                {
                    // Move the whole app off the active slot onto the highest surviving slot
                    // below it, reloading every connected device that holds it with the repaired
                    // profile - so no running engine or leader key is left aimed at a deleted slot.
                    newActive = inUse
                        .Where(slot => slot != request.Slot && slot < request.Slot)
                        .DefaultIfEmpty(PresetStore.MinSlot)
                        .Max();

                    var holders = new Dictionary<string, GamepadProfile>(StringComparer.Ordinal);
                    foreach (var id in connected)
                    {
                        if (_presetStore.LoadSlot(id, newActive) is { } profile)
                        {
                            holders[id] = profile;
                        }
                    }

                    _host.SwitchPresetSlot(newActive, holders);
                }
                else if (activeRepaired && connected.Contains(deviceId, StringComparer.Ordinal))
                {
                    // The active slot is unchanged, but its profile was repaired: reload it so the
                    // live runtime matches the store rather than keeping the stale leader.
                    _host.Reload(deviceId, activeAfter!);
                }
            }
            catch (Exception exception)
            {
                foreach (var (slot, profile) in rollback)
                {
                    _presetStore.SaveSlot(deviceId, slot, profile);
                }

                return new MutationAckMessage(
                    request.RequestId,
                    false,
                    _revision,
                    _sequence,
                    "preset-delete-failed",
                    $"删除预设已回滚：{exception.Message}");
            }

            _activePresetSlot = newActive;

            // The snapshot body must follow the repaired active profile, or a client re-adopting
            // it would carry the deleted-slot reference and a later apply could write it back into
            // the repaired store. The body keeps the bare device id, exactly as the switch path.
            GamepadProfile? bodyProfile = wasActive
                ? _presetStore.LoadSlot(deviceId, newActive)
                : activeRepaired ? activeAfter : null;
            if (bodyProfile is not null)
            {
                _profileName = bodyProfile.Name;
                _profileJson = ProfileSerializer.Serialize(bodyProfile with { DeviceId = deviceId });
            }

            _revision++;
            PublishStateChange();

            return new MutationAckMessage(request.RequestId, true, _revision, _sequence);
        }
    }

    /// <summary>
    /// The daemon-side entry point for a leader-key preset switch.
    /// <see cref="EngineHost.PresetSwitchRequested"/> is raised from the tick thread and
    /// the host owns no preset store, so without a daemon-side subscriber the leader key
    /// is dead in daemon mode; <see cref="DaemonHost"/> wires the event here. Runs the
    /// same logic as a GUI switch under a synthetic request id - the slot is app-wide,
    /// so no device is named - and the tick thread calls it synchronously, exactly like
    /// the reloads the switch performs.
    /// </summary>
    internal void RequestPresetSlotSwitch(int slot)
        => Process(new SwitchPresetSlotMessage(LeaderPresetSwitchRequestId, string.Empty, slot), DirectConnectionId);

    private MutationAckMessage HandleStart(StartMessage start)
    {
        lock (_sync)
        {
            if (start.ExpectedRevision != _revision)
            {
                return RevisionMismatch(start.RequestId, start.ExpectedRevision);
            }

            if (_host.IsRunning)
            {
                if (_host.IsMappingEnabled && _desiredEnabled)
                {
                    return new MutationAckMessage(start.RequestId, true, _revision, _sequence);
                }

                _host.SetMappingEnabled(true);
                _desiredEnabled = true;
                _lastError = null;
                PublishStateChange();
                return new MutationAckMessage(start.RequestId, true, _revision, _sequence);
            }

            var error = TryStartMapping();
            if (error is not null)
            {
                return new MutationAckMessage(
                    start.RequestId,
                    false,
                    _revision,
                    _sequence,
                    DaemonErrorCodes.InjectionActive,
                    error);
            }

            return new MutationAckMessage(start.RequestId, true, _revision, _sequence);
        }
    }

    private MutationAckMessage HandleStop(StopMessage stop)
    {
        lock (_sync)
        {
            var wasRunning = _host.IsRunning;
            if (!wasRunning && !_desiredEnabled)
            {
                return new MutationAckMessage(stop.RequestId, true, _revision, _sequence);
            }

            if (wasRunning)
            {
                _host.StopMapping();
            }

            _desiredEnabled = false;
            PublishStateChange();

            return new MutationAckMessage(stop.RequestId, true, _revision, _sequence);
        }
    }

    private MutationAckMessage HandleSetMappingEnabled(SetMappingEnabledMessage request)
    {
        lock (_sync)
        {
            if (_host.IsMappingEnabled != request.Enabled)
            {
                _host.SetMappingEnabled(request.Enabled);
                PublishStateChange();
            }

            return new MutationAckMessage(request.RequestId, true, _revision, _sequence);
        }
    }

    private MutationAckMessage HandleSetInjectionSuspended(SetInjectionSuspendedMessage request, string connectionId)
    {
        lock (_sync)
        {
            UpdateInjectionSuspension(connectionId, request.Suspended);
            return new MutationAckMessage(request.RequestId, true, _revision, _sequence);
        }
    }

    private void UpdateInjectionSuspension(string connectionId, bool suspended)
    {
        if (suspended)
        {
            _injectionSuspensions.Add(connectionId);
        }
        else
        {
            _injectionSuspensions.Remove(connectionId);
        }

        var shouldSuspend = _injectionSuspensions.Count > 0;
        if (shouldSuspend == _injectionSuspended)
        {
            return;
        }

        _host.SetInjectionSuspended(shouldSuspend);
        _injectionSuspended = shouldSuspend;
    }

    /// <summary>
    /// Starts the mapping loop. Returns the refusal message when another process holds the
    /// injection mutex, or null on success. The caller only acks after this returns, so a
    /// start ack always follows a real mutex acquire.
    /// </summary>
    private string? TryStartMapping()
    {
        try
        {
            _host.Start();
        }
        catch (InjectionAlreadyActiveException exception)
        {
            _lastError = exception.Message;
            return exception.Message;
        }

        _host.SetMappingEnabled(true);
        _desiredEnabled = true;
        _lastError = null;
        PublishStateChange();
        return null;
    }

    private MutationAckMessage RevisionMismatch(string requestId, long expected)
        => new MutationAckMessage(
            requestId,
            false,
            CurrentRevision(),
            CurrentSequence(),
            IpcErrorCodes.RevisionMismatch,
            $"expected revision {expected}, the daemon is at {CurrentRevision()}");

    private long CurrentRevision()
    {
        lock (_sync)
        {
            return _revision;
        }
    }

    private long CurrentSequence()
    {
        lock (_sync)
        {
            return _sequence;
        }
    }

    /// <summary>
    /// Bumps the sequence, persists the desired state and publishes the snapshot. Callers
    /// hold <see cref="_sync"/>; pipe writes are queued so a slow GUI cannot hold up the
    /// mapping loop.
    /// </summary>
    private void PublishStateChange()
    {
        _sequence++;
        _stateStore.Save(new DaemonState(_desiredEnabled, _profileName, _profileJson, _revision, _activePresetSlot));

        var message = new StateChangedMessage(DaemonInstanceId, BuildSnapshot());
        StateChanged?.Invoke(message);
        PublishToConnections(message);
    }

    private void OnDevicesChanged()
    {
        lock (_inputGate)
        {
            var connected = _host.ConnectedDeviceIds;
            foreach (var deviceId in _lastInputSamples.Keys.Where(id => !connected.Contains(id, StringComparer.Ordinal)).ToList())
            {
                _lastInputSamples.Remove(deviceId);
            }
        }

        lock (_sync)
        {
            _sequence++;
            var message = new StateChangedMessage(DaemonInstanceId, BuildSnapshot());
            StateChanged?.Invoke(message);
            PublishToConnections(message);
        }
    }

    private void ResetInputSampleBaselines()
    {
        lock (_inputGate)
        {
            _lastInputSamples.Clear();
        }
    }

    private void OnInputSampled(string deviceId, DeziroJoy.Core.Input.IGamepadState state)
    {
        InputSampleMessage message;
        lock (_inputGate)
        {
            if (_lastInputSamples.TryGetValue(deviceId, out var previous) && previous.Matches(state))
            {
                return;
            }

            message = new InputSampleMessage(
                DaemonInstanceId,
                ++_inputSequence,
                deviceId,
                state.PressedButtons,
                state.LeftStickX,
                state.LeftStickY,
                state.RightStickX,
                state.RightStickY,
                state.ExtraButtons.Count > 0 ? state.ExtraButtons.ToArray() : null);
            _lastInputSamples[deviceId] = message;
        }

        InputSampled?.Invoke(message);
        PublishInputSample(message);
    }

    private StateSnapshot BuildSnapshot()
    {
        var profileName = _profileName;
        var profileJson = _profileJson;
        var connected = _host.ConnectedDeviceIds;
        if (connected.Count > 0)
        {
            string? profileDeviceId = null;
            var malformedProfile = false;
            if (!string.IsNullOrEmpty(profileJson))
            {
                try
                {
                    profileDeviceId = ProfileSerializer.Deserialize(profileJson).DeviceId;
                }
                catch (JsonException)
                {
                    malformedProfile = true;
                }
            }

            var targetDeviceId = profileDeviceId is not null
                && connected.Contains(profileDeviceId, StringComparer.Ordinal)
                    ? profileDeviceId
                    : connected[0];
            var targetDisconnected = profileDeviceId is not null
                && !connected.Contains(profileDeviceId, StringComparer.Ordinal);
            if (malformedProfile || targetDisconnected || string.IsNullOrEmpty(profileJson))
            {
                var profile = _host.GetProfile(targetDeviceId)
                    ?? _presetStore.LoadSlot(targetDeviceId, _activePresetSlot);
                if (profile is not null)
                {
                    profileName = profile.Name;
                    profileJson = ProfileSerializer.Serialize(profile with { DeviceId = targetDeviceId });
                }
                else
                {
                    profileName = string.Empty;
                    profileJson = string.Empty;
                }
            }
        }

        return new StateSnapshot(
            Sequence: _sequence,
            Revision: _revision,
            Running: _host.IsRunning,
            MappingEnabled: _host.IsMappingEnabled,
            InjectorPid: _host.InjectorPid,
            InjectorIntegrity: _host.InjectorIntegrity,
            ProfileName: profileName,
            LastError: _lastError,
            InjectedEvents: _host.InjectedEvents,
            BlockedEvents: _host.BlockedEvents,
            HasController: _host.HasController,
            ActivePresetSlot: _activePresetSlot)
        {
            ProfileJson = string.IsNullOrEmpty(profileJson) ? null : profileJson,
            Devices = _host.ConnectedDevices,
        };
    }
}
