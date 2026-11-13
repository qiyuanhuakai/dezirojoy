using System.Diagnostics;
using System.Text.Json;
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
/// <remarks>
/// The other half under test is what "stopped" means. The daemon releases its mapping pipe as
/// part of exiting, so a stop that returns the moment the pipe is gone can still be racing an
/// elevated daemon that holds the install directory's files open - and an installer that starts
/// in that window fails its DeleteFile with access denied (error 5) and rolls back. So the
/// daemon's own PID is waited on after the pipe vanishes, and the wait is a seam the tests drive.
/// </remarks>
public sealed class UpdateProcessCoordinatorTests
{
    /// <summary>The PID the fake daemon reports about itself, the way the real one does.</summary>
    private const int DaemonPid = 4242;

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    /// <summary>Runs the production stop over a fake transport with an injected exit wait.</summary>
    private static Task<bool> StopAsync(
        FakeDaemonEndpoint endpoint,
        TimeSpan stopBudget,
        FakeExitWaiter? exit = null,
        TimeSpan? requestTimeout = null)
        => UpdateProcessCoordinator.StopDaemonAsync(
            Cancellation,
            endpoint,
            stopBudget,
            requestTimeout,
            (exit ?? new FakeExitWaiter()).WaitAsync);

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

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5));

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

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5));

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

        var stopped = await StopAsync(endpoint, TimeSpan.FromMilliseconds(300));

        Assert.False(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Daemon_That_Went_Away_During_The_Handshake_Is_Not_A_Failure()
    {
        // The pipe answered the probe and the daemon was gone by the handshake: the vanish
        // loop is what decides, and it finds nothing resident.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, AcceptHandshake = false };

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5));

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
    public async Task A_Daemon_That_Takes_The_Pipe_And_Goes_Quiet_Is_Not_A_Cancellation()
    {
        // The controller links its own request timeout into the handshake, so a daemon that
        // accepts the connection and then says nothing raises OperationCanceledException with
        // the caller's token perfectly healthy. Letting that through would tell the host the
        // user cancelled - and its message would claim nothing was stopped while the daemon is
        // still running. The honest answer is the vanish loop's: still there, so a failure.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, AnswerHello = false };

        var stopped = await StopAsync(
            endpoint,
            TimeSpan.FromMilliseconds(300),
            requestTimeout: TimeSpan.FromMilliseconds(200));

        Assert.False(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Cancelled_Stop_Still_Propagates_When_The_Handshake_Hangs()
    {
        // The filter above must not swallow the caller's own cancellation just because the
        // daemon is also misbehaving: the two are told apart by the token, as everywhere else.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, AnswerHello = false };
        using var cancellation = new CancellationTokenSource();

        var stop = UpdateProcessCoordinator.StopDaemonAsync(
            cancellation.Token,
            endpoint,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
    }

    [Fact]
    public async Task A_Frame_Protocol_Failure_Is_An_Honest_False_Not_An_Escape()
    {
        // FrameProtocolException derives from Exception, not IOException: outside the filter
        // it would reach the host unhandled and leave the phase wedged at Installing.
        var endpoint = new FakeDaemonEndpoint
        {
            IsAnswering = true,
            HandshakeFailure = new FrameProtocolException("frame payload of 0 bytes"),
        };

        var stopped = await StopAsync(endpoint, TimeSpan.FromMilliseconds(300));

        Assert.False(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task A_Malformed_Payload_Is_An_Honest_False_Not_An_Escape()
    {
        // The other protocol exception the channel can raise: a payload that does not
        // deserialize is a fact about the peer, not a crash of the update flow.
        var endpoint = new FakeDaemonEndpoint
        {
            IsAnswering = true,
            HandshakeFailure = new JsonException("the activation message deserialized to null"),
        };

        var stopped = await StopAsync(endpoint, TimeSpan.FromMilliseconds(300));

        Assert.False(stopped, endpoint.Diagnostics);
        Assert.Equal(0, endpoint.LaunchRequests);
    }

    [Fact]
    public async Task The_Daemon_Process_Is_Waited_On_Once_Its_Pipe_Is_Gone()
    {
        // The pipe is the daemon's own and it closes it while exiting: a daemon that is still
        // unwinding behind that holds the install directory's files open, and an installer that
        // starts now fails its DeleteFile with access denied. So the process the daemon named in
        // its own snapshot is waited on before the stop is reported.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true };
        var exit = new FakeExitWaiter();

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5), exit);

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Equal(DaemonPid, Assert.Single(exit.WaitedPids));
    }

    [Fact]
    public async Task A_Daemon_Process_That_Will_Not_Exit_Is_An_Honest_Failure()
    {
        // The pipe is gone but the process is not: installing now would race a live daemon for
        // the files, so the stop fails instead of pretending the daemon is gone.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true };
        var exit = new FakeExitWaiter { Next = false };

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5), exit);

        Assert.False(stopped, endpoint.Diagnostics);
        Assert.Equal(DaemonPid, Assert.Single(exit.WaitedPids));
    }

    [Fact]
    public async Task A_Daemon_That_Named_No_Process_Still_Counts_As_Stopped()
    {
        // A daemon that was already gone when the handshake ran named no process: its pipe is
        // gone, which is the same evidence a vanished process gives, and the bounded grace
        // stands in for the wait that could not be aimed at anything.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, AcceptHandshake = false };
        var exit = new FakeExitWaiter();

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5), exit);

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Empty(exit.WaitedPids);
    }

    [Fact]
    public async Task A_Snapshot_That_Names_No_Injector_Is_Waited_On_For_Nothing()
    {
        // PID 0 is not a process: waiting on it would be waiting on nothing at all, so the
        // daemon is unnamed and the grace covers it.
        var endpoint = new FakeDaemonEndpoint { IsAnswering = true, InjectorPid = 0 };
        var exit = new FakeExitWaiter();

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5), exit);

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Empty(exit.WaitedPids);
    }

    [Fact]
    public async Task A_Daemon_That_Reports_This_Process_Is_Not_Waited_On()
    {
        // Were a daemon ever to be served in-process, its reported PID would be this GUI's own:
        // waiting for that to exit would fail every stop, because the GUI exits only once the
        // install it is waiting for has already been reported.
        var endpoint = new FakeDaemonEndpoint
        {
            IsAnswering = true,
            InjectorPid = Environment.ProcessId,
        };
        var exit = new FakeExitWaiter();

        var stopped = await StopAsync(endpoint, TimeSpan.FromSeconds(5), exit);

        Assert.True(stopped, endpoint.Diagnostics);
        Assert.Empty(exit.WaitedPids);
    }

    [Fact]
    public async Task The_Production_Endpoint_Answers_The_Same_Way_Whatever_It_Is_Asked_About_Launching()
    {
        // A launch-capable endpoint answers the two differently: with a launch allowed it
        // starts a daemon (UAC on Windows) and waits for it. This one must not, whatever it is
        // asked for - and must answer well inside the launch budget either way.
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

        /// <summary>
        /// The PID the daemon's snapshot reports about itself. Zero models a daemon that names
        /// no injector at all, which is not a process to wait on.
        /// </summary>
        public int InjectorPid { get; init; } = DaemonPid;

        /// <summary>Whether the daemon leaves after the shutdown is acknowledged.</summary>
        public bool ExitsOnShutdown { get; init; } = true;

        /// <summary>Whether the shutdown mutation is acknowledged at all.</summary>
        public bool AcknowledgeShutdown { get; init; } = true;

        /// <summary>Whether the daemon accepts the shutdown it acknowledges.</summary>
        public bool AcceptsShutdown { get; init; } = true;

        /// <summary>Whether the handshake is accepted; a refusal also retires the daemon.</summary>
        public bool AcceptHandshake { get; init; } = true;

        /// <summary>Whether the daemon answers the handshake at all.</summary>
        public bool AnswerHello { get; init; } = true;

        /// <summary>
        /// What the channel raises instead of answering the handshake - a protocol failure the
        /// coordinator has to classify rather than let escape.
        /// </summary>
        public Exception? HandshakeFailure { get; init; }

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
        private int _receives;

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
                        new StateSnapshot(0, 0, false, true, endpoint.InjectorPid, "high", "Default", null, 0, 0, false),
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

        public async Task<IpcMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (_receives == 0)
            {
                // The first read of a connection is the handshake answer: this is where a
                // daemon that took the pipe and then went quiet, or one that speaks a protocol
                // this channel cannot read, shows itself.
                _receives++;
                if (endpoint.HandshakeFailure is { } failure)
                {
                    throw failure;
                }

                if (!endpoint.AnswerHello)
                {
                    // The controller's own request budget is what eventually gives up here, so
                    // the token it hands in is not the caller's.
                    await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                }
            }

            IpcMessage? queued = null;
            Task<IpcMessage>? pending = null;
            lock (_gate)
            {
                if (_incoming.Count > 0)
                {
                    queued = _incoming.Dequeue();
                }
                else if (_closed)
                {
                    throw new EndOfStreamException();
                }
                else
                {
                    _waiting ??= new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = _waiting.Task;
                }
            }

            return queued ?? await pending!.WaitAsync(cancellationToken);
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

    /// <summary>
    /// A daemon-exit wait that decides without a real process. The PIDs it was asked about are
    /// recorded, so the tests assert the stop waited on the right process; <see cref="Next"/>
    /// models a daemon whose process will not exit.
    /// </summary>
    private sealed class FakeExitWaiter
    {
        /// <summary>What the next wait answers; false models a process that will not exit.</summary>
        public bool Next { get; set; } = true;

        /// <summary>The PIDs waited on, in order.</summary>
        public List<int> WaitedPids { get; } = [];

        public Task<bool> WaitAsync(int processId, TimeSpan budget, CancellationToken cancellationToken)
        {
            WaitedPids.Add(processId);
            return Task.FromResult(Next);
        }
    }
}
