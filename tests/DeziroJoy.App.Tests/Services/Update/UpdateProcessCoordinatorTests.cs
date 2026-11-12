using System.Diagnostics;
using DeziroJoy.App.Ipc;
using DeziroJoy.App.Services;
using Xunit;

namespace DeziroJoy.App.Tests.Services.Update;

/// <summary>
/// The production daemon stop, over a fake daemon transport. The point of these tests is the
/// Windows-only failure the Linux test run cannot show: a liveness probe that is allowed to
/// launch a daemon turns "nothing to stop" into a UAC prompt, and turns the post-shutdown
/// confirmation into a resurrection that fails the stop. The fake records every launch
/// request, so the regression is asserted rather than reasoned about.
/// </summary>
public sealed class UpdateProcessCoordinatorTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Daemon_That_Is_Not_Running_Is_Not_Started_Just_To_Be_Stopped()
    {
        var endpoint = new FakeDaemonEndpoint { IsAnswering = false };

        var stopped = await UpdateProcessCoordinator.StopDaemonAsync(Cancellation, endpoint);

        Assert.True(stopped, "a daemon that is not there needs no stopping");
        Assert.Equal(0, endpoint.LaunchRequests);
        Assert.Equal(0, endpoint.OpenCalls);
        Assert.Empty(endpoint.Sent.OfType<ShutdownDaemonMessage>());
    }

    [Fact]
    public async Task A_Daemon_That_Acknowledged_The_Shutdown_Stops_The_Wait_Without_Starting_A_New_One()
    {
        // The normal successful stop: the probe finds the daemon, the shutdown is accepted,
        // the daemon exits - and the confirmation must not resurrect it, because a fresh
        // daemon answering every probe would exhaust the budget and fail the stop.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true };

        var stopped = await UpdateProcessCoordinator.StopDaemonAsync(
            Cancellation,
            endpoint,
            TimeSpan.FromSeconds(5));

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Single(endpoint.Sent.OfType<ShutdownDaemonMessage>());
        Assert.Equal(1, endpoint.OpenCalls);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Daemon_That_Exits_Without_Answering_Still_Counts_As_Stopped()
    {
        // The shutdown request is lost - the daemon exits before its ack, so the pipe drops:
        // the goal state, nothing resident, holds, and the stop succeeds rather than
        // reporting a failure.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, AcknowledgeShutdown = false };

        var stopped = await UpdateProcessCoordinator.StopDaemonAsync(
            Cancellation,
            endpoint,
            TimeSpan.FromSeconds(5));

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Daemon_That_Refuses_To_Stop_Is_An_Honest_Failure()
    {
        var endpoint = new FakeDaemonEndpoint
        {
            IsAnswering = true,
            AcceptsShutdown = false,
            ExitsOnShutdown = false,
        };

        var stopped = await UpdateProcessCoordinator.StopDaemonAsync(
            Cancellation,
            endpoint,
            TimeSpan.FromMilliseconds(300));

        Assert.False(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Daemon_That_Went_Away_During_The_Handshake_Is_Not_A_Failure()
    {
        // The pipe answered the probe and the daemon was gone by the handshake: the vanish
        // loop is what decides, and it finds nothing resident.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, AcceptHandshake = false };

        var stopped = await UpdateProcessCoordinator.StopDaemonAsync(
            Cancellation,
            endpoint,
            TimeSpan.FromSeconds(5));

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Cancelled_Stop_Propagates_Instead_Of_Reporting_Success()
    {
        // "No answer" must never be mistaken for "the daemon is gone" when the caller gave up.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, ExitsOnShutdown = false };
        using var cancellation = new CancellationTokenSource();

        var stop = UpdateProcessCoordinator.StopDaemonAsync(
            cancellation.Token,
            endpoint,
            TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
    }

    [Fact]
    public async Task The_Production_Endpoint_Answers_The_Same_Way_Whatever_It_Is_Asked_About_Launching()
    {
        // A launch-capable endpoint answers the two differently: with a launch allowed it
        // starts a daemon (UAC on Windows) and waits for it. This one must not, whatever it
        // is asked for - and must answer well inside the launch budget either way.
        var endpoint = new ProbeOnlyDaemonEndpoint();

        var withLaunch = await ProbeAsync(endpoint, allowLaunch: true);
        var withoutLaunch = await ProbeAsync(endpoint, allowLaunch: false);

        Assert.Equal(withoutLaunch.Ready, withLaunch.Ready);
        Assert.True(
            withLaunch.Elapsed < TimeSpan.FromSeconds(5),
            $"a probe that may launch would have waited for one: {withLaunch.Elapsed}");
        Assert.True(
            withoutLaunch.Elapsed < TimeSpan.FromSeconds(5),
            $"a probe that may launch would have waited for one: {withoutLaunch.Elapsed}");
    }

    private static async Task<(bool Ready, TimeSpan Elapsed)> ProbeAsync(
        ProbeOnlyDaemonEndpoint endpoint,
        bool allowLaunch)
    {
        var elapsed = Stopwatch.StartNew();
        var ready = await endpoint.EnsureReadyAsync(allowLaunch, Cancellation);
        return (ready, elapsed.Elapsed);
    }

    /// <summary>
    /// A daemon transport that records whether it was asked to launch. The launch branch
    /// mirrors the production endpoint's: an absent pipe plus <c>allowLaunch</c> starts a
    /// daemon, so a coordinator that ever probes in that state fails these tests.
    /// </summary>
    private sealed class FakeDaemonEndpoint : IDaemonEndpoint, IDisposable
    {
        private readonly object _gate = new();
        private readonly FakeDaemonChannel _channel;
        private bool _answering;

        public FakeDaemonEndpoint()
            => _channel = new FakeDaemonChannel(this);

        /// <summary>Whether the daemon answers its pipe right now.</summary>
        public bool IsAnswering
        {
            get => Volatile.Read(ref _answering);
            set => Volatile.Write(ref _answering, value);
        }

        /// <summary>Whether the daemon leaves after the shutdown is acknowledged.</summary>
        public bool ExitsOnShutdown { get; init; } = true;

        /// <summary>Whether the shutdown mutation is acknowledged at all.</summary>
        public bool AcknowledgeShutdown { get; init; } = true;

        /// <summary>Whether the daemon accepts the shutdown it acknowledges.</summary>
        public bool AcceptsShutdown { get; init; } = true;

        /// <summary>Whether the handshake is accepted; a refusal also retires the daemon.</summary>
        public bool AcceptHandshake { get; init; } = true;

        /// <summary>How many times a launch would have been started.</summary>
        public int LaunchRequests { get; private set; }

        public int OpenCalls { get; private set; }

        public List<IpcMessage> Sent => _channel.Sent;

        public string Diagnostics
            => $"answering={IsAnswering}, launches={LaunchRequests}, opens={OpenCalls}, " +
                $"shutdowns={Sent.OfType<ShutdownDaemonMessage>().Count()}";

        public Task<bool> EnsureReadyAsync(bool allowLaunch, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (IsAnswering)
                {
                    return Task.FromResult(true);
                }

                if (!allowLaunch)
                {
                    return Task.FromResult(false);
                }

                // The production endpoint starts a daemon here - a UAC prompt on Windows.
                LaunchRequests++;
                IsAnswering = true;
                return Task.FromResult(true);
            }
        }

        public Task<IDaemonChannel> OpenChannelAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                OpenCalls++;
                if (!IsAnswering)
                {
                    throw new IOException("the daemon is gone");
                }

                return Task.FromResult<IDaemonChannel>(_channel);
            }
        }

        /// <summary>Called by the channel when the daemon goes away.</summary>
        public void Retire()
        {
            if (ExitsOnShutdown)
            {
                IsAnswering = false;
            }
        }

        public void Dispose() => _channel.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// One daemon connection: the handshake, the state pull and the shutdown mutation, with
    /// the daemon's own lifecycle applied as it answers - a refused handshake retires it,
    /// because that is the daemon that went away mid-connect.
    /// </summary>
    private sealed class FakeDaemonChannel(FakeDaemonEndpoint endpoint) : IDaemonChannel
    {
        private readonly object _gate = new();
        private readonly Queue<IpcMessage> _incoming = [];
        private TaskCompletionSource<IpcMessage>? _waiting;
        private bool _closed;

        public List<IpcMessage> Sent { get; } = [];

        public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
        {
            IpcMessage? response;
            switch (message)
            {
                case HelloMessage:
                    if (endpoint.AcceptHandshake)
                    {
                        response = new HelloAckMessage(IpcContract.ProtocolVersion, true, "daemon-1", 0);
                    }
                    else
                    {
                        endpoint.Retire();
                        response = new HelloAckMessage(
                            IpcContract.ProtocolVersion,
                            false,
                            "daemon-1",
                            0,
                            "the daemon went away");
                    }

                    break;

                case GetStateMessage getState:
                    response = new StateChangedMessage(
                        "daemon-1",
                        new StateSnapshot(0, 0, false, true, 4242, "high", "Default", null, 0, 0, false),
                        getState.RequestId);
                    break;

                case ShutdownDaemonMessage shutdown:
                    if (endpoint.ExitsOnShutdown)
                    {
                        endpoint.Retire();
                    }

                    if (endpoint.AcknowledgeShutdown)
                    {
                        response = new MutationAckMessage(
                            shutdown.RequestId,
                            endpoint.AcceptsShutdown,
                            0,
                            0);
                    }
                    else
                    {
                        // The daemon exits before it can answer: the pipe drops with no ack.
                        response = null;
                        Close();
                    }

                    break;

                default:
                    response = null;
                    break;
            }

            lock (_gate)
            {
                Sent.Add(message);
            }

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
                return _waiting.Task.WaitAsync(cancellationToken);
            }
        }

        private void Push(IpcMessage message)
        {
            lock (_gate)
            {
                _incoming.Enqueue(message);
                _waiting?.TrySetResult(message);
                _waiting = null;
            }
        }

        private void Close()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _closed = true;
                _waiting?.TrySetException(new EndOfStreamException());
                _waiting = null;
            }

            return ValueTask.CompletedTask;
        }
    }
}
