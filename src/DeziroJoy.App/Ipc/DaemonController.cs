using DeziroJoy.Core.Input;
using DeziroJoy.Core.Mapping;

namespace DeziroJoy.App.Ipc;

public enum DaemonConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

public enum MutationOutcome
{
    Accepted,
    Refused,
    Unknown,
}

public readonly record struct MutationResult(MutationOutcome Outcome, long Revision, string? ErrorCode, string? ErrorMessage)
{
    public static MutationResult Accepted(long revision) => new(MutationOutcome.Accepted, revision, null, null);

    public static MutationResult Refused(long revision, string? code, string? message)
        => new(MutationOutcome.Refused, revision, code, message);

    public static MutationResult Unknown() => new(MutationOutcome.Unknown, 0, null, null);
}

public sealed class DaemonUnavailableException(string message) : Exception(message);

public sealed class DaemonHandshakeException(string message) : Exception(message);

public sealed class DaemonDisconnectedException() : Exception("the daemon connection was lost");

public sealed class DaemonProtocolException(string message) : Exception(message);

public interface IDaemonChannel : IAsyncDisposable
{
    Task SendAsync(IpcMessage message, CancellationToken cancellationToken);

    Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken);
}

public interface IDaemonEndpoint
{
    Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken);

    Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken);
}

