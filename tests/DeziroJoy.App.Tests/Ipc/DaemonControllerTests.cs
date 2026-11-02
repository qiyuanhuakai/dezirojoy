using DeziroJoy.App.Ipc;
using Xunit;

namespace DeziroJoy.App.Tests.Ipc;

public sealed class DaemonControllerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Connect_Performs_Ready_Handshake_And_Full_State_In_Order()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);

        var snapshot = await controller.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Connected, controller.State);
        Assert.Equal(0, snapshot.Sequence);
        Assert.Equal(1, endpoint.EnsureCalls);
        Assert.Equal(1, endpoint.OpenCalls);
        Assert.True(endpoint.LastAllowLaunch);
        Assert.Collection(
            endpoint.Channel.Sent,
            message => Assert.IsType<HelloMessage>(message),
            message => Assert.IsType<GetStateMessage>(message));
    }

    [Fact]
    public async Task Apply_And_Start_Use_The_Daemons_Current_Revision()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);

        endpoint.Channel.OnSend = message => message switch
        {
            ApplyConfigurationMessage apply => new MutationAckMessage(apply.RequestId, true, 1, 1),
            StartMessage start => new MutationAckMessage(start.RequestId, true, 1, 2),
            _ => null,
        };

        var applied = await controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);
        var started = await controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MutationOutcome.Accepted, applied.Outcome);
        Assert.Equal(MutationOutcome.Accepted, started.Outcome);
        Assert.Equal(0, Assert.Single(endpoint.Channel.Sent.OfType<ApplyConfigurationMessage>()).ExpectedRevision);
        Assert.Equal(1, Assert.Single(endpoint.Channel.Sent.OfType<StartMessage>()).ExpectedRevision);
    }

    [Fact]
    public async Task A_Revision_Conflict_Is_Refused_And_Never_Replayed()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is ApplyConfigurationMessage apply
            ? new MutationAckMessage(apply.RequestId, false, 7, 4, IpcErrorCodes.RevisionMismatch, "stale")
            : null;

        var result = await controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);

        Assert.Equal(MutationOutcome.Refused, result.Outcome);
        Assert.Equal(IpcErrorCodes.RevisionMismatch, result.ErrorCode);
        Assert.Equal(7, result.Revision);
        Assert.Single(endpoint.Channel.Sent.OfType<ApplyConfigurationMessage>());
    }

    [Fact]
    public async Task Only_One_Mutation_Is_In_Flight()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = null;

        var applyTask = controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);
        var startTask = controller.StartAsync(TestContext.Current.CancellationToken);
        var apply = await endpoint.Channel.WaitForSendAsync<ApplyConfigurationMessage>(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is StartMessage);

        endpoint.Channel.Push(new MutationAckMessage(apply.RequestId, true, 1, 1));
        var start = await endpoint.Channel.WaitForSendAsync<StartMessage>(TestContext.Current.CancellationToken);
        endpoint.Channel.Push(new MutationAckMessage(start.RequestId, true, 1, 2));

        Assert.Equal(MutationOutcome.Accepted, (await applyTask).Outcome);
        Assert.Equal(MutationOutcome.Accepted, (await startTask).Outcome);
    }

    [Fact]
    public async Task A_Queued_Apply_Keeps_Its_Original_Revision_And_Is_Not_Sent_After_A_Conflict()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message switch
        {
            ApplyConfigurationMessage apply => new MutationAckMessage(apply.RequestId, true, 1, 2),
            _ => null,
        };

        var firstMutation = controller.SetMappingEnabledAsync(false, TestContext.Current.CancellationToken);
        var firstRequest = await endpoint.Channel.WaitForSendAsync<SetMappingEnabledMessage>(
            TestContext.Current.CancellationToken);
        var applyTask = controller.ApplyAsync("Stale", "{}", 0, "daemon-1", TestContext.Current.CancellationToken);

        var externalRevision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            if (snapshot.Revision == 1)
            {
                externalRevision.TrySetResult();
            }
        };
        endpoint.Channel.Push(State("daemon-1", 1, revision: 1));
        await externalRevision.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        endpoint.Channel.Push(new MutationAckMessage(firstRequest.RequestId, true, 1, 2));
        var result = await applyTask;

        Assert.Equal(MutationOutcome.Refused, result.Outcome);
        Assert.Equal(IpcErrorCodes.RevisionMismatch, result.ErrorCode);
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is ApplyConfigurationMessage);
        Assert.Equal(MutationOutcome.Accepted, (await firstMutation).Outcome);

        var fromOldDaemon = await controller.ApplyAsync(
            "Stale", "{}", 1, "daemon-old", TestContext.Current.CancellationToken);
        Assert.Equal(MutationOutcome.Refused, fromOldDaemon.Outcome);
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is ApplyConfigurationMessage);
    }

    [Fact]
    public async Task Disconnect_During_A_Mutation_Returns_Unknown_Without_Replaying_Or_Stopping()
    {
        var endpoint = new FakeEndpoint();
        var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = null;

        var mutation = controller.ApplyAsync("Arena", "{}", controller.Revision, controller.DaemonInstanceId!, TestContext.Current.CancellationToken);
        await endpoint.Channel.WaitForSendAsync<ApplyConfigurationMessage>(TestContext.Current.CancellationToken);
        endpoint.Channel.Close();
        var result = await mutation;

        Assert.Equal(MutationOutcome.Unknown, result.Outcome);
        Assert.Single(endpoint.Channel.Sent.OfType<ApplyConfigurationMessage>());
        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);

        await controller.DisposeAsync();
        Assert.DoesNotContain(endpoint.Channel.Sent, message => message is StopMessage);
    }

    [Fact]
    public async Task A_Stale_Push_Is_Dropped_And_A_Fresh_Push_Is_Adopted()
    {
        var endpoint = new FakeEndpoint { InitialSequence = 5 };
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        var adopted = new List<StateSnapshot>();
        var fresh = new TaskCompletionSource<StateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            adopted.Add(snapshot);
            if (snapshot.Sequence == 6)
            {
                fresh.TrySetResult(snapshot);
            }
        };

        endpoint.Channel.Push(State("daemon-1", 4));
        endpoint.Channel.Push(State("daemon-1", 6));
        await fresh.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(6, controller.Snapshot!.Sequence);
        Assert.Equal([6], adopted.Select(snapshot => snapshot.Sequence).ToArray());
    }

    [Fact]
    public async Task A_Sequence_Gap_Triggers_One_Authoritative_Reread()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        endpoint.Channel.OnSend = message => message is GetStateMessage getState
            ? State("daemon-1", 2, getState.RequestId)
            : null;
        var reread = new TaskCompletionSource<StateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.SnapshotChanged += snapshot =>
        {
            if (snapshot.Sequence == 2)
            {
                reread.TrySetResult(snapshot);
            }
        };

        endpoint.Channel.Push(State("daemon-1", 2));
        await reread.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(2, controller.Snapshot!.Sequence);
        Assert.Equal(2, endpoint.Channel.Sent.OfType<GetStateMessage>().Count());
    }

    [Fact]
    public async Task A_Foreign_Instance_Ends_The_Session_Without_Adopting_Its_State()
    {
        var endpoint = new FakeEndpoint();
        await using var controller = new DaemonController(endpoint);
        await ConnectAsync(controller);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Disconnected += () => disconnected.TrySetResult();

        endpoint.Channel.Push(State("daemon-2", 1));
        await disconnected.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(DaemonConnectionState.Disconnected, controller.State);
        Assert.Equal(0, controller.Snapshot!.Sequence);
    }

    private static async Task ConnectAsync(DaemonController controller)
    {
        await controller.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DaemonConnectionState.Connected, controller.State);
    }

    private static StateChangedMessage State(string instanceId, long sequence, string? requestId = null, long revision = 0)
        => new(instanceId, new StateSnapshot(sequence, revision, false, true, 4242, "high", "Default", null, 0, 0, false), requestId);

    private sealed class FakeEndpoint : IDaemonEndpoint
    {
        public FakeDaemonChannel Channel { get; } = new();

        public bool Ready { get; set; } = true;

        public long InitialSequence { get; set; }

        public int EnsureCalls { get; private set; }

        public int OpenCalls { get; private set; }

        public bool LastAllowLaunch { get; private set; }

        public FakeEndpoint()
        {
            Channel.OnSend = message => message switch
            {
                HelloMessage => new HelloAckMessage(IpcContract.ProtocolVersion, true, "daemon-1", 0),
                GetStateMessage getState => State("daemon-1", InitialSequence, getState.RequestId),
                _ => null,
            };
        }

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
        {
            EnsureCalls++;
            LastAllowLaunch = allowLaunch;
            return Task.FromResult(Ready);
        }

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            OpenCalls++;
            return Task.FromResult<IDaemonChannel>(Channel);
        }
    }

    private sealed class FakeDaemonChannel : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = new();
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        public List<IpcMessage> Sent { get; } = [];

        public Func<IpcMessage, IpcMessage?>? OnSend { get; set; }

        private event Action<IpcMessage>? SentObserved;

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    throw new EndOfStreamException();
                }

                Sent.Add(message);
            }

            SentObserved?.Invoke(message);
            var response = OnSend?.Invoke(message);
            if (response is not null)
            {
                Push(response);
            }

            return Task.CompletedTask;
        }

        public Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    return Task.FromResult(_incoming.Dequeue());
                }

                if (_closed)
                {
                    return Task.FromException<IpcMessage>(new EndOfStreamException());
                }

                _waiting ??= new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                return AwaitReceiveAsync(_waiting.Task, cancellationToken);
            }
        }

        public void Push(IpcMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue(message);
                _waiting?.TrySetResult(message);
                _waiting = null;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }
        }

        public async Task<T> WaitForSendAsync<T>(CancellationToken cancellationToken) where T : IpcMessage
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(IpcMessage message)
            {
                if (message is T expected)
                {
                    completion.TrySetResult(expected);
                }
            }

            SentObserved += Handler;
            try
            {
                lock (_gate)
                {
                    if (Sent.OfType<T>().FirstOrDefault() is { } existing)
                    {
                        return existing;
                    }
                }

                return await completion.Task.WaitAsync(TestTimeout, cancellationToken);
            }
            finally
            {
                SentObserved -= Handler;
            }
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }

        private async Task<IpcMessage> AwaitReceiveAsync(Task<IpcMessage> wait, CancellationToken cancellationToken)
        {
            await wait.WaitAsync(cancellationToken);
            return await ReceiveAsync(cancellationToken);
        }
    }
}
