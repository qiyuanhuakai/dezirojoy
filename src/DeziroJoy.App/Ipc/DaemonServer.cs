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
    private readonly DaemonStateStore _stateStore;
    private const string DirectConnectionId = "direct";
    private readonly Lock _sync = new();
    private readonly DaemonState? _restored;
    private readonly HashSet<string> _injectionSuspensions = new(StringComparer.Ordinal);

    private long _revision;
    private long _sequence;
    private string _profileName = string.Empty;
    private string _profileJson = string.Empty;
    private string? _lastError;
    private bool _desiredEnabled;
    private bool _injectionSuspended;

    /// <summary>Raised after every state change, carrying the full snapshot to publish.</summary>
    public event Action<StateChangedMessage>? StateChanged;

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
        _stateStore = stateStore;
        DaemonInstanceId = $"daemon-{(clock ?? new SystemClock()).UtcNow.Ticks:x16}";

        _restored = stateStore.Load();
        if (_restored is not null)
        {
            _desiredEnabled = _restored.MappingEnabled;
            _profileName = _restored.ProfileName;
            _profileJson = _restored.ProfileJson;
            _revision = _restored.Revision;
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
        return new StateChangedMessage(DaemonInstanceId, BuildSnapshot(), getState.RequestId);
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

            _profileStore.Save(profile);
            foreach (var deviceId in _host.ConnectedDeviceIds)
            {
                _host.Reload(deviceId, profile);
            }

            _profileName = apply.ProfileName;
            _profileJson = apply.ProfileJson;
            _lastError = null;
            _revision++;
            PublishStateChange();

            return new MutationAckMessage(apply.RequestId, true, _revision, _sequence);
        }
    }

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
        _stateStore.Save(new DaemonState(_desiredEnabled, _profileName, _profileJson, _revision));

        var message = new StateChangedMessage(DaemonInstanceId, BuildSnapshot());
        StateChanged?.Invoke(message);
        PublishToConnections(message);
    }

    private StateSnapshot BuildSnapshot() => new(
        Sequence: _sequence,
        Revision: _revision,
        Running: _host.IsRunning,
        MappingEnabled: _host.IsMappingEnabled,
        InjectorPid: _host.InjectorPid,
        InjectorIntegrity: _host.InjectorIntegrity,
        ProfileName: _profileName,
        LastError: _lastError,
        InjectedEvents: _host.InjectedEvents,
        BlockedEvents: _host.BlockedEvents,
        HasController: _host.HasController);
}