public sealed class DaemonController : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IDaemonEndpoint _endpoint;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _pendingGate = new();
    private readonly object _snapshotGate = new();
    private readonly object _inputGate = new();
    private readonly Dictionary<string, TaskCompletionSource<IpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InputSampleMessage> _inputSamples = new(StringComparer.Ordinal);
    private readonly SnapshotSequenceTracker _sequence = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _reconnectDelay;

    private IDaemonChannel? _channel;
    private CancellationTokenSource? _lifetime;
    private Task? _receiver;
    private Task? _heartbeat;
    private Task? _reconnector;
    private string? _daemonInstanceId;
    private long _nextRequestId;
    private long _lastInputSequence;
    private int _resyncRunning;
    private int _reconnectRunning;
    private int _shutdownRequested;
    private bool _disposed;

    public DaemonController(
        IDaemonEndpoint endpoint,
        TimeSpan? requestTimeout = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? reconnectDelay = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(5);
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1);
        ClientInstanceId = Guid.NewGuid().ToString("N");
    }

    public string ClientInstanceId { get; }

    public DaemonConnectionState State { get; private set; } = DaemonConnectionState.Disconnected;

    public StateSnapshot? Snapshot { get; private set; }

    public string? DaemonInstanceId => _daemonInstanceId;

    public long Revision { get; private set; }

    public string? LastError { get; private set; }

    public event Action<StateSnapshot>? SnapshotChanged;

    public event Action<InputSampleMessage>? InputSampled;

    /// <summary>The latest sample per device, including samples received during the handshake.</summary>
    public IReadOnlyList<InputSampleMessage> InputSamples
    {
        get { lock (_inputGate) { return _inputSamples.Values.ToArray(); } }
    }

    public event Action? Connected;

    public event Action? Disconnected;

    public async Task<StateSnapshot> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref _shutdownRequested, 0);
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<StateSnapshot> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State == DaemonConnectionState.Connected && Snapshot is not null)
        {
            return Snapshot;
        }

        if (State == DaemonConnectionState.Connecting)
        {
            throw new InvalidOperationException("a daemon connection attempt is already in progress");
        }

        State = DaemonConnectionState.Connecting;
        await CleanupConnectionAsync().ConfigureAwait(false);
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        try
        {
            if (!await _endpoint.EnsureReadyAsync(allowLaunch: true, timeout.Token).ConfigureAwait(false))
            {
                throw new DaemonUnavailableException("the resident daemon could not be found or started");
            }

            var channel = await _endpoint.OpenChannelAsync(timeout.Token).ConfigureAwait(false);
            _channel = channel;

            var hello = await ExchangeDirectAsync(
                channel,
                new HelloMessage(
                    "DeziroJoy.GUI",
                    IpcContract.ProtocolVersion,
                    ClientInstanceId,
                    IpcSession.Current,
                    SubscribeInputSamples: true),
                message => message is HelloAckMessage,
                timeout.Token).ConfigureAwait(false);
            var ack = (HelloAckMessage)hello;
            if (!ack.Accepted)
            {
                throw new DaemonHandshakeException(ack.RejectReason ?? "the daemon refused the handshake");
            }

            _daemonInstanceId = ack.DaemonInstanceId;
            Revision = ack.Revision;
            ResetInputSamples();

            var requestId = NextRequestId();
            var state = await ExchangeDirectAsync(
                channel,
                new GetStateMessage(requestId),
                message => message is StateChangedMessage { RequestId: var id } && id == requestId,
                timeout.Token).ConfigureAwait(false);
            var initial = (StateChangedMessage)state;
            if (initial.DaemonInstanceId != _daemonInstanceId)
            {
                throw new DaemonHandshakeException("the daemon instance changed during the handshake");
            }

            AdoptAuthoritative(initial, reset: true);
            State = DaemonConnectionState.Connected;
            LastError = null;
            _receiver = Task.Run(() => ReceiveLoopAsync(channel, lifetime.Token), CancellationToken.None);
            _heartbeat = Task.Run(() => HeartbeatLoopAsync(lifetime.Token), CancellationToken.None);
            Connected?.Invoke();
            return initial.Snapshot;
        }
        catch
        {
            State = DaemonConnectionState.Disconnected;
            await CleanupConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<StateSnapshot> GetStateAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        var requestId = NextRequestId();
        var completion = RegisterPending(requestId);

        try
        {
            await SendAsync(new GetStateMessage(requestId), cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(_requestTimeout, cancellationToken).ConfigureAwait(false);
            if (response is StateChangedMessage state)
            {
                return AdoptAuthoritative(state) ? state.Snapshot : Snapshot!;
            }

            if (response is ErrorMessage error)
            {
                throw new DaemonProtocolException($"{error.ErrorCode}: {error.Message}");
            }

            throw new DaemonProtocolException($"the daemon answered GetState with {response.GetType().Name}");
        }
        finally
        {
            UnregisterPending(requestId);
        }
    }

    public Task<MutationResult> ApplyAsync(
        string profileName,
        string profileJson,
        long expectedRevision,
        string expectedDaemonInstanceId,
        CancellationToken cancellationToken)
        => MutateAsync(
            requestId => new ApplyConfigurationMessage(requestId, expectedRevision, profileName, profileJson),
            cancellationToken,
            expectedRevision,
            expectedDaemonInstanceId);

    public Task<MutationResult> StartAsync(CancellationToken cancellationToken)
        => MutateAsync(requestId => new StartMessage(requestId, Revision), cancellationToken);

    public Task<MutationResult> StopAsync(CancellationToken cancellationToken)
        => MutateAsync(requestId => new StopMessage(requestId), cancellationToken);

    public Task<MutationResult> SetMappingEnabledAsync(bool enabled, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SetMappingEnabledMessage(requestId, enabled), cancellationToken);

    public Task<MutationResult> SetInjectionSuspendedAsync(bool suspended, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SetInjectionSuspendedMessage(requestId, suspended), cancellationToken);

    /// <summary>
    /// Pauses or resumes keyboard and mouse mapping for native gamepad input - the pause a
    /// foreground rule asks for, distinct from the user's own pause and from this connection's
    /// hijack lease. A refused or unknown outcome is reported in the result rather than thrown,
    /// so a rule that could not be honoured leaves the watcher's next poll to try again.
    /// </summary>
    public Task<MutationResult> SetNativeGamepadPausedAsync(bool paused, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SetNativeGamepadPausedMessage(requestId, paused), cancellationToken);

    public Task<MutationResult> SwitchPresetSlotAsync(string deviceId, int slot, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SwitchPresetSlotMessage(requestId, deviceId, slot), cancellationToken);

    public Task<MutationResult> DeletePresetSlotAsync(string deviceId, int slot, CancellationToken cancellationToken)
        => MutateAsync(requestId => new DeletePresetSlotMessage(requestId, deviceId, slot), cancellationToken);

    /// <summary>
    /// Opens or closes the floating screen keyboard. The daemon owns the flag every window
    /// reads, so a window that dismisses the keyboard itself asks here rather than hiding a
    /// window the daemon still believes is open; the snapshot that follows is what moves
    /// every window, which keeps one keyboard and one input lease between them. A refused or
    /// unknown outcome is reported in the result rather than thrown, so the window keeps what
    /// it has instead of vanishing on a failed request.
    /// </summary>
    public Task<MutationResult> SetScreenKeyboardAsync(bool open, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SetScreenKeyboardMessage(requestId, open), cancellationToken);

    /// <summary>
    /// Types one short string into whatever has keyboard focus, through the daemon's own
    /// output path - the floating screen keyboard's output. The daemon is the sole injection
    /// authority, so the window asks and the daemon injects. A refusal (no runtime to inject
    /// through, an empty or oversized string) is reported in the result rather than thrown,
    /// so the keyboard can say why nothing arrived.
    /// </summary>
    public Task<MutationResult> InjectTextAsync(string text, CancellationToken cancellationToken)
        => MutateAsync(requestId => new InjectTextMessage(requestId, text), cancellationToken);

    /// <summary>
    /// Taps one key - press, then release, with the modifiers held around it - into whatever
    /// has keyboard focus, through the daemon's own output path. What the floating screen
    /// keyboard's action keys need: 退格 sends a Backspace, 清空 sends Ctrl+A and then Delete.
    /// </summary>
    public Task<MutationResult> InjectKeyAsync(GamepadKey key, KeyModifiers modifiers, CancellationToken cancellationToken)
        => MutateAsync(requestId => new InjectKeyMessage(requestId, key, modifiers), cancellationToken);

    public async Task<MutationResult> ShutdownDaemonAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        MutationResult result;
        try
        {
            result = await MutateAsync(requestId => new ShutdownDaemonMessage(requestId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _shutdownRequested, 0);
            StartReconnect();
            throw;
        }

        if (result.Outcome != MutationOutcome.Accepted)
        {
            Interlocked.Exchange(ref _shutdownRequested, 0);
            StartReconnect();
        }

        return result;
    }

    public async Task<bool> RequestShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ShutdownDaemonAsync(cancellationToken).ConfigureAwait(false);
            return result.Outcome == MutationOutcome.Accepted;
        }
        catch (Exception exception) when (exception
            is InvalidOperationException or DaemonDisconnectedException or TimeoutException
            or OperationCanceledException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task<MutationResult> MutateAsync(
        Func<string, IpcMessage> build,
        CancellationToken cancellationToken,
        long? expectedRevision = null,
        string? expectedDaemonInstanceId = null)
    {
        EnsureConnected();
        if (!MatchesMutationBaseline(expectedRevision, expectedDaemonInstanceId))
        {
            return MutationBaselineConflict();
        }

        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (!MatchesMutationBaseline(expectedRevision, expectedDaemonInstanceId))
            {
                return MutationBaselineConflict();
            }

            var requestId = NextRequestId();
            var completion = RegisterPending(requestId);

            try
            {
                await SendAsync(build(requestId), cancellationToken).ConfigureAwait(false);
                var response = await completion.Task.WaitAsync(_requestTimeout, cancellationToken).ConfigureAwait(false);
                return response switch
                {
                    MutationAckMessage ack => Settle(ack),
                    ErrorMessage error => MutationResult.Refused(Revision, error.ErrorCode, error.Message),
                    _ => MutationResult.Unknown(),
                };
            }
            catch (Exception exception) when (exception
                is DaemonDisconnectedException or TimeoutException or OperationCanceledException
                or IOException or ObjectDisposedException)
            {
                TriggerResync();
                return MutationResult.Unknown();
            }
            finally
            {
                UnregisterPending(requestId);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private bool MatchesMutationBaseline(long? expectedRevision, string? expectedDaemonInstanceId)
        => (!expectedRevision.HasValue || Revision == expectedRevision.Value)
            && (expectedDaemonInstanceId is null
                || string.Equals(_daemonInstanceId, expectedDaemonInstanceId, StringComparison.Ordinal));

    private MutationResult MutationBaselineConflict()
        => MutationResult.Refused(
            Revision,
            IpcErrorCodes.RevisionMismatch,
            "the daemon configuration changed before the edit was sent");

    private MutationResult Settle(MutationAckMessage ack)
    {
        Revision = ack.Revision;
        return ack.Accepted
            ? MutationResult.Accepted(ack.Revision)
            : MutationResult.Refused(ack.Revision, ack.ErrorCode, ack.ErrorMessage);
    }

    private async Task<IpcMessage> ExchangeDirectAsync(
        IDaemonChannel channel,
        IpcMessage request,
        Func<IpcMessage, bool> isAnswer,
        CancellationToken cancellationToken)
    {
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (message is InputSampleMessage sample)
            {
                HandleInputSample(sample);
                continue;
            }

            if (isAnswer(message))
            {
                return message;
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
                await GetStateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            EnterDisconnected();
        }
    }

    private void StartReconnect()
    {
        if (_disposed || Volatile.Read(ref _shutdownRequested) != 0
            || Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) != 0)
        {
            return;
        }

        _reconnector = Task.Run(async () =>
        {
            try
            {
                while (!_disposed && Volatile.Read(ref _shutdownRequested) == 0)
                {
                    try
                    {
                        await Task.Delay(_reconnectDelay, _shutdown.Token).ConfigureAwait(false);
                        await ConnectAsync(_shutdown.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        LastError = exception.Message;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectRunning, 0);
            }
        }, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(IDaemonChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (!Route(message))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            EnterDisconnected();
        }
    }

    private bool Route(IpcMessage message)
    {
        switch (message)
        {
            case MutationAckMessage ack:
                CompletePending(ack.RequestId, ack);
                return true;
            case ErrorMessage error:
                CompletePending(error.RequestId, error);
                return true;
            case InputSampleMessage sample:
                return HandleInputSample(sample);
            case StateChangedMessage state when state.DaemonInstanceId != _daemonInstanceId:
                return false;
            case StateChangedMessage { RequestId: not null } answer:
                CompletePending(answer.RequestId!, answer);
                return true;
            case StateChangedMessage push:
                return HandlePush(push);
            default:
                return true;
        }
    }

    private bool HandlePush(StateChangedMessage push)
    {
        lock (_snapshotGate)
        {
            switch (_sequence.Evaluate(push.Snapshot.Sequence))
            {
                case SnapshotDisposition.Fresh:
                    Adopt(push);
                    return true;
                case SnapshotDisposition.Stale:
                    return true;
                case SnapshotDisposition.Gap:
                    TriggerResync();
                    return true;
                default:
                    throw new DaemonProtocolException($"unknown snapshot disposition for sequence {push.Snapshot.Sequence}");
            }
        }
    }

    private bool AdoptAuthoritative(StateChangedMessage state, bool reset = false)
    {
        lock (_snapshotGate)
        {
            if (!reset && Snapshot is { } current && state.Snapshot.Sequence < current.Sequence)
            {
                return false;
            }

            _sequence.Adopt(state.Snapshot.Sequence);
            Adopt(state);
            return true;
        }
    }

    private void Adopt(StateChangedMessage state)
    {
        Snapshot = state.Snapshot;
        Revision = state.Snapshot.Revision;
        SnapshotChanged?.Invoke(state.Snapshot);
    }

    private void ResetInputSamples()
    {
        lock (_inputGate)
        {
            _inputSamples.Clear();
            _lastInputSequence = 0;
        }
    }

    private bool HandleInputSample(InputSampleMessage sample)
    {
        if (sample.DaemonInstanceId != _daemonInstanceId)
        {
            return false;
        }

        lock (_inputGate)
        {
            if (sample.Sequence <= _lastInputSequence)
            {
                return true;
            }

            _lastInputSequence = sample.Sequence;
            _inputSamples[sample.DeviceId] = sample;
        }

        InputSampled?.Invoke(sample);
        return true;
    }

    private void TriggerResync()
    {
        if (State != DaemonConnectionState.Connected || Interlocked.CompareExchange(ref _resyncRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var token = _lifetime?.Token ?? CancellationToken.None;
                await GetStateAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _resyncRunning, 0);
            }
        }, CancellationToken.None);
    }

    private void EnterDisconnected()
    {
        if (_disposed || State != DaemonConnectionState.Connected)
        {
            return;
        }

        State = DaemonConnectionState.Disconnected;
        FailPending();
        Disconnected?.Invoke();
        StartReconnect();
    }

    private TaskCompletionSource<IpcMessage> RegisterPending(string requestId)
    {
        var completion = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            _pending.Add(requestId, completion);
        }

        return completion;
    }

    private void CompletePending(string requestId, IpcMessage message)
    {
        lock (_pendingGate)
        {
            if (_pending.Remove(requestId, out var completion))
            {
                completion.TrySetResult(message);
            }
        }
    }

    private void UnregisterPending(string requestId)
    {
        lock (_pendingGate)
        {
            _pending.Remove(requestId);
        }
    }

    private void FailPending()
    {
        lock (_pendingGate)
        {
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new DaemonDisconnectedException());
            }

            _pending.Clear();
        }
    }

    private async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_channel is null)
            {
                throw new DaemonDisconnectedException();
            }

            await _channel.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State != DaemonConnectionState.Connected)
        {
            throw new InvalidOperationException("the daemon controller is not connected");
        }
    }

    private string NextRequestId() => $"gui-{Interlocked.Increment(ref _nextRequestId)}";

    private async Task CleanupConnectionAsync()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        var channel = _channel;
        _channel = null;
        if (channel is not null)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        var receiver = _receiver;
        _receiver = null;
        if (receiver is not null)
        {
            try
            {
                await receiver.WaitAsync(_requestTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                LastError = exception.Message;
            }
        }

        var heartbeat = _heartbeat;
        _heartbeat = null;
        if (heartbeat is not null)
        {
            try
            {
                await heartbeat.WaitAsync(_requestTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                LastError = exception.Message;
            }
        }

        lifetime?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_reconnector is not null)
        {
            try
            {
                await _reconnector.WaitAsync(_requestTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                LastError = exception.Message;
            }
        }

        await CleanupConnectionAsync().ConfigureAwait(false);
        State = DaemonConnectionState.Disconnected;
        FailPending();
        _sendGate.Dispose();
        _mutationGate.Dispose();
        _connectGate.Dispose();
        _shutdown.Dispose();
    }
}
