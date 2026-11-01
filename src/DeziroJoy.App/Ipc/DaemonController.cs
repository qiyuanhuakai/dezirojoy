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
    private readonly Dictionary<string, TaskCompletionSource<IpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SnapshotSequenceTracker _sequence = new();

    private IDaemonChannel? _channel;
    private CancellationTokenSource? _lifetime;
    private Task? _receiver;
    private string? _daemonInstanceId;
    private long _nextRequestId;
    private int _resyncRunning;
    private bool _disposed;

    public DaemonController(IDaemonEndpoint endpoint, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint = endpoint;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        ClientInstanceId = Guid.NewGuid().ToString("N");
    }

    public string ClientInstanceId { get; }

    public DaemonConnectionState State { get; private set; } = DaemonConnectionState.Disconnected;

    public StateSnapshot? Snapshot { get; private set; }

    public long Revision { get; private set; }

    public string? LastError { get; private set; }

    public event Action<StateSnapshot>? SnapshotChanged;

    public event Action? Disconnected;

    public async Task<StateSnapshot> ConnectAsync(CancellationToken cancellationToken)
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
                new HelloMessage("DeziroJoy.GUI", IpcContract.ProtocolVersion, ClientInstanceId, IpcSession.Current),
                message => message is HelloAckMessage,
                timeout.Token).ConfigureAwait(false);
            var ack = (HelloAckMessage)hello;
            if (!ack.Accepted)
            {
                throw new DaemonHandshakeException(ack.RejectReason ?? "the daemon refused the handshake");
            }

            _daemonInstanceId = ack.DaemonInstanceId;
            Revision = ack.Revision;

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

            AdoptAuthoritative(initial);
            State = DaemonConnectionState.Connected;
            LastError = null;
            _receiver = Task.Run(() => ReceiveLoopAsync(channel, lifetime.Token), CancellationToken.None);
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
                AdoptAuthoritative(state);
                return state.Snapshot;
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

    public Task<MutationResult> ApplyAsync(string profileName, string profileJson, CancellationToken cancellationToken)
        => MutateAsync(
            requestId => new ApplyConfigurationMessage(requestId, Revision, profileName, profileJson),
            cancellationToken);

    public Task<MutationResult> StartAsync(CancellationToken cancellationToken)
        => MutateAsync(requestId => new StartMessage(requestId, Revision), cancellationToken);

    public Task<MutationResult> StopAsync(CancellationToken cancellationToken)
        => MutateAsync(requestId => new StopMessage(requestId), cancellationToken);

    public Task<MutationResult> SetMappingEnabledAsync(bool enabled, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SetMappingEnabledMessage(requestId, enabled), cancellationToken);

    public Task<MutationResult> SetInjectionSuspendedAsync(bool suspended, CancellationToken cancellationToken)
        => MutateAsync(requestId => new SetInjectionSuspendedMessage(requestId, suspended), cancellationToken);

    private async Task<MutationResult> MutateAsync(
        Func<string, IpcMessage> build,
        CancellationToken cancellationToken)
    {
        EnsureConnected();
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
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
            if (isAnswer(message))
            {
                return message;
            }
        }
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

    private void AdoptAuthoritative(StateChangedMessage state)
    {
        _sequence.Adopt(state.Snapshot.Sequence);
        Adopt(state);
    }

    private void Adopt(StateChangedMessage state)
    {
        Snapshot = state.Snapshot;
        Revision = state.Snapshot.Revision;
        SnapshotChanged?.Invoke(state.Snapshot);
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
        State = DaemonConnectionState.Disconnected;
        FailPending();
        Disconnected?.Invoke();
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

        lifetime?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CleanupConnectionAsync().ConfigureAwait(false);
        State = DaemonConnectionState.Disconnected;
        FailPending();
        _sendGate.Dispose();
        _mutationGate.Dispose();
    }
}
